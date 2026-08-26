using System.Globalization;
using System.Text;
using mTiles.Models;

namespace mTiles.Services;

public sealed class FileLogWriter
{
    private readonly string _logDirectory;
    private readonly object _writeLock = new();

    public FileLogWriter()
    {
        _logDirectory = AppPaths.GetLogsDirectory();
        Directory.CreateDirectory(_logDirectory);
        CleanupOldLogs();
    }

    public void Write(string level, string message, string? stackTrace = null)
    {
        var entry = FormatEntry(level, message, stackTrace);
        lock (_writeLock)
        {
            try
            {
                File.AppendAllText(GetTodayFilePath(), entry);
            }
            catch { }
        }
    }

    private static string FormatEntry(string level, string message, string? stackTrace)
    {
        var sb = new StringBuilder();
        sb.Append('[').Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append("] [").Append(level).Append("] ").Append(message);
        if (stackTrace is not null)
        {
            sb.AppendLine();
            sb.Append(stackTrace);
        }
        sb.AppendLine();
        return sb.ToString();
    }

    /// <summary>What today's log is called, and what every log written before the application was
    /// renamed is called. Both are pruned; only the first is written.</summary>
    private const string Prefix = "mtiles-";
    private const string LegacyPrefix = "mterminal-";

    private string GetTodayFilePath() =>
        Path.Combine(_logDirectory, $"{Prefix}{DateTime.Now:yyyy-MM-dd}.log");

    private void CleanupOldLogs()
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-AppDefaults.LogRetentionDays);
            // Both prefixes, or the logs written under the old name are never cleaned up again — a
            // retention policy that stops applying to yesterday's files is not one.
            foreach (var prefix in new[] { Prefix, LegacyPrefix })
            foreach (var file in Directory.GetFiles(_logDirectory, $"{prefix}*.log"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var datePart = name[prefix.Length..];
                if (DateTime.TryParseExact(datePart, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                    && date < cutoff)
                {
                    try { File.Delete(file); } catch { }
                }
            }
        }
        catch { }
    }
}
