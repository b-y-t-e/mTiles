using MTerminal.Models;

namespace MTerminal.Services;

public sealed class GitStatusResult
{
    public bool IsGitRepo { get; init; }
    public string BranchName { get; init; } = "";
    public List<GitFileChange> Changes { get; init; } = [];
    public List<CommitLogEntry> CommitLog { get; init; } = [];
    public int StashCount { get; init; }
}

public sealed class DiffResult
{
    public string DiffText { get; init; } = "";
    public string OldContent { get; init; } = "";
    public string NewContent { get; init; } = "";
}

public sealed class GitService(string workingDirectory)
{
    private readonly GitCommandRunner _git = new(workingDirectory);

    public string WorkingDirectory => workingDirectory;

    public async Task<GitStatusResult> GetStatusAsync(CancellationToken ct = default)
    {
        var checkResult = await _git.RunAsync("rev-parse --is-inside-work-tree", throwOnError: false, ct);
        if (checkResult.Trim() != "true")
            return new GitStatusResult { IsGitRepo = false };

        var branchTask = _git.RunAsync("rev-parse --abbrev-ref HEAD", ct);
        var statusTask = _git.RunAsync("status --porcelain", ct);
        var logTask = _git.RunAsync("log --oneline -30", ct);
        var stashTask = _git.RunAsync("stash list", ct);

        await Task.WhenAll(branchTask, statusTask, logTask, stashTask);
        ct.ThrowIfCancellationRequested();

        var changes = ParseChanges(await statusTask);
        var commitLog = ParseCommitLog(await logTask);
        var stashLines = (await stashTask).Split('\n', StringSplitOptions.RemoveEmptyEntries);

        return new GitStatusResult
        {
            IsGitRepo = true,
            BranchName = (await branchTask).Trim(),
            Changes = changes,
            CommitLog = commitLog,
            StashCount = stashLines.Length
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

    private async Task<string> ReadWorkingFileAsync(string relativePath)
    {
        var fullPath = Path.Combine(workingDirectory, relativePath);
        if (!File.Exists(fullPath)) return "";
        try { return await File.ReadAllTextAsync(fullPath); }
        catch { return ""; }
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
}
