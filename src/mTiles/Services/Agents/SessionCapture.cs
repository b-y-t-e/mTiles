using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace mTiles.Services.Agents;

/// <summary>
/// The mechanics behind <c>SessionStrategy.CapturedAfterStart</c>: getting an id out of an agent that
/// insists on choosing it itself.
/// </summary>
/// <remarks>
/// <para>Two agents need this and they need it in opposite ways — agy answers with an id if you ask it
/// something, codex leaves one on disk — so what is shared is not a procedure but the two pieces of
/// plumbing underneath: running a CLI and reading what it printed, and finding the newest file matching
/// a pattern. The per-agent part is an override on the agent, so a sixth agent adds a method to itself
/// rather than a branch to this.</para>
/// <para>Everything that can be a pure function is one, because both halves are otherwise untestable
/// without the CLIs installed.</para>
/// </remarks>
internal static partial class SessionCapture
{
    /// <summary>Long enough for a cheap round trip to a hosted model, short enough that a tile being
    /// created does not appear to hang. A capture that times out is a tile whose conversation is not
    /// resumable, which is a loss and not a failure.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Runs the agent once and returns everything it printed, or null if it could not be run.
    /// </summary>
    /// <remarks>Never throws: a capture is an optimisation on top of a tile that works without it, so
    /// every failure here has to end as "no session id" rather than as no tile.</remarks>
    /// <param name="environment">What the tile's own session will run with, where a <c>null</c> value
    /// removes a variable. A capture that creates the conversation has to be given it, or it creates
    /// one the tile cannot then resume as itself.</param>
    public static async Task<string?> RunForOutputAsync(string executablePath, string workingDirectory,
        IReadOnlyList<string> arguments, CancellationToken ct,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);

            var psi = new ProcessStartInfo
            {
                FileName = executablePath,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // Redirected and closed at once, for the reason every other launch here does it: a CLI
                // that decides to be interactive would otherwise wait on a windowed process' standard
                // input, which nobody is ever going to type into.
                RedirectStandardInput = true,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
            };

            foreach (var argument in arguments)
                psi.ArgumentList.Add(argument);

            foreach (var (name, value) in environment ?? new Dictionary<string, string?>())
            {
                if (value is null) psi.Environment.Remove(name);
                else psi.Environment[name] = value;
            }

            using var process = Process.Start(psi);
            if (process is null) return null;

            try { process.StandardInput.Close(); } catch { /* already gone */ }

            var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);

            await using var kill = timeout.Token.Register(() =>
            {
                try { process.Kill(entireProcessTree: true); } catch { }
            });

            await process.WaitForExitAsync(CancellationToken.None);

            var text = await output;
            var stderr = await errors;

            // Both, in that order. agy prints its warning about an unknown conversation on stderr and
            // the JSON on stdout, and a CLI that changes its mind about which stream to use would
            // otherwise turn a working capture into a silent null.
            return string.IsNullOrWhiteSpace(text) ? stderr : text;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Capturing a session id from {0} failed, so the tile will start a new "
                + "conversation instead: {1}", executablePath, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// The <c>conversation_id</c> in what an agent printed, or null.
    /// </summary>
    /// <remarks>
    /// <para>Line by line and tolerant of everything else, because <c>--output-format json</c> is one
    /// object on stdout <em>and</em> whatever the CLI decided to say around it — measured: agy warns
    /// about an unknown conversation before printing the object. Parsing the whole of what came back as
    /// one document therefore fails on exactly the run this exists to read.</para>
    /// <para>The last one wins: a run that opened a conversation and then reported on it names the same
    /// id twice, and if it ever names two the later one is the one that exists.</para>
    /// </remarks>
    public static string? ConversationIdIn(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;

        string? found = null;

        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] != '{') continue;

            try
            {
                using var document = JsonDocument.Parse(trimmed);
                if (document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("conversation_id", out var id)
                    && id.GetString() is { Length: > 0 } value)
                    found = value;
            }
            catch (JsonException)
            {
                // A line that starts like an object and is not one. Nothing to say about it.
            }
        }

        return found;
    }

    /// <summary>
    /// The id of the codex session this tile started under <paramref name="sessionsRoot"/>, or null.
    /// </summary>
    /// <remarks>
    /// <para>codex writes <c>&lt;root&gt;/YYYY/MM/DD/rollout-&lt;timestamp&gt;-&lt;uuid&gt;.jsonl</c>.
    /// The date directories are walked rather than assumed, because a session started before midnight
    /// is under yesterday's, and the newest is chosen by creation time rather than by the name's
    /// timestamp — the name's format is codex's and the file system's is not.</para>
    /// <para><b>Three filters, and none of them is redundant.</b> <paramref name="notBefore"/> rules out
    /// a session older than this tile, but it cannot tell two live ones apart: codex appends to its
    /// rollout for as long as the session lasts, so a second codex tile — or one the user started by
    /// hand in a terminal tile — is being written the whole time this one is looking, and picking the
    /// most recently *written* file would hand both tiles the same id. So the file's own recorded
    /// <c>cwd</c> must be <paramref name="workingDirectory"/>, and the id has to be one this tile can
    /// still take (<paramref name="tryTake"/>). Two codex tiles in one workspace started in the same
    /// second are what the second filter alone would not settle.</para>
    /// <para><b><paramref name="tryTake"/> takes the id, it does not merely report on it.</b> Asking
    /// whether an id is free and claiming it afterwards is two steps, and two tiles restored from one
    /// layout capture on the thread pool at the same moment: both would read the same rollout as free,
    /// both would write the same id down, and one conversation would be lost at the next launch — the
    /// very failure the third filter exists to prevent. It is therefore the <em>last</em> filter, so
    /// nothing is taken on behalf of a candidate the earlier two reject.</para>
    /// <para>A rollout whose <c>cwd</c> cannot be read is <em>not</em> a candidate. That is the safe way
    /// round for the same reason the timestamp exists: resuming a stranger's session is worse than
    /// starting a fresh one, and a format change that makes every capture fail costs a conversation,
    /// never a tile.</para>
    /// </remarks>
    public static string? NewestSessionId(string sessionsRoot, DateTimeOffset notBefore,
        string workingDirectory, Func<string, bool>? tryTake = null)
    {
        try
        {
            if (!Directory.Exists(sessionsRoot)) return null;

            return new DirectoryInfo(sessionsRoot)
                .EnumerateFiles("rollout-*.jsonl", SearchOption.AllDirectories)
                .Where(file => Started(file) >= notBefore.UtcDateTime)
                .OrderByDescending(Started)
                .Select(file => (File: file, Id: SessionIdIn(file.Name)))
                .Where(candidate => candidate.Id is { Length: > 0 }
                    && StartedIn(candidate.File.FullName, workingDirectory)
                    && tryTake?.Invoke(candidate.Id) != false)
                .Select(candidate => candidate.Id)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Reading the codex session directory failed, so the tile will start a "
                + "new conversation instead: {0}", ex.Message);
            return null;
        }
    }

    /// <summary>When a rollout file's session began.</summary>
    /// <remarks>Creation time rather than last write: codex keeps appending to a rollout for the whole
    /// of its session, so the write time of a conversation opened yesterday is a moment ago. The write
    /// time is taken when it is the earlier of the two, which is what a file system that does not record
    /// a creation time reports.</remarks>
    private static DateTime Started(FileInfo file) =>
        file.CreationTimeUtc < file.LastWriteTimeUtc ? file.CreationTimeUtc : file.LastWriteTimeUtc;

    /// <summary>Whether a rollout file says it belongs to a session started in
    /// <paramref name="workingDirectory"/>.</summary>
    /// <remarks>Only the first line is read — codex writes its session metadata there and the rest of
    /// the file is the conversation, which can run to megabytes.</remarks>
    private static bool StartedIn(string rolloutPath, string workingDirectory)
    {
        try
        {
            using var reader = new StreamReader(new FileStream(rolloutPath, FileMode.Open,
                FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));

            return CwdIn(reader.ReadLine()) is { } cwd && SameDirectory(cwd, workingDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The <c>cwd</c> a codex rollout's metadata line records, or null.</summary>
    /// <remarks>Looked for at the root and one level down under <c>payload</c>, because codex has
    /// written both shapes. Anywhere else reads as "it did not say", which is not a candidate.</remarks>
    public static string? CwdIn(string? metadataLine)
    {
        if (string.IsNullOrWhiteSpace(metadataLine)) return null;

        try
        {
            using var document = JsonDocument.Parse(metadataLine);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            if (root.TryGetProperty("cwd", out var cwd) && cwd.GetString() is { Length: > 0 } here)
                return here;

            if (root.TryGetProperty("payload", out var payload)
                && payload.ValueKind == JsonValueKind.Object
                && payload.TryGetProperty("cwd", out var nested)
                && nested.GetString() is { Length: > 0 } inside)
                return inside;

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Whether two paths name the same directory.</summary>
    /// <remarks>Compared as full paths without their trailing separator, and case-insensitively only
    /// where the platform is: <c>C:\Work</c> and <c>c:\work\</c> are one directory on Windows and two
    /// on Linux.</remarks>
    private static bool SameDirectory(string left, string right)
    {
        try
        {
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            return string.Equals(Normalise(left), Normalise(right), comparison);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        static string Normalise(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    /// <summary>The UUID in a <c>rollout-&lt;timestamp&gt;-&lt;uuid&gt;.jsonl</c> file name, or null.
    /// <para>Anchored on the UUID's own shape rather than on "everything after the second dash": the
    /// timestamp contains dashes too, and counting them is a rule that breaks the first time codex
    /// changes its stamp format — silently, into an id that resumes nothing.</para></summary>
    public static string? SessionIdIn(string fileName) =>
        RolloutName().Match(fileName) is { Success: true } match ? match.Groups[1].Value : null;

    [GeneratedRegex(@"^rollout-.*-([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\.jsonl$")]
    private static partial Regex RolloutName();
}
