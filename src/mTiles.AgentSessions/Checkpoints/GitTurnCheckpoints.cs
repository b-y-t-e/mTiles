using System.Diagnostics;
using System.Text;
using mTiles.AgentSessions.Events;

namespace mTiles.AgentSessions.Checkpoints;

/// <summary>
/// Checkpoints as git trees, written beside the repository's history without touching anything the user
/// can see.
/// </summary>
/// <remarks>
/// <para><b>The same construction as the Goal tile's baseline</b> (<c>GoalBaseline</c>), for the same
/// reasons: a private <c>GIT_INDEX_FILE</c> copied from the real one so <c>add -A</c> only re-hashes
/// what moved, <c>write-tree</c> and <c>commit-tree</c> so no branch and no stash moves, and a ref under
/// <c>refs/mtiles/</c> so <c>git gc</c> keeps it. Untracked files are included — they are the ones
/// nothing else can bring back — and <c>.gitignore</c> keeps build output out.</para>
/// <para><b>Tree against tree, never tree against the working copy.</b> A file untracked when a
/// checkpoint was taken is in that tree and not in the index, so <c>git diff &lt;checkpoint&gt;</c>
/// reports it deleted while it sits on disk untouched (measured for <c>GoalBaseline</c>).</para>
/// <para>The commit has no parent, which t3code's checkpoints do the same way: a repository with no
/// commit yet still gets checkpoints.</para>
/// </remarks>
public sealed class GitTurnCheckpoints(string workingDirectory, string gitPath = "git") : ITurnCheckpoints
{
    public const string RefPrefix = "refs/mtiles/agent-sessions/";

    /// <summary>Where the files a restore replaced are kept.</summary>
    public const string ReplacedByRestorePrefix = "refs/mtiles/before-restore/";

    /// <summary>How long one snapshot may take before it is abandoned. A turn's end must not wait on a
    /// repository hashing something enormous.</summary>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(20);

    private const int MaxDiffBytes = 10 * 1024 * 1024;

    public async Task<string?> CaptureAsync(string conversationId, int index, CancellationToken ct)
    {
        using var timed = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timed.CancelAfter(Budget);

        try
        {
            if ((await RunAsync(["rev-parse", "--is-inside-work-tree"], null, timed.Token, throwOnError: false)).Trim() != "true")
                return null;

            // The index only orders the refs for a reader. It cannot be the whole name: the host counts it from
            // the stored events, so a ref written without its event — a failed diff, an event this build could
            // not read — is counted again on the next open, and a name reused there moves a ref a stored
            // checkpoint still points at, which makes that turn's diff and Undo describe other files.
            var name = $"{RefPrefix}{Safe(conversationId)}/{index}-{Guid.NewGuid():N}";
            await SnapshotAsync(name, $"mTiles checkpoint {Safe(conversationId)} {index}", timed.Token);
            return name;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"[AgentSessions] Checkpoint {index} of {conversationId} failed: {ex.Message}");
            return null;
        }
    }

    public async Task<IReadOnlyList<ChangedFile>> ChangesAsync(string fromCheckpoint, string toCheckpoint,
        CancellationToken ct)
    {
        var numstat = await RunAsync(
            ["diff", "--numstat", "-z", "--no-color", "--no-ext-diff", "--no-textconv", "-M", "--relative",
                fromCheckpoint, toCheckpoint, "--", "."],
            null, ct, throwOnError: false);
        var status = await RunAsync(
            ["diff", "--name-status", "-z", "--no-color", "-M", "--relative", fromCheckpoint, toCheckpoint, "--", "."],
            null, ct, throwOnError: false);

        return CheckpointDiffParser.Parse(numstat, status);
    }

    public async Task<string> DiffAsync(string fromCheckpoint, string toCheckpoint, IReadOnlyList<string>? paths,
        CancellationToken ct)
    {
        // Every argument travels on its own: a file name is anybody's text, and one carrying a quote must
        // not be able to close the pathspec and hand git an option of its choosing. And a path is spelled
        // `:(literal)`, because git reads a bare pathspec as a glob: a file actually called `Data[1].json`
        // or `a?b.txt` would not match itself, and the row would open on an empty patch saying nothing.
        string[] pathspec = paths is { Count: > 0 } ? [.. paths.Select(p => ":(literal)" + p)] : ["."];
        var diff = await RunAsync(
            ["diff", "--patch", "--no-color", "--no-ext-diff", "--no-textconv", "-M", "--relative",
                fromCheckpoint, toCheckpoint, "--", .. pathspec],
            null, ct, throwOnError: false);
        return diff.Length > MaxDiffBytes ? diff[..MaxDiffBytes] + "\n… (diff truncated)" : diff;
    }

    /// <remarks>
    /// <para>Not <c>git restore</c> plus <c>git clean</c>, which is what t3code runs: <c>restore</c> goes by
    /// the index, so a file that was untracked when the checkpoint was taken is not put back, and it also
    /// rewrites the user's staging. Instead the checkpoint's tree is checked out through a private index —
    /// every file it holds, tracked or not — and then whatever is on disk now that the checkpoint does not
    /// hold is deleted. Ignored files are in neither list and are never touched.</para>
    /// <para><b>The tree being replaced is photographed first</b>, and a photograph that fails stops the
    /// restore: the checkpoint only knows what the agent's turns left, so an edit made in the terminal next
    /// door since then would otherwise be overwritten or deleted with nothing anywhere holding it. Filed
    /// outside the conversation's own refs, so forgetting the conversation does not take it too.</para>
    /// </remarks>
    public async Task<string> RestoreAsync(string checkpoint, CancellationToken ct)
    {
        var replaced = $"{ReplacedByRestorePrefix}{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
        await SnapshotAsync(replaced, $"mTiles: the files as they were before restoring {checkpoint}", ct);

        var index = Path.Combine(Path.GetTempPath(), $"mtiles-restore-{Guid.NewGuid():N}.idx");
        try
        {
            await RunAsync(["read-tree", checkpoint], index, ct);
            await RunAsync(["checkout-index", "-a", "-f"], index, ct);
        }
        finally
        {
            TryDelete(index);
            TryDelete(index + ".lock");
        }

        // Both lists are limited to the working directory — git scopes `ls-tree`, `ls-files` and
        // `checkout-index -a` to the directory it is run in — and both are spelled from the repository's
        // root, so a workspace inside a larger repository compares like with like and never reaches past
        // itself.
        var pathComparer = await PathComparerAsync(ct);
        var kept = (await RunAsync(["ls-tree", "-r", "-z", "--full-name", "--name-only", checkpoint], null, ct))
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(pathComparer);
        var present = (await RunAsync(["ls-files", "-z", "--full-name", "--cached", "--others", "--exclude-standard"], null, ct))
            .Split('\0', StringSplitOptions.RemoveEmptyEntries);

        var root = (await RunAsync(["rev-parse", "--show-toplevel"], null, ct)).Trim();
        foreach (var path in present.Where(p => !kept.Contains(p)).Distinct(pathComparer))
            TryDelete(Path.Combine(root, path));

        return replaced;
    }

    /// <summary>
    /// How this working tree tells two paths apart: by <c>core.ignorecase</c>, which git sets from the file
    /// system when the repository is made.
    /// </summary>
    /// <remarks>On a file system that ignores case, <c>readme.md</c> restored from the checkpoint and
    /// <c>README.md</c> still in the index are one file, and deleting the second deletes what was just
    /// restored.</remarks>
    private async Task<StringComparer> PathComparerAsync(CancellationToken ct)
    {
        var ignoreCase = (await RunAsync(["config", "--bool", "core.ignorecase"], null, ct, throwOnError: false)).Trim();
        return ignoreCase == "true" ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    }

    /// <summary>Writes the working tree as a parentless commit and points <paramref name="refName"/> at it.</summary>
    private async Task SnapshotAsync(string refName, string message, CancellationToken ct)
    {
        var gitDir = (await RunAsync(["rev-parse", "--absolute-git-dir"], null, ct)).Trim();
        var tree = await WriteTreeAsync(gitDir, ct);

        var commit = (await RunAsync(
            ["-c", "user.name=mTiles", "-c", "user.email=mtiles@localhost", "-c", "commit.gpgsign=false",
                "commit-tree", tree, "-m", message],
            null, ct)).Trim();

        await RunAsync(["update-ref", refName, commit], null, ct);
    }

    public async Task ForgetAsync(string conversationId, CancellationToken ct)
    {
        try
        {
            var listed = await RunAsync(
                ["for-each-ref", "--format=%(refname)", $"{RefPrefix}{Safe(conversationId)}/"], null, ct, throwOnError: false);
            foreach (var name in listed.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                await RunAsync(["update-ref", "-d", name], null, ct, throwOnError: false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Trace.TraceWarning($"[AgentSessions] Forgetting the checkpoints of {conversationId} failed: {ex.Message}");
        }
    }

    private async Task<string> WriteTreeAsync(string gitDir, CancellationToken ct)
    {
        var index = Path.Combine(Path.GetTempPath(), $"mtiles-checkpoint-{Guid.NewGuid():N}.idx");
        try
        {
            var real = Path.Combine(gitDir, "index");
            if (File.Exists(real))
                File.Copy(real, index, overwrite: true);
            else if ((await RunAsync(["rev-parse", "--verify", "HEAD"], null, ct, throwOnError: false)).Trim().Length > 0)
                await RunAsync(["read-tree", "HEAD"], index, ct);

            await RunAsync(["add", "-A", "--", "."], index, ct);
            return (await RunAsync(["write-tree"], index, ct)).Trim();
        }
        finally
        {
            TryDelete(index);
            TryDelete(index + ".lock");
        }
    }

    /// <summary>The part of an id that is safe in a ref name and on a command line.</summary>
    internal static string Safe(string id)
    {
        var kept = new string([.. id.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')]);
        return kept.Length > 0 ? kept : "conversation";
    }

    private async Task<string> RunAsync(IReadOnlyList<string> arguments, string? indexFile, CancellationToken ct,
        bool throwOnError = true)
    {
        var psi = new ProcessStartInfo(gitPath)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) psi.ArgumentList.Add(argument);
        if (indexFile is not null) psi.Environment["GIT_INDEX_FILE"] = indexFile;
        psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";

        using var process = Process.Start(psi)
                            ?? throw new InvalidOperationException($"git {string.Join(' ', arguments)} could not be started");
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            await Task.WhenAll(stdout, stderr);
            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0 && throwOnError)
                throw new InvalidOperationException(
                    $"git {string.Join(' ', arguments)} failed (exit {process.ExitCode}): {(await stderr).Trim()}");

            return process.ExitCode == 0 ? await stdout : "";
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }

            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
