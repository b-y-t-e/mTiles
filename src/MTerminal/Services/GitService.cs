using System.Text.RegularExpressions;
using MTerminal.Models;

namespace MTerminal.Services;

public sealed class GitStatusResult
{
    public bool IsGitRepo { get; init; }
    public string BranchName { get; init; } = "";
    public List<GitFileChange> Changes { get; init; } = [];
    public List<CommitLogEntry> CommitLog { get; init; } = [];
    public int StashCount { get; init; }
    public int UnpushedCount { get; init; }
    public bool HasRemote { get; init; }
}

public sealed class DiffResult
{
    public string DiffText { get; init; } = "";
    public string OldContent { get; init; } = "";
    public string NewContent { get; init; } = "";
}

public sealed partial class GitService(string workingDirectory, string gitPath = "git")
{
    private readonly GitCommandRunner _git = new(workingDirectory, gitPath);

    public string WorkingDirectory => workingDirectory;

    public async Task<GitStatusResult> GetStatusAsync(CancellationToken ct = default)
    {
        var checkResult = await _git.RunAsync("rev-parse --is-inside-work-tree", throwOnError: false, ct);
        if (checkResult.Trim() != "true")
            return new GitStatusResult { IsGitRepo = false };

        var branch = (await _git.RunAsync("rev-parse --abbrev-ref HEAD", ct)).Trim();

        var statusTask = _git.RunAsync("-c core.quotePath=false status --porcelain -uall", ct);
        var logTask = _git.RunAsync("log --oneline -30", ct);
        var stashTask = _git.RunAsync("stash list", ct);
        var tagsTask = GetTagsMapInternalAsync(ct);
        var unpushedTask = GetUnpushedHashesAsync(branch, ct);

        await Task.WhenAll(statusTask, logTask, stashTask, tagsTask, unpushedTask);
        ct.ThrowIfCancellationRequested();

        var changes = ParseChanges(await statusTask);
        var commitLog = ParseCommitLog(await logTask);
        var stashLines = (await stashTask).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var tagsMap = await tagsTask;
        var unpushedResult = await unpushedTask;
        var unpushedHashes = unpushedResult.Hashes;
        var hasRemote = unpushedResult.HasRemote;

        var decoratedLog = commitLog.Select(e =>
        {
            var shortHash = e.Hash[..Math.Min(7, e.Hash.Length)];
            var entryTags = tagsMap.TryGetValue(shortHash, out var t) ? t : [];
            return new CommitLogEntry
            {
                Hash = e.Hash,
                Message = e.Message,
                Tags = entryTags,
                IsPushed = !hasRemote || !unpushedHashes.Contains(shortHash)
            };
        }).ToList();

        return new GitStatusResult
        {
            IsGitRepo = true,
            BranchName = branch,
            Changes = changes,
            CommitLog = decoratedLog,
            StashCount = stashLines.Length,
            UnpushedCount = unpushedHashes.Count,
            HasRemote = hasRemote
        };
    }

    public async Task<DiffResult> GetDiffAsync(GitFileChange change)
    {
        string diffArgs;
        if (change.Status == "?")
            diffArgs = $"diff --no-index /dev/null -- \"{change.FilePath}\"";
        else if (change.Status == "R" && change.OldFilePath != null)
            diffArgs = $"diff -M -- \"{change.OldFilePath}\" \"{change.FilePath}\"";
        else
            diffArgs = $"diff -- \"{change.FilePath}\"";

        var diffTask = _git.RunAsync(diffArgs, throwOnError: false);

        Task<string> oldTask;
        Task<string> newTask;

        if (change.Status == "?")
        {
            oldTask = Task.FromResult("");
            newTask = ReadWorkingFileAsync(change.FilePath);
        }
        else if (change.Status == "D")
        {
            oldTask = _git.RunAsync($"show HEAD:\"{change.FilePath}\"", throwOnError: false);
            newTask = Task.FromResult("");
        }
        else
        {
            var oldPath = change.OldFilePath ?? change.FilePath;
            oldTask = _git.RunAsync($"show HEAD:\"{oldPath}\"", throwOnError: false);
            newTask = ReadWorkingFileAsync(change.FilePath);
        }

        await Task.WhenAll(diffTask, oldTask, newTask);

        return new DiffResult
        {
            DiffText = await diffTask,
            OldContent = await oldTask,
            NewContent = await newTask
        };
    }

    public async Task<string> GetCommitDiffAsync(string commitHash)
    {
        return await _git.RunAsync($"show --format= \"{commitHash}\"");
    }

    public async Task CommitAsync(IReadOnlyList<string> files, string message)
    {
        var addArgs = string.Join(" ", files.Select(f => $"\"{f}\""));
        await _git.RunAsync($"add -- {addArgs}");

        var tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tempFile, message);
            await _git.RunAsync($"commit -F \"{tempFile}\"");
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    public async Task StashAsync() => await _git.RunAsync("stash");

    public async Task StashPopAsync() => await _git.RunAsync("stash pop");

    public async Task DiscardAsync(string filePath) =>
        await _git.RunAsync($"checkout -- \"{filePath}\"");

    private async Task<string> ReadWorkingFileAsync(string relativePath)
    {
        var fullPath = Path.Combine(workingDirectory, relativePath);
        if (!File.Exists(fullPath)) return "";
        try { return await File.ReadAllTextAsync(fullPath); }
        catch { return ""; }
    }

    public async Task<HashSet<string>> GetIgnoredDirsAsync(CancellationToken ct = default)
    {
        var output = await _git.RunAsync("ls-files --others --ignored --exclude-standard --directory --no-empty-directory", ct);
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.TrimEnd('\r');
            if (!trimmed.EndsWith('/')) continue;
            var fullPath = Path.GetFullPath(Path.Combine(_git.WorkingDirectory, trimmed));
            dirs.Add(fullPath);
        }
        return dirs;
    }

    private static List<GitFileChange> ParseChanges(string statusOutput)
    {
        var changes = new List<GitFileChange>();
        foreach (var line in statusOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length < 3) continue;
            var change = GitCommandRunner.ParseStatusLine(line);
            if (change != null)
                changes.Add(change);
        }
        changes.Sort((a, b) => string.Compare(a.FilePath, b.FilePath, StringComparison.OrdinalIgnoreCase));
        return changes;
    }

    private static List<CommitLogEntry> ParseCommitLog(string logOutput)
    {
        var entries = new List<CommitLogEntry>();
        foreach (var line in logOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var spaceIdx = line.IndexOf(' ');
            entries.Add(spaceIdx > 0
                ? new CommitLogEntry { Hash = line[..spaceIdx], Message = line[(spaceIdx + 1)..] }
                : new CommitLogEntry { Hash = line, Message = "" });
        }
        return entries;
    }

    private async Task<Dictionary<string, List<string>>> GetTagsMapInternalAsync(CancellationToken ct)
    {
        // %(*objectname:short) dereferences annotated tags to the commit hash; empty for lightweight tags
        var output = await _git.RunAsync(
            "tag -l --format=\"%(objectname:short) %(*objectname:short) %(refname:short)\"",
            throwOnError: false, ct);
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Trim().Trim('"').Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;

            string tagHash, deref, tagName;
            if (parts.Length >= 3)
            {
                tagHash = parts[0];
                deref = parts[1];
                tagName = parts[2];
            }
            else
            {
                tagHash = parts[0];
                deref = "";
                tagName = parts[1];
            }

            var commitHash = deref.Length > 0 ? deref : tagHash;
            if (!map.TryGetValue(commitHash, out var list))
            {
                list = [];
                map[commitHash] = list;
            }
            list.Add(tagName);
        }
        return map;
    }

    private readonly record struct UnpushedResult(HashSet<string> Hashes, bool HasRemote);

    private async Task<UnpushedResult> GetUnpushedHashesAsync(string branch, CancellationToken ct)
    {
        if (branch == "HEAD") // detached HEAD
            return new UnpushedResult([], false);

        var upstream = (await _git.RunAsync("rev-parse --abbrev-ref @{upstream}", throwOnError: false, ct)).Trim();
        if (string.IsNullOrWhiteSpace(upstream) || upstream.Contains(' '))
            return new UnpushedResult([], false);

        var output = await _git.RunAsync($"log \"{upstream.Trim()}..HEAD\" --oneline", throwOnError: false, ct);
        var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var spaceIdx = line.IndexOf(' ');
            var hash = spaceIdx > 0 ? line[..spaceIdx] : line.Trim();
            if (hash.Length > 0)
                hashes.Add(hash[..Math.Min(7, hash.Length)]);
        }
        return new UnpushedResult(hashes, true);
    }

    public async Task PushAsync(string branch, CancellationToken ct = default)
    {
        var upstream = (await _git.RunAsync("rev-parse --abbrev-ref @{upstream}", throwOnError: false, ct)).Trim();
        if (string.IsNullOrWhiteSpace(upstream) || upstream.Contains(' '))
            await _git.RunAsync($"push -u origin \"{branch}\"", ct);
        else
            await _git.RunAsync("push", ct);
    }

    public async Task FetchAsync(CancellationToken ct = default)
    {
        await _git.RunAsync("fetch --all --prune", ct);
    }

    public async Task CreateTagAsync(string tagName, string commitHash, CancellationToken ct = default)
    {
        if (!IsValidTagName(tagName))
            throw new ArgumentException($"Invalid tag name: {tagName}");
        if (!IsValidCommitHash(commitHash))
            throw new ArgumentException($"Invalid commit hash: {commitHash}");
        await _git.RunAsync($"tag \"{tagName}\" {commitHash}", ct);
    }

    private static bool IsValidTagName(string name) =>
        !string.IsNullOrWhiteSpace(name) && TagNameRegex().IsMatch(name);

    private static bool IsValidCommitHash(string hash) =>
        !string.IsNullOrWhiteSpace(hash) && hash.Length <= 40 && CommitHashRegex().IsMatch(hash);

    [GeneratedRegex(@"^[a-zA-Z0-9._/\-]+$")]
    private static partial Regex TagNameRegex();

    [GeneratedRegex(@"^[a-f0-9]+$")]
    private static partial Regex CommitHashRegex();

    public async Task UndoLastCommitAsync(CancellationToken ct = default)
    {
        await _git.RunAsync("reset --soft HEAD~1", ct);
    }

    public async Task<List<string>> GetRecentMessagesAsync(int count = 50, CancellationToken ct = default)
    {
        var output = await _git.RunAsync($"log --format=%s -{count}", throwOnError: false, ct);
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                     .Select(l => l.Trim())
                     .Where(l => l.Length > 0)
                     .ToList();
    }

    public async Task<List<string>> GetTagListAsync(int count = 10, CancellationToken ct = default)
    {
        var output = await _git.RunAsync("tag -l --sort=-creatordate", throwOnError: false, ct);
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                     .Select(l => l.Trim())
                     .Where(l => l.Length > 0)
                     .Take(count)
                     .ToList();
    }

    public static async Task<string> GetBranchNameAsync(string directory, string gitPath = "git")
    {
        var runner = new GitCommandRunner(directory, gitPath);
        var result = await runner.RunAsync("rev-parse --abbrev-ref HEAD", throwOnError: false);
        var branch = result.Trim();
        return string.IsNullOrEmpty(branch) || branch.Contains(' ') ? "" : branch;
    }

    public static string ResolveGitPath(string? customPath)
    {
        if (!string.IsNullOrEmpty(customPath) && File.Exists(customPath))
            return customPath;

        if (OperatingSystem.IsWindows())
        {
            var found = ShellDetector.FindExecutable("git.exe")
                        ?? ShellDetector.FindExecutable("git.cmd");
            if (found != null) return found;

            var wellKnown = new[]
            {
                @"C:\Program Files\Git\cmd\git.exe",
                @"C:\Program Files (x86)\Git\cmd\git.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                             "Programs", "Git", "cmd", "git.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                             "scoop", "shims", "git.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                             "scoop", "apps", "git", "current", "cmd", "git.exe"),
            };
            foreach (var p in wellKnown)
                if (File.Exists(p)) return p;
        }
        else
        {
            var found = ShellDetector.FindExecutable("git");
            if (found != null) return found;
        }

        return "git";
    }

    public static async Task<string?> TestGitAsync(string gitPath)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(gitPath, "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            var process = System.Diagnostics.Process.Start(psi);
            if (process == null) return null;

            using (process)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
                var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);
                await Task.WhenAll(stdoutTask, stderrTask);
                await process.WaitForExitAsync(cts.Token);
                var line = (await stdoutTask).Trim().Split('\n')[0].Trim();
                return string.IsNullOrEmpty(line) ? null : line;
            }
        }
        catch
        {
            return null;
        }
    }
}
