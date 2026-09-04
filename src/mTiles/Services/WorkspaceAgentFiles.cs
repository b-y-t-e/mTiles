using mTiles.Services.Agents;

namespace mTiles.Services;

/// <summary>
/// The files this workspace puts where its AI agents look: the skills they may load, and the one-line
/// shim that points an agent which does not read the canon at the canon.
/// </summary>
/// <remarks>
/// <para><b>The source of truth is this workspace's tile tree, not what is installed on the machine.</b>
/// A project you only ever open Claude Code in gets <c>.claude/skills</c> and <c>CLAUDE.md</c> and
/// nothing else — creating <c>.opencode/skills</c> in somebody's repository for a tool nobody here uses
/// is littering.</para>
/// <para><b>Why this cannot live in the tile that has something to write.</b> Measured 2026-09-03:
/// codex, pi and agy all three read <c>.agents/skills</c>. So "the pi tile was closed, delete pi's
/// directory" is wrong whenever a codex tile is still standing in the same workspace. The rule is
/// therefore <em>recompute the set of paths and delete the difference</em>, and that needs one object
/// per workspace that knows the whole of it — the same category as <see cref="WorkspaceGitWatcher"/>.
/// </para>
/// <para><b>Writing and deleting are asymmetric on purpose.</b> A write goes only to the paths of the
/// agents present here. A <em>blind</em> delete
/// (<see cref="RemoveSkillEverywhere"/>) goes to every path any agent could ever read, without looking
/// at the tiles at all, and it is the rule that covers database access being switched off: no agent may
/// find out about a bridge that is no longer there. Deleting something that was not there costs
/// nothing; leaving a live database address in a directory nobody remembers costs the user.</para>
/// <para>The shim is not covered by that: <c>CLAUDE.md</c> carries one line and no secret, so it goes
/// the ordinary way — it appears with the first Claude Code tile and leaves with the last.</para>
/// <para>What the writer this replaces left behind is somebody else's concern:
/// <see cref="LegacyDatabaseSectionCleanup"/>, called explicitly by whoever opens the workspace, because
/// a migration has its own reason to change and its own expiry date — and because editing files in
/// somebody's repository is not something a constructor should do as a side effect of being reached.
/// </para>
/// <para>See <c>docs/AGENTS-MD-SYNC.md</c> for the measurements and for the alternatives that were
/// rejected (symlinks, full copies, two-way sync by mtime).</para>
/// </remarks>
public sealed class WorkspaceAgentFiles
{
    /// <summary>The one file with content in it. Four of the five CLIs read it without being asked.
    /// </summary>
    public const string CanonicalInstructionFile = "AGENTS.md";

    /// <summary>The whole of a shim, and the only content this class will ever overwrite.</summary>
    /// <remarks>A Claude Code <c>@</c>-import, and deliberately without a generated header carrying a
    /// hash or a timestamp: that is what turns a shim into churn in every commit. Because it is exactly
    /// one line, "is this file ours" is a string comparison rather than a guess — which is what lets a
    /// <c>CLAUDE.md</c> somebody actually wrote survive untouched.</remarks>
    public const string ShimContent = "@" + CanonicalInstructionFile;

    /// <summary>The file a skill is written as, the name all five CLIs use.</summary>
    private const string SkillFileName = "SKILL.md";

    private readonly string _workspaceDir;
    private readonly Lock _gate = new();

    /// <summary>What the last <see cref="Follow"/> worked out, so the next one can delete the
    /// difference rather than having to be told what left.</summary>
    private IReadOnlyList<string> _skillDirectories = [];
    private IReadOnlyList<string> _shimFiles = [];

    /// <summary>The skills this session has already swept out of the paths a previous one wrote them to.
    /// </summary>
    /// <remarks><b>The set difference can only see within one session.</b> Both lists start empty every
    /// launch, so a workspace whose opencode tile was taken out of the layout between two sessions has
    /// nothing to compare against: <c>Missing</c> is empty and the orphaned
    /// <c>.opencode/skills/mtiles-database/SKILL.md</c> — carrying the bridge's address and the names of
    /// this machine's servers — would simply stay. The first sight of a skill in a session therefore
    /// takes it out of every path any agent could read, and the write that follows puts it back wherever
    /// a tile still asks for it. The <c>.gitignore</c> line is deliberately left alone: a workspace
    /// merely being reopened is not the user withdrawing anything.</remarks>
    private readonly HashSet<string> _sweptSkills = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The skills this workspace currently offers, by name.</summary>
    /// <remarks>Held rather than re-asked for, because the two halves arrive at different moments: the
    /// database tile says what there is to write, and the tile tree says where it goes. A pi tile added
    /// an hour later has to find the skill already waiting for it, and the only thing that can put it
    /// there is what was remembered here.</remarks>
    private readonly Dictionary<string, string> _skills = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The shims this instance created, and the only ones it will ever delete.</summary>
    /// <remarks>Recognising a shim by its content says it <em>is</em> one; it does not say it is
    /// <em>ours</em>. A repository that commits <c>CLAUDE.md</c> holding <c>@AGENTS.md</c> — the layout
    /// this whole feature recommends — would otherwise have that file deleted from its working tree the
    /// moment its last Claude Code tile closed. So the rule is `GitIgnoreFile`'s: remove only what we
    /// added, and a shim that outlives the session it was written in simply stays.</remarks>
    private readonly HashSet<string> _shimsWeCreated = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The shims this session tried to write and could not.</summary>
    /// <remarks>A write that fails fails for a reason that does not go away by itself — a read-only
    /// checkout, a file held open, a share that is gone — and <see cref="Follow"/> is asked on every
    /// layout change, a dragged splitter included. Without remembering the refusal, a shim that cannot
    /// be created is a file this class tries to write once per frame of a drag, for the life of the
    /// window. The record is per session and is dropped as soon as the set of shims moves, so a
    /// permission fixed while the workspace is open is picked up by the next tile added.</remarks>
    private readonly HashSet<string> _unwritableShims = new(StringComparer.OrdinalIgnoreCase);

    public WorkspaceAgentFiles(string workspaceDir) => _workspaceDir = workspaceDir;

    /// <summary>The workspace whose files these are.</summary>
    public string WorkspaceDirectory => _workspaceDir;

    /// <summary>
    /// Recomputes every path from the agents this workspace is holding, then makes the disk match.
    /// </summary>
    /// <remarks>Called after any change to the tile tree. Idempotent, so calling it more often than
    /// necessary is only work and never damage.</remarks>
    /// <param name="agents">The agents whose tiles are in this workspace, in any order and with
    /// repeats — three tiles naming <c>.agents/skills</c> are one directory.</param>
    public void Follow(IEnumerable<IAiAgent> agents)
    {
        var present = agents.ToList();
        var skillDirectories = Distinct(present
            .Select(agent => agent.SkillsDirectory(_workspaceDir))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!));
        var shimFiles = Distinct(present
            .Select(agent => agent.InstructionFile)
            .Where(file => !file.Equals(CanonicalInstructionFile, StringComparison.OrdinalIgnoreCase))
            .Select(file => Path.Combine(_workspaceDir, file)));

        lock (_gate)
        {
            var swept = SweepOrphansOnce();

            // Every layout change asks — a splitter dragged as readily as a tile added — and the
            // answer is the same nearly every time. Without this, a SKILL.md would be rewritten while
            // the user drags a divider. A shim that has not been written yet is the one thing that
            // makes an unchanged set worth acting on again: a workspace whose AGENTS.md appears later
            // has to get its shim then, and nothing else will ask — so that one outstanding piece of
            // work is done **on its own**, never as a reason to rewrite every skill and re-enqueue
            // every .gitignore edit behind it.
            if (!swept && Same(_skillDirectories, skillDirectories) && Same(_shimFiles, shimFiles))
            {
                foreach (var shim in shimFiles)
                    WriteShim(shim);
                return;
            }

            foreach (var gone in Missing(_skillDirectories, skillDirectories))
                foreach (var skill in _skills.Keys)
                    // An agent tile left; the user withdrew nothing — and closing the window is the
                    // commonest way for one to leave. Its line stays, for the reason ForgetSkill
                    // spells out: a workspace closed and reopened must not read as a .gitignore edit.
                    DeleteSkillIn(_workspaceDir, gone, skill, unlistFromGitIgnore: false);

            foreach (var gone in Missing(_shimFiles, shimFiles))
                DeleteShim(gone);

            _skillDirectories = skillDirectories;
            _shimFiles = shimFiles;
            _unwritableShims.Clear();

            foreach (var (name, content) in _skills)
                WriteSkillIn(skillDirectories, name, content);

            foreach (var shim in shimFiles)
                WriteShim(shim);
        }
    }

    /// <summary>Takes each skill not yet seen in this session out of every path any agent could read,
    /// and answers whether anything was swept.</summary>
    /// <remarks>See <see cref="_sweptSkills"/>: what it is for is the previous session's paths, which
    /// nothing in this one can name. The caller writes the skills back straight afterwards, so a path a
    /// tile still asks for is rebuilt within the same call.</remarks>
    private bool SweepOrphansOnce()
    {
        var swept = false;
        foreach (var skill in _skills.Keys)
            swept |= SweepOrphansOf(skill);

        return swept;
    }

    private bool SweepOrphansOf(string name)
    {
        if (!_sweptSkills.Add(name)) return false;

        RemoveSkillEverywhere(_workspaceDir, name, unlistFromGitIgnore: false);
        return true;
    }

    /// <summary>Offers a skill to every agent in this workspace, replacing whatever it said before.
    /// </summary>
    public void WriteSkill(string name, string content)
    {
        lock (_gate)
        {
            _skills[name] = content;
            // A skill offered before the tile tree has been followed — the database tile writes from
            // its own constructor — reaches the sweep here instead, so the order of the two cannot
            // decide whether a previous session's orphan survives.
            SweepOrphansOf(name);
            WriteSkillIn(_skillDirectories, name, content);
        }
    }

    /// <summary>Withdraws a skill: forgotten here, and gone from every path any agent could read.
    /// </summary>
    /// <remarks>Blind rather than scoped to the tiles present, which is the security rule rather than
    /// tidiness — see the class remarks. This is the <em>deliberate</em> withdrawal — the last database
    /// unticked, the service switched off, the tile gone from the layout — so the line the skill put in
    /// the user's <c>.gitignore</c> goes with it.</remarks>
    public void RemoveSkill(string name)
    {
        lock (_gate)
        {
            _skills.Remove(name);
            RemoveSkillEverywhere(_workspaceDir, name);
        }
    }

    /// <summary>Takes a skill's files away without touching the <c>.gitignore</c> line they added.
    /// </summary>
    /// <remarks><b>This is what a closing tile asks for, and the difference from
    /// <see cref="RemoveSkill"/> is the user's own repository.</b> Closing the application disposes
    /// every tile, so a withdrawal that unlisted the entry would take the marked block out of a tracked
    /// <c>.gitignore</c> on every exit and put it back on every launch — a modification in
    /// <c>git diff</c> twice a session, for a decision nobody made. The rule
    /// <see cref="GitIgnoreFile"/> already follows for <c>.mtiles/</c> is that the line goes when the
    /// setting behind it is turned off, never when the window closes. The <c>SKILL.md</c> itself still
    /// goes, because that half is the security rule: no agent may find a bridge that is not there.
    /// </remarks>
    public void ForgetSkill(string name)
    {
        lock (_gate)
        {
            _skills.Remove(name);
            RemoveSkillEverywhere(_workspaceDir, name, unlistFromGitIgnore: false);
        }
    }

    /// <summary>
    /// Deletes a skill from every directory any known agent reads, whatever this workspace holds.
    /// </summary>
    /// <remarks>Static and tile-blind on purpose: the callers are "the last database was unticked" and
    /// "the database service was switched off", and both have to reach a directory left behind by a CLI
    /// that has since been uninstalled or by a tile that was closed before the service stopped.
    /// </remarks>
    public static void RemoveSkillEverywhere(string workspaceDir, string name,
        bool unlistFromGitIgnore = true)
    {
        foreach (var agent in AiAgentCatalog.All)
        {
            if (agent.SkillsDirectory(workspaceDir) is { Length: > 0 } directory)
                DeleteSkillIn(workspaceDir, directory, name, unlistFromGitIgnore);
        }
    }

    private static IReadOnlyList<string> Distinct(IEnumerable<string> paths) =>
        [.. paths.Distinct(StringComparer.OrdinalIgnoreCase)];

    private static bool Same(IReadOnlyList<string> before, IReadOnlyList<string> after) =>
        before.Count == after.Count
        && !before.Except(after, StringComparer.OrdinalIgnoreCase).Any();

    private static IEnumerable<string> Missing(IEnumerable<string> before, IEnumerable<string> after) =>
        before.Except(after, StringComparer.OrdinalIgnoreCase);

    private void WriteSkillIn(IEnumerable<string> directories, string name, string content)
    {
        foreach (var directory in directories)
        {
            try
            {
                var skillDirectory = Path.Combine(directory, name);
                Directory.CreateDirectory(skillDirectory);
                File.WriteAllText(Path.Combine(skillDirectory, SkillFileName), content);
                IgnoreSkill(_workspaceDir, directory, name);
            }
            catch (Exception ex)
            {
                // Somebody else's repository, on somebody else's disk: read-only, on a share that has
                // gone away, or held open. A skill that could not be written is a tile that offers less,
                // never a workspace that will not open.
                System.Diagnostics.Trace.TraceWarning(
                    "Could not write the '{0}' skill under '{1}': {2}", name, directory, ex.Message);
            }
        }
    }

    /// <summary>Removes our own subdirectory and nothing else — the user's other skills live beside it.
    /// </summary>
    private static void DeleteSkillIn(string workspaceDir, string directory, string name,
        bool unlistFromGitIgnore = true)
    {
        try
        {
            var skillDirectory = Path.Combine(directory, name);
            if (Directory.Exists(skillDirectory))
                Directory.Delete(skillDirectory, recursive: true);
            if (unlistFromGitIgnore)
                StopIgnoringSkill(workspaceDir, directory, name);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning(
                "Could not remove the '{0}' skill under '{1}': {2}", name, directory, ex.Message);
        }
    }

    // -- The skill's line in the user's .gitignore --

    /// <summary>
    /// Lists this skill's own directory in the workspace's <c>.gitignore</c>, so the machine detail in
    /// it cannot be committed by a <c>git add .</c>.
    /// </summary>
    /// <remarks>
    /// <para><b>Why it has to be ignored at all.</b> A <c>SKILL.md</c> carries
    /// <c>http://localhost:&lt;port&gt;</c> and the names of this machine's servers and databases —
    /// exactly the "machine detail in a committed file" that the section became a skill to be rid of.
    /// Untracked and unignored, it waits in every <c>git status</c> for somebody to stage it.</para>
    /// <para><b>Only our own subdirectory</b> (<c>.claude/skills/mtiles-database/</c>), never the skills
    /// directory itself — the user keeps their own skills beside ours, and those are theirs to commit.
    /// The same rule <see cref="GitIgnoreFile"/> already follows for <c>.mtiles/</c>, through the same
    /// marked block, so removing it takes away our line and nothing else.</para>
    /// <para><b>Only in a repository.</b> Creating a <c>.gitignore</c> in a folder that is not one is
    /// the littering the skill directories themselves are careful to avoid.</para>
    /// </remarks>
    private static void IgnoreSkill(string workspaceDir, string skillsDirectory, string name) =>
        EditIgnore(workspaceDir, skillsDirectory, name,
            (dir, entry) => GitIgnoreFile.EnsureAsync(dir, entry));

    private static void StopIgnoringSkill(string workspaceDir, string skillsDirectory, string name) =>
        EditIgnore(workspaceDir, skillsDirectory, name,
            (dir, entry) => GitIgnoreFile.RemoveAsync(dir, entry));

    private static void EditIgnore(string workspaceDir, string skillsDirectory, string name,
        Func<string, string, Task<bool>> edit)
    {
        if (!LooksLikeRepository(workspaceDir)) return;
        if (IgnoreEntryFor(workspaceDir, skillsDirectory, name) is not { } entry) return;

        GitIgnoreEditQueue.Enqueue(async () =>
        {
            try
            {
                await edit(workspaceDir, entry);
            }
            catch (Exception ex)
            {
                // Somebody else's repository: read-only, on a share that has gone, or held open. A line
                // that could not be written is a file the user may see in git status, never a workspace
                // that will not open.
                System.Diagnostics.Trace.TraceWarning(
                    "Could not list '{0}' in the .gitignore of '{1}': {2}", entry, workspaceDir, ex.Message);
            }
        });
    }

    /// <summary>The entry as git reads it: relative to the workspace, forward slashes, a trailing slash
    /// because it is a directory. Null for a skills directory that is not under the workspace at all,
    /// which nothing here can ignore on the user's behalf.</summary>
    private static string? IgnoreEntryFor(string workspaceDir, string skillsDirectory, string name)
    {
        var relative = Path.GetRelativePath(workspaceDir, Path.Combine(skillsDirectory, name))
            .Replace('\\', '/');
        if (relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            return null;

        return relative.TrimEnd('/') + "/";
    }

    private static bool LooksLikeRepository(string workspaceDir)
    {
        var git = Path.Combine(workspaceDir, ".git");
        // A file rather than a directory is a worktree or a submodule, and both are repositories.
        return Directory.Exists(git) || File.Exists(git);
    }

    /// <summary>
    /// Writes the shim, unless there is already a file there or there is no canon to point it at.
    /// </summary>
    /// <remarks>
    /// <para><b>A file with content in it is never overwritten.</b> The repository where
    /// <c>CLAUDE.md</c> and <c>AGENTS.md</c> both exist with different content is a reconciliation only
    /// the user can make — which of the two is the canon is not something this can guess — and until
    /// they do, nothing is touched. That is also why the shim's content is a constant: "ours" is a
    /// comparison, not an assumption.</para>
    /// <para><b>And a shim is only worth having where the canon exists.</b> In a repository with no
    /// instructions at all, opening a Claude Code tile would otherwise leave an untracked
    /// <c>CLAUDE.md</c> importing a file that is not there — an import to nowhere, and the same
    /// littering the skill directories are careful not to do.</para>
    /// </remarks>
    private void WriteShim(string path)
    {
        try
        {
            // A file that is already there is either our shim — rewriting it would only churn its
            // mtime — or somebody's own instructions, which are not ours to replace.
            if (File.Exists(path)) return;
            if (_unwritableShims.Contains(path)) return;
            if (!CanonExists()) return;

            File.WriteAllText(path, ShimContent + Environment.NewLine);
            _shimsWeCreated.Add(path);
        }
        catch (Exception ex)
        {
            // See _unwritableShims: asked again on every layout change, so a refusal has to be
            // remembered or it is retried once per frame of a dragged splitter.
            _unwritableShims.Add(path);
            System.Diagnostics.Trace.TraceWarning(
                "Could not write the instruction shim '{0}': {1}", path, ex.Message);
        }
    }

    /// <summary>Takes the shim away when the last tile that needed it goes, and only if we put it
    /// there ourselves and it is still a shim.</summary>
    /// <remarks>See <see cref="_shimsWeCreated"/>: content says what a file is, not whose it is.
    /// </remarks>
    private void DeleteShim(string path)
    {
        try
        {
            if (!_shimsWeCreated.Remove(path)) return;
            if (IsShim(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning(
                "Could not remove the instruction shim '{0}': {1}", path, ex.Message);
        }
    }

    /// <summary>Whether there is anything for a shim to point at.</summary>
    private bool CanonExists() => File.Exists(Path.Combine(_workspaceDir, CanonicalInstructionFile));

    private static bool IsShim(string path)
    {
        try
        {
            return File.Exists(path)
                   && File.ReadAllText(path).Trim().Equals(ShimContent, StringComparison.Ordinal);
        }
        catch
        {
            // Unreadable is not ours: the one thing that must not happen is deleting a file whose
            // content nobody could look at.
            return false;
        }
    }
}
