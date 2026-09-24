using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace mTiles.Services;

/// <summary>
/// Of every installation of a program this machine has, the one with the highest version.
/// </summary>
/// <remarks>
/// <para>For the AI CLIs, which are installed by more than one route — npm, winget, a native
/// installer — and are forgotten there: measured 2026-09-23, a winget <c>claude.exe</c> at 2.1.140
/// left behind beside npm's 2.1.280, and every agent tile ran the old one, which refused the model it
/// was configured with. <c>PATH</c> order says nothing about which of them is current, and a model
/// launched on a stale CLI is a run that fails with somebody else's error message.</para>
/// <para><b>Asking costs a process per installation</b>, so it is asked only where there is more than
/// one, and remembered against each candidate's path and last-write time: an update rewrites the
/// binary or its shim, and that is the only thing that changes the answer. A candidate that does not
/// answer, or answers something without a version in it, loses to one that does; when none does, the
/// first in <see cref="ExecutableFinder.Everywhere"/>'s order wins, which is what was used before.</para>
/// </remarks>
internal static partial class NewestInstallation
{
    /// <summary>How long one <c>--version</c> may take before that installation is counted as not
    /// answering. They are asked in parallel, so this is also the worst case for the whole question.</summary>
    private static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(5);

    private static readonly ConcurrentDictionary<string, (string Stamp, string Chosen)> Chosen =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The newest installation of <paramref name="name"/>, or null when there is none.</summary>
    public static string? Find(string name)
    {
        var candidates = ExecutableFinder.Everywhere(name);
        if (candidates.Count <= 1) return candidates.FirstOrDefault();

        var stamp = Stamp(candidates);
        if (Chosen.TryGetValue(name, out var known) && known.Stamp == stamp)
            return known.Chosen;

        var versions = Task.WhenAll(candidates.Select(path => Task.Run(() => VersionOfAsync(path))))
            .GetAwaiter().GetResult();
        var chosen = candidates[Pick(versions)];

        Trace.TraceInformation("{0}: {1} installations, using {2}. Found: {3}", name, candidates.Count, chosen,
            string.Join("; ", candidates.Select((path, i) => $"{path} = {versions[i]?.ToString() ?? "?"}")));

        Chosen[name] = (stamp, chosen);
        return chosen;
    }

    /// <summary>The index of the highest version; the earliest of equals, and the first when none is
    /// known.</summary>
    internal static int Pick(IReadOnlyList<Version?> versions)
    {
        var best = 0;
        for (var i = 1; i < versions.Count; i++)
            if (versions[i] is { } version && (versions[best] is null || version > versions[best]))
                best = i;
        return best;
    }

    /// <summary>The first dotted number in what a <c>--version</c> printed — <c>2.1.280 (Claude
    /// Code)</c>, <c>codex-cli 0.141.0</c>, <c>v1.18.18</c> — or null.</summary>
    internal static Version? ParseVersion(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var match = DottedNumber().Match(output);
        return match.Success && Version.TryParse(match.Value, out var version) ? version : null;
    }

    [GeneratedRegex(@"\d+(\.\d+){1,3}")]
    private static partial Regex DottedNumber();

    private static string Stamp(IEnumerable<string> candidates) =>
        string.Join("|", candidates.Select(path =>
        {
            try { return path + "@" + File.GetLastWriteTimeUtc(path).Ticks; }
            catch { return path; }
        }));

    private static async Task<Version?> VersionOfAsync(string path)
    {
        try
        {
            var psi = new ProcessStartInfo(path, "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi);
            if (process is null) return null;

            using var cts = new CancellationTokenSource(VersionTimeout);
            try
            {
                var stdout = process.StandardOutput.ReadToEndAsync(cts.Token);
                var stderr = process.StandardError.ReadToEndAsync(cts.Token);
                await process.WaitForExitAsync(cts.Token);
                return ParseVersion(await stdout) ?? ParseVersion(await stderr);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                return null;
            }
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Could not ask {0} for its version: {1}", path, ex.Message);
            return null;
        }
    }
}
