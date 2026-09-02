using System.Diagnostics;
using System.Text.Json;

namespace mTiles.Services;

/// <summary>What one day cost, where anything was recorded for it.</summary>
/// <param name="Date">The UTC day, which is the boundary the counters this records reset on.</param>
/// <param name="Amount">What was spent, or null for a day this application was not watching. <b>Null is
/// not zero</b>: a day nobody sampled and a day that cost nothing are not the same fact, and drawn alike
/// they read as a week of free days.</param>
public sealed record UsageDay(DateOnly Date, decimal? Amount);

/// <summary>
/// The seven days on a money card, kept here because nobody else keeps them.
/// </summary>
/// <remarks>
/// <para><b>Why this exists at all.</b> Measured 2026-09-01: OpenRouter's <c>api/v1/activity</c> answers
/// 403 for an ordinary key (<i>Only management keys can fetch activity</i>), so there is no per-day
/// history to fetch from anybody without asking the user for a second and stronger key. The bars are
/// therefore this application's own snapshots — they start empty and fill in, and the card says so
/// rather than drawing six zero-height bars that read as six free days.</para>
/// <para><b>The day is UTC and the maximum for a date wins.</b> UTC because that is the boundary the
/// counter being sampled resets on; the maximum because the value is a running daily total, so a poll
/// landing just after midnight would otherwise write a fresh small number over a finished day.</para>
/// <para>Fails soft at every step. An unreadable file is a fresh start rather than a crash: what is
/// lost is a row of bars, and the card is still worth drawing without them.</para>
/// </remarks>
public sealed class UsageHistory
{
    /// <summary>How far back is kept. Long enough that a month's shape survives a fortnight away from
    /// the machine, short enough that the file stays a few kilobytes.</summary>
    public const int RetainedDays = 60;

    private readonly string _filePath;
    private readonly Lock _gate = new();
    private Dictionary<string, Dictionary<string, decimal>>? _bySource;

    /// <summary>The store in this installation's own directory.</summary>
    public UsageHistory() : this(Path.Combine(AppPaths.GetUsageDirectory(), "history.json")) { }

    /// <summary>A store at a given path, which is what a test hands it.</summary>
    public UsageHistory(string filePath) => _filePath = filePath;

    /// <summary>
    /// Notes what a source has spent today, and answers whether anything changed.
    /// </summary>
    /// <remarks>A smaller figure for a day already recorded is ignored rather than written, which is
    /// what makes an unordered sequence of polls produce the same file as an ordered one.</remarks>
    public bool Record(string sourceId, DateTimeOffset measuredAt, decimal amount)
    {
        if (sourceId.Length == 0 || amount < 0) return false;

        lock (_gate)
        {
            var days = Loaded().TryGetValue(sourceId, out var existing)
                ? existing
                : Loaded()[sourceId] = new Dictionary<string, decimal>();

            var key = Key(DateOnly.FromDateTime(measuredAt.UtcDateTime));
            if (days.TryGetValue(key, out var recorded) && recorded >= amount) return false;

            days[key] = amount;
            Prune(days, DateOnly.FromDateTime(measuredAt.UtcDateTime));
            Save();
            return true;
        }
    }

    /// <summary>The last <paramref name="count"/> days for a source, oldest first.</summary>
    /// <remarks>Always exactly that many entries, so the row of bars has a fixed shape and the days
    /// nothing was recorded for are visible as gaps rather than absent.</remarks>
    public IReadOnlyList<UsageDay> Days(string sourceId, DateTimeOffset today, int count)
    {
        lock (_gate)
        {
            var days = Loaded().GetValueOrDefault(sourceId);
            var last = DateOnly.FromDateTime(today.UtcDateTime);

            return [.. Enumerable.Range(0, Math.Max(count, 0))
                .Select(back => last.AddDays(back + 1 - count))
                .Select(date => new UsageDay(date,
                    days is not null && days.TryGetValue(Key(date), out var amount) ? amount : null))];
        }
    }

    /// <summary>The oldest day anything was recorded for a source, or null when nothing was.</summary>
    /// <remarks>What the card's <i>collecting since</i> line is drawn from — the honest way of saying
    /// that the empty days before it are this application's silence rather than the account's.</remarks>
    public DateOnly? CollectingSince(string sourceId)
    {
        lock (_gate)
        {
            var recorded = Loaded().GetValueOrDefault(sourceId)?.Keys
                .Select(Parse).OfType<DateOnly>().ToList();

            return recorded is { Count: > 0 } ? recorded.Min() : null;
        }
    }

    private static string Key(DateOnly date) => date.ToString("yyyy-MM-dd");

    private static DateOnly? Parse(string key) =>
        DateOnly.TryParse(key, out var date) ? date : null;

    private static void Prune(Dictionary<string, decimal> days, DateOnly today)
    {
        var oldest = today.AddDays(-RetainedDays);

        foreach (var key in days.Keys.Where(key => Parse(key) is not { } date || date < oldest).ToList())
            days.Remove(key);
    }

    private Dictionary<string, Dictionary<string, decimal>> Loaded() => _bySource ??= Read();

    private Dictionary<string, Dictionary<string, decimal>> Read()
    {
        try
        {
            return File.Exists(_filePath)
                ? JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, decimal>>>(
                      File.ReadAllText(_filePath)) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Trace.TraceWarning("The usage history could not be read: {0}", ex.Message);
            return [];
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            PrivateFile.WriteAllText(_filePath, JsonSerializer.Serialize(Loaded()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("The usage history could not be written: {0}", ex.Message);
        }
    }
}
