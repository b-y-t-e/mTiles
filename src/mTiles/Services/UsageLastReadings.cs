using System.Diagnostics;
using System.Text.Json;
using mTiles.Models;

namespace mTiles.Services;

/// <summary>One account's last good reading, and when it was taken.</summary>
public sealed record UsageLastReading(AiUsageReport Report, DateTimeOffset TakenAt);

/// <summary>
/// The last good reading of every account, kept across a restart.
/// </summary>
/// <remarks>
/// <para><b>Why a restart needs it.</b> <c>AiUsageService</c> keeps a card standing through a bad round
/// by showing the last good reading — but that reading lived only in memory, so an account being
/// rate-limited when mTiles started had nothing to stand in for it and simply vanished from the tile.
/// Measured 2026-09-23: Anthropic's usage endpoint answered one subscription with
/// <c>Retry-After: 1601</c>, and a restart in that half hour took its card away until the limit
/// lifted.</para>
/// <para>What is stored is what the card showed and nothing more — no token, no key. Owner-only
/// through <see cref="PrivateFile"/> for the reason <see cref="UsageHistory"/> is: it is a record of
/// what somebody's accounts have spent. Fails soft at every step; an unreadable file is a start with
/// nothing held over, which is how every launch before this one began.</para>
/// </remarks>
public sealed class UsageLastReadings
{
    private readonly string _filePath;
    private readonly Lock _gate = new();

    /// <summary>The store in this installation's own directory.</summary>
    public UsageLastReadings() : this(Path.Combine(AppPaths.GetUsageDirectory(), "last-readings.json")) { }

    /// <summary>A store at a given path, which is what a test hands it.</summary>
    public UsageLastReadings(string filePath) => _filePath = filePath;

    /// <summary>What was last saved, keyed by source id — empty when nothing was, or it cannot be read.</summary>
    public IReadOnlyDictionary<string, UsageLastReading> Load()
    {
        lock (_gate)
        {
            try
            {
                return File.Exists(_filePath)
                    ? JsonSerializer.Deserialize<Dictionary<string, UsageLastReading>>(File.ReadAllText(_filePath))
                      ?? []
                    : [];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                           or NotSupportedException)
            {
                Trace.TraceWarning("The last usage readings could not be read: {0}", ex.Message);
                return new Dictionary<string, UsageLastReading>();
            }
        }
    }

    /// <summary>Replaces what is stored with <paramref name="readings"/>.</summary>
    public void Save(IReadOnlyDictionary<string, UsageLastReading> readings)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
                PrivateFile.WriteAllText(_filePath, JsonSerializer.Serialize(readings));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                Trace.TraceWarning("The last usage readings could not be written: {0}", ex.Message);
            }
        }
    }
}
