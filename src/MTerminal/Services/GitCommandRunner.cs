using System.Diagnostics;
using MTerminal.Models;

namespace MTerminal.Services;

public sealed class GitCommandRunner(string workingDirectory)
{
    public string WorkingDirectory { get; } = workingDirectory;

    public Task<string> RunAsync(string arguments, CancellationToken ct = default)
        => RunAsync(arguments, throwOnError: true, ct);

    public async Task<string> RunAsync(string arguments, bool throwOnError, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        var process = Process.Start(psi);
        if (process == null)
            throw new InvalidOperationException($"Failed to start git process: git {arguments}");

        using (process)
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            await Task.WhenAll(stdoutTask, stderrTask);
            await process.WaitForExitAsync(ct);

            if (throwOnError && process.ExitCode != 0)
            {
                var stderr = (await stderrTask).Trim();
                throw new InvalidOperationException(
                    $"git {arguments} failed (exit {process.ExitCode}): {stderr}");
            }

            return await stdoutTask;
        }
    }

    public static GitFileChange? ParseStatusLine(string line)
    {
        if (line.Length < 3) return null;

        var x = line[0];
        var y = line[1];
        var rawPath = line[3..].Trim();

        char statusChar;
        if (x == '?' && y == '?')
            statusChar = '?';
        else if (x != ' ' && x != '?')
            statusChar = x;
        else
            statusChar = y;

        string? oldPath = null;
        string path;

        if (statusChar is 'R' or 'C')
        {
            var arrowIdx = rawPath.IndexOf(" -> ", StringComparison.Ordinal);
            if (arrowIdx > 0)
            {
                oldPath = rawPath[..arrowIdx].Trim('"');
                path = rawPath[(arrowIdx + 4)..].Trim('"');
            }
            else
            {
                path = rawPath.Trim('"');
            }
        }
        else
        {
            path = rawPath.Trim('"');
        }

        var display = statusChar switch
        {
            'M' => "Modified",
            'A' => "Added",
            'D' => "Deleted",
            'R' => "Renamed",
            'C' => "Copied",
            'U' => "Unmerged",
            '?' => "Untracked",
            _ => statusChar.ToString()
        };

        return new GitFileChange
        {
            FilePath = path,
            OldFilePath = oldPath,
            Status = statusChar.ToString(),
            StatusDisplay = display
        };
    }
}
