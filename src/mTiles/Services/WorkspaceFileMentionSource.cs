using System.Diagnostics;

namespace mTiles.Services;

/// <summary>
/// The workspace's own files and folders, read once and kept for a while.
/// </summary>
/// <remarks>
/// <para><b>Git decides what exists, three rules decide what is shown.</b> The corpus is
/// <c>ls-files --cached --recurse-submodules</c> plus <c>--others --exclude-standard</c> — the tracked
/// files and the untracked ones the repository does not ignore, which is the user's own
/// <c>.gitignore</c> rather than a list this application would have to keep in step with. On top of
/// that come the three rules applied to every candidate, git's included:
/// <see cref="ExcludedDirectories"/>, the workspace's <c>.ignore</c>/<c>.rgignore</c>
/// (<see cref="FileSuggestionIgnore"/>), and <see cref="MaxPaths"/>.</para>
/// <para><c>-z</c> rather than <c>-c core.quotepath=false</c>: it is the same fix for the
/// same problem — git quotes any path that is not plain ASCII, so a repository with Polish file names
/// would suggest paths that do not exist — and it also survives a newline in a file name, which
/// splitting on lines cannot.</para>
/// <para>Outside a repository there is no such list, so the walk skips what starts with a dot — which
/// is <c>.git</c> and the editors' own directories — along with the same excluded names, and stops at
/// the same ceiling. Tools of this kind fall back to ripgrep here; this walks, because ripgrep is a
/// program a user may not have and a workspace that offers nothing is worse than one that walks.</para>
/// <para>Folders are offered beside files, each ending in <c>/</c>. A mention is often a place rather
/// than a file — <c>@src/mTiles/Services/</c> says where to work — and having them in the list is also
/// what lets Tab walk down the tree one folder at a time.</para>
/// </remarks>
public sealed class WorkspaceFileMentionSource(
    string workingDirectory, string gitPath = "git") : IFileMentionSource
{
    /// <summary>
    /// How long a reading stands before the tree is read again.
    /// <para>The refresh throttle a listing like this carries. Short, because it is a floor rather
    /// than the usual interval: <see cref="GitIndexStamp"/> reads the index's timestamp on every ask and forces
    /// a re-read the moment git touches it, so a commit or a checkout in the terminal tile next door
    /// shows up at once and a quiet minute of typing still costs one reading.</para>
    /// </summary>
    private static readonly TimeSpan FreshFor = TimeSpan.FromSeconds(5);

    /// <summary>The ceiling on how many paths are held at once.</summary>
    private const int MaxPaths = 200_000;

    /// <summary>How long a reading of the tree may run before it is given up on.</summary>
    /// <remarks>
    /// <para>Ten seconds: long enough for a large working tree on a cold cache, short enough that a
    /// hung git is one abandoned process rather than one per refresh for the rest of the session.</para>
    /// <para><b>It bounds the tracked half as well, and that half needed it more.</b> The untracked
    /// listing is started without being awaited, so a hang there costs a process and a pool task. The
    /// tracked one is awaited <em>inside <see cref="_gate"/></em> — so a <c>ls-files --cached
    /// --recurse-submodules</c> that never returns, over a large submodule or a network share or under
    /// contention for <c>index.lock</c>, holds the semaphore for good. Every later <c>@</c> in the tile
    /// then queues behind it on a wait that had no token of its own and never comes back: the feature
    /// stops working for the rest of the session, with nothing on screen and nothing in the log. The
    /// walk is covered for the same reason — it runs inside the gate too.</para>
    /// </remarks>
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Directory names nothing under is offered, whatever git says.
    /// </summary>
    /// <remarks>
    /// <para>The excluded-directory list a popup like this carries, applied where such a list belongs —
    /// to <b>every</b> candidate, git's output included, not only to the walk. It is the one place this
    /// overrules the user's own <c>.gitignore</c>, and it earns that: a repository that commits its
    /// <c>dist/</c> has said something about distributing it and nothing at all about wanting eight
    /// hundred bundled files in a popup.</para>
    /// <para>The last four are ours. Lists like this are written for JavaScript and Python
    /// repositories and never had to name the .NET, Java and Rust build outputs, and a tile in this
    /// application offering <c>bin/Debug/net10.0/mTiles.dll</c> is the failure this list exists to
    /// prevent. The cost is stated rather than hidden: a repository that deliberately tracks
    /// something under <c>bin/</c> — and some do — will not have it offered here.</para>
    /// <para>Names, not paths, because a monorepo has one of each per package; matched against every
    /// directory above the file, because that is what "nothing under" means.</para>
    /// </remarks>
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".hg", ".svn", "node_modules", ".pnpm-store", ".yarn", ".next", ".nuxt",
        ".svelte-kit", ".turbo", "dist", "build", "out", "output", "coverage", ".cache",
        "__pycache__", ".pytest_cache", ".mypy_cache", ".ruff_cache", ".tox", "venv", ".venv",
        "__pypackages__", "site-packages", "wandb", "lightning_logs", "mlruns",
        "bin", "obj", "packages", "target",
    };

    /// <summary>
    /// The agent's own project configuration, offered even where git hides it.
    /// </summary>
    /// <remarks>
    /// <para>Offered the way a popup like this offers it, pointed at the agent this application
    /// actually drives. It adds the markdown under its own config directories to the corpus outside
    /// the git listing entirely, and the reason is visible in almost any repository's
    /// <c>.gitignore</c>, which routinely carries
    /// <c>/.claude</c> and <c>CLAUDE.md</c>: the files that tell the agent how to work are routinely
    /// the ones the repository declines to track, and they are exactly what somebody writing a goal
    /// wants to point at.</para>
    /// <para>Markdown only, one level deep, and only these directories — a rule narrow enough that it
    /// cannot quietly become a second file listing that ignores <c>.gitignore</c>.</para>
    /// </remarks>
    private static readonly string[] AgentConfigDirectories =
        [".claude/commands", ".claude/agents", ".claude/skills", ".claude/output-styles"];

    /// <summary>The agent's own instruction files, for the same reason.</summary>
    private static readonly string[] AgentConfigFiles = ["CLAUDE.md", "AGENTS.md", "CLAUDE.local.md"];

    /// <summary>How deep the fallback walk goes. Deep enough for any source tree, and a floor under a
    /// directory symlinked into itself.</summary>
    private const int MaxDepth = 24;

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// One reading of the tree, and everything that says whether it still stands.
    /// </summary>
    /// <remarks>
    /// <para><b>One field, because there is one fast path and it does not take the gate.</b> These
    /// began as three fields — the paths, when they were read, and what <c>.git/index</c> looked like
    /// then — written under the gate and read outside it. A reference assignment is atomic and a
    /// <c>long?</c> is not, so the unlocked reader could see a new timestamp beside an old stamp and
    /// call a stale reading fresh, or the other way about. Held together they are replaced in one
    /// write, and whoever reads gets one whole answer or the one before it.</para>
    /// <para><see cref="Generation"/> is the cache generation: the untracked files
    /// arrive after the reading that asked for them has been handed out, and possibly after it has been
    /// replaced — merging them into a newer reading would put back files a refresh had just established
    /// were gone.</para>
    /// </remarks>
    private sealed record Reading(
        IReadOnlyList<string> Paths, long ReadAt, DateTime? IndexStamp, int Generation);

    /// <summary>The reading in force, or null before the first one.</summary>
    /// <remarks>Read and written through <see cref="Volatile"/>: the background merge runs on a
    /// thread-pool thread and the fast path on the UI thread, and neither is going to be helped by a
    /// cached register.</remarks>
    private Reading? _reading;

    private int _generation;

    public async Task<IReadOnlyList<string>> GetPathsAsync(CancellationToken ct = default)
    {
        var stamp = GitIndexStamp();
        if (Volatile.Read(ref _reading) is { } current && IsFresh(current, stamp)) return current.Paths;

        await _gate.WaitAsync(ct);
        try
        {
            // Asked again inside the gate: several boxes share one source, and whoever was queued
            // behind the read wants its answer rather than another read of the same tree.
            if (Volatile.Read(ref _reading) is { } fresh && IsFresh(fresh, stamp)) return fresh.Paths;

            var generation = ++_generation;

            // Bounded, with the caller's token folded in rather than replaced: a tile being disposed
            // still cancels, and a git that never answers still lets the gate go.
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
            bounded.CancelAfter(ReadTimeout);

            var paths = await ReadAsync(generation, bounded.Token);

            Volatile.Write(ref _reading,
                new Reading(paths, Environment.TickCount64, stamp, generation));

            return paths;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// A reading stands while it is inside <see cref="FreshFor"/> <em>and</em> git has not touched its
    /// index since.
    /// </summary>
    /// <remarks>
    /// The second half is what makes the first half short enough to be cheap and long enough to be
    /// useful. A branch switch or a commit rewrites <c>.git/index</c>, and a list that has been stale
    /// since then is stale in the way the user will notice — the file they just created is missing.
    /// A null stamp (no repository, or the file cannot be read) says nothing either way, and then the
    /// clock is all there is.
    /// </remarks>
    private static bool IsFresh(Reading reading, DateTime? stamp) =>
        Environment.TickCount64 - reading.ReadAt < (long)FreshFor.TotalMilliseconds
        && stamp == reading.IndexStamp;

    /// <summary>When <c>.git/index</c> was last written, or null when there is no reading it.</summary>
    /// <remarks>The file rather than <c>git status</c>: this runs on every keystroke that opens a
    /// mention, and one stat is the most such a check may cost.</remarks>
    private DateTime? GitIndexStamp()
    {
        try
        {
            var index = Path.Combine(workingDirectory, ".git", "index");
            return File.Exists(index) ? File.GetLastWriteTimeUtc(index) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The list, from git where there is one and from the walk where there is not.</summary>
    /// <remarks>
    /// The walk goes to the thread pool because everything above it runs on the UI thread: the popup
    /// asks while the user is typing, and <c>await</c> here resumes on Avalonia's dispatcher, so a
    /// synchronous walk of a large tree is the keyboard stopping mid-word — measured at a fifth of a
    /// second on this repository, and repeated whenever the reading goes stale. Git's half already
    /// waits on a process rather than on this thread.
    /// </remarks>
    private async Task<IReadOnlyList<string>> ReadAsync(int generation, CancellationToken ct)
    {
        // Everything here is on the thread pool, and the walk was only the most obvious part of it.
        // The popup asks while the user is typing and every `await` below resumes on Avalonia's
        // dispatcher, so what git waits for costs nothing and what happens to its answer costs the
        // keyboard: splitting up to two hundred thousand paths, running every ignore rule's regular
        // expression over each of them, building the directory set and sorting it. Reading the ignore
        // files is a synchronous `File.ReadAllLines` on the same thread. Git waiting off-thread while
        // its output is parsed on the UI thread is the half-measure this closes.
        var ignore = await Task.Run(() => FileSuggestionIgnore.Read(workingDirectory), ct);

        var tracked = await ReadTrackedAsync(ct);
        if (tracked is null)
            return await Task.Run(() => Assemble(Walk(ignore, ct), ignore), ct);

        StartUntrackedRead(ignore, generation);
        return await Task.Run(() => Assemble(Keep(Split(tracked), ignore), ignore), ct);
    }

    /// <summary>What git tracks, or null when git could not be asked at all.</summary>
    /// <remarks>
    /// <para>Null rather than an empty list, because the two mean opposite things: an empty list is
    /// an empty repository and would leave the fallback walking a tree git had already answered for.
    /// </para>
    /// <para><c>--recurse-submodules</c> so a superproject offers the files inside its submodules,
    /// which are the ones somebody working in a superproject is usually pointing at. It is accepted
    /// only alongside <c>--cached</c>, which is why the untracked half is a second command.</para>
    /// <para><c>--no-optional-locks</c> for the reason every command in <see cref="WorktreeReader"/>
    /// carries it, and more sharply here: this runs unprompted while the user is typing. With
    /// <c>core.untrackedCache</c> or fsmonitor enabled, <c>ls-files</c> refreshes the index on the way
    /// past and takes <c>index.lock</c> — which is how a rebase in the terminal tile next door fails
    /// with <c>index.lock: File exists</c> because a popup was deciding what to offer.</para>
    /// </remarks>
    private async Task<string?> ReadTrackedAsync(CancellationToken ct)
    {
        try
        {
            // The raw output, filtered by the caller on the thread pool. Returning a filtered list from
            // here would put that work on whatever thread resumed the await, which is the dispatcher.
            return await Git().RunAsync(
                $"--no-optional-locks ls-files --cached --recurse-submodules -z {Excludes}", ct);
        }
        catch (OperationCanceledException)
        {
            // The caller went away. Not a reason to fall back to walking the whole tree instead.
            throw;
        }
        catch (Exception ex)
        {
            Trace.TraceInformation($"Listing files with git failed, walking the directory: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Asks git for the untracked files, and merges them in when they arrive.
    /// </summary>
    /// <remarks>
    /// <para><b>Not awaited</b>, which is the usual arrangement and for the usual reason: the tracked list
    /// is the answer to nearly every mention, and making the first <c>@</c> of a session wait for a
    /// second walk of the working tree spends the user's time on the half they are least likely to
    /// want. The untracked files land in the next reading, or in this one if they beat the next
    /// keystroke.</para>
    /// <para>The generation is what makes that safe. A refresh that has already replaced this reading
    /// has established what is there now, and merging a slower answer to an older question on top of it
    /// would put back files that refresh had just found were gone.</para>
    /// <para>Its failure is not the tracked read's: a repository mid-rebase can refuse this while
    /// answering that perfectly well, and half a list is worth having.</para>
    /// </remarks>
    private void StartUntrackedRead(FileSuggestionIgnore ignore, int generation) => _ = Task.Run(async () =>
    {
        // A budget of its own, because nothing else bounds this one. It is started and not awaited, so
        // no caller's token reaches it and no caller is left waiting to notice it never came back — a
        // `git ls-files --others` that hangs on a rebase or a slow network share would otherwise keep a
        // process and a pool task for the life of the application, and a fresh one joins them every
        // five seconds of typing. Tools of this kind bound the same call the same way, for the same
        // reason and at the same length.
        using var bound = new CancellationTokenSource(ReadTimeout);

        try
        {
            var output = await Git().RunAsync(
                $"--no-optional-locks ls-files --others --exclude-standard -z {Excludes}", bound.Token);

            var untracked = Keep(Split(output), ignore);
            if (untracked.Count == 0) return;

            await _gate.WaitAsync(bound.Token);
            try
            {
                // The reading this answers may have been replaced while git was running, and merging
                // into a newer one would put back files that refresh had just found were gone.
                if (Volatile.Read(ref _reading) is not { } current || current.Generation != generation)
                    return;

                // Capped on the *merged* list, not on each half. `Keep` had already trimmed the
                // tracked files to the ceiling and the untracked ones to the ceiling again, so joining
                // them could carry twice it — and the ceiling is what `FileMentionCorpus` sizes its
                // cost against. Re-running the two filters over the tracked half is a few milliseconds
                // once per reading, against a rule that is stated in three places and has to be true.
                var merged = Keep(
                    [..current.Paths.Where(p => !p.EndsWith('/')), ..untracked], ignore);

                Volatile.Write(ref _reading, current with { Paths = Assemble(merged, ignore) });
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception ex)
        {
            // An unhandled exception on a thread-pool thread ends the process, and no list of
            // untracked files is worth the application.
            Trace.TraceInformation($"Listing untracked files failed: {ex.Message}");
        }
    });

    private GitCommandRunner Git() => new(workingDirectory, gitPath);

    /// <summary>This application's own state directory, kept out of both git listings.</summary>
    /// <remarks>It holds this tile's transcript and the images pasted into it, exactly as
    /// <see cref="WorktreeReader"/> excludes it for the same reason. Git hides it only where the Git
    /// tile has put it in <c>.gitignore</c>, which is a setting and a tile a workspace need not
    /// have.</remarks>
    private static string Excludes =>
        $"\":(exclude){WorkspacePaths.DirName}\" \":(exclude){WorkspacePaths.LegacyDirName}\"";

    private static IEnumerable<string> Split(string output) =>
        output.Split('\0', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// The candidates that survive the three rules, in the order they came.
    /// </summary>
    /// <remarks>The three rules in order: the ignore files, then the excluded
    /// directories, then the ceiling — applied here to git's output as much as to the walk's, which is
    /// the whole point of the arrangement.</remarks>
    private static List<string> Keep(IEnumerable<string> candidates, FileSuggestionIgnore ignore)
    {
        var kept = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var candidate in candidates)
        {
            if (kept.Count >= MaxPaths) break;
            if (IsExcluded(candidate) || ignore.Ignores(candidate)) continue;

            // Deduplicated here rather than at each caller, because every candidate passes through
            // this and there are three ways one arrives twice. The one that happens: the agent's own
            // configuration is added outside the git listing, so a `CLAUDE.md` that is untracked and
            // not ignored is added here *and* listed by `ls-files --others` — and a repository before
            // its first commit has all of them in that state, so the handful of paths this feature
            // exists to surface each take two of the fifteen rows a popup has.
            if (!seen.Add(candidate)) continue;

            kept.Add(candidate);
        }

        return kept;
    }

    /// <summary>Whether any directory above this path is one nothing is offered from.</summary>
    /// <remarks>Every segment but the last, because the last is the file's own name and a file called
    /// <c>build</c> is a file, and an exclusion list of this kind draws the line in the same place.</remarks>
    private static bool IsExcluded(string path)
    {
        var segments = path.Split('/');

        for (var i = 0; i < segments.Length - 1; i++)
            if (ExcludedDirectories.Contains(segments[i]))
                return true;

        return false;
    }

    /// <summary>
    /// The finished corpus: every folder, then the agent's own configuration, then the files.
    /// </summary>
    /// <remarks>
    /// <para>Folders before files so that among rows the scorer likes equally the place sorts above
    /// what is inside it — the sort is stable and ties fall back to this order.</para>
    /// <para>The configuration goes in front of the files it is grouped with, and <b>behind the
    /// folders</b>, which is what the code does and what the summary above used to contradict. The
    /// claim that went with it — that these paths would otherwise be "buried in thousands" — was not
    /// true either: this order is only ever a tie-break in <see cref="FileMentionMatcher"/>, never a
    /// filter, so a configuration file is found on how well it matches and not on where it sits here.
    /// What the position buys is one row's worth of precedence among equals, which is all it can
    /// buy.</para>
    /// </remarks>
    private List<string> Assemble(List<string> files, FileSuggestionIgnore ignore)
    {
        var all = new List<string>(files);

        foreach (var extra in AgentConfig())
            if (!IsExcluded(extra) && !ignore.Ignores(extra) && !all.Contains(extra, StringComparer.Ordinal))
                all.Insert(0, extra);

        // `MaxPaths` is a ceiling on the corpus, not on one of its halves, and it is what
        // `FileMentionCorpus` prices its cost against — so it has to hold on what leaves here. `Keep`
        // had already capped the files; the folders are derived on top of them and could carry the
        // total to roughly twice the number the measurements were taken at.
        //
        // **The files keep their places and the folders take what is left**, which is the one detail a
        // `Take` on the finished list would get backwards: folders are emitted first, so trimming the
        // tail would throw away the actual files and keep every folder row. A folder is a way into a
        // place the user can also reach by typing; a file that is missing cannot be mentioned at all.
        var room = Math.Max(0, MaxPaths - all.Count);

        return [..Directories(all).Take(room), ..all];
    }

    /// <summary>
    /// Every folder above a file, each ending in <c>/</c>.
    /// </summary>
    /// <remarks>
    /// Derived rather than listed: git has no command for the directories, and a second walk to find
    /// them would answer for a tree the file list has already described. The trailing slash is what
    /// tells the two apart on screen and in the text, and it is also the boundary character the scorer
    /// pays a bonus for, so typing the next folder's first letter after it scores as the word start it
    /// is.
    /// </remarks>
    private static IEnumerable<string> Directories(List<string> files)
    {
        var directories = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in files)
            for (var cut = file.IndexOf('/'); cut >= 0; cut = file.IndexOf('/', cut + 1))
                directories.Add(file[..(cut + 1)]);

        return directories.OrderBy(d => d, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The agent's own configuration files that are actually there.</summary>
    private IEnumerable<string> AgentConfig()
    {
        foreach (var file in AgentConfigFiles)
            if (Exists(() => File.Exists(Path.Combine(workingDirectory, file))))
                yield return file;

        foreach (var directory in AgentConfigDirectories)
        {
            var full = Path.Combine(workingDirectory, directory.Replace('/', Path.DirectorySeparatorChar));
            if (!Exists(() => Directory.Exists(full))) continue;

            foreach (var markdown in Entries(() => Directory.EnumerateFiles(full, "*.md")))
                yield return $"{directory}/{Path.GetFileName(markdown)}";
        }
    }

    private static bool Exists(Func<bool> check)
    {
        try
        {
            return check();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private List<string> Walk(FileSuggestionIgnore ignore, CancellationToken ct)
    {
        var found = new List<string>();
        Collect(workingDirectory, prefix: "", found, depth: 0, ct);
        found.Sort(StringComparer.OrdinalIgnoreCase);

        return Keep(found, ignore);
    }

    private static void Collect(
        string directory, string prefix, List<string> into, int depth, CancellationToken ct)
    {
        if (into.Count >= MaxPaths || depth > MaxDepth) return;
        ct.ThrowIfCancellationRequested();

        foreach (var file in Entries(() => Directory.EnumerateFiles(directory)))
        {
            if (into.Count >= MaxPaths) return;

            var name = Path.GetFileName(file);
            if (!IsHidden(name)) into.Add(prefix + name);
        }

        foreach (var subdirectory in Entries(() => Directory.EnumerateDirectories(directory)))
        {
            var name = Path.GetFileName(subdirectory);
            if (IsSkipped(name)) continue;

            Collect(subdirectory, $"{prefix}{name}/", into, depth + 1, ct);
        }
    }

    /// <summary>One directory's entries, or none when it cannot be read.</summary>
    /// <remarks>Materialised inside the try: the enumerator throws while it is being walked, not when
    /// it is created, so an unreadable directory deep in the tree would otherwise take the whole walk
    /// with it.</remarks>
    private static IReadOnlyList<string> Entries(Func<IEnumerable<string>> enumerate)
    {
        try
        {
            return enumerate().ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool IsHidden(string name) => name.StartsWith('.');

    private static bool IsSkipped(string name) => IsHidden(name) || ExcludedDirectories.Contains(name);
}
