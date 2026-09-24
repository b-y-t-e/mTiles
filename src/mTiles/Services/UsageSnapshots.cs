using System.Diagnostics;
using System.Text.Json;
using mTiles.Models;

namespace mTiles.Services;

/// <summary>One account's last good reading, and any refusal still in force, as it goes to disk.</summary>
/// <param name="LastGood">The last answered report, or null for an account refused before it ever
/// answered — kept for its <paramref name="BackoffUntil"/> alone. Never carries an
/// <see cref="AiUsageReport.AccountKey"/>: that is an account's own id, compared in memory and never stored.
/// </param>
/// <param name="LastGoodAt">When it was taken — the clock <c>AiUsageService.MaskLimit</c> runs on.</param>
/// <param name="BackoffUntil">A <c>Retry-After</c> still in force, or null.</param>
public sealed record UsageSnapshot(AiUsageReport? LastGood, DateTimeOffset LastGoodAt,
    DateTimeOffset? BackoffUntil);

/// <summary>
/// The last good reading of every account, kept across a restart.
/// </summary>
/// <remarks>
/// <para><b>Why a restart needs it.</b> A 429 asks for up to half an hour of silence; restarting the
/// application inside that window used to forget both the reading and the request, so the first round
/// asked straight away — one more refusal against the account — and drew no card for it at all.</para>
/// <para>A restored reading is shown only where <c>AiUsageService</c> would have shown it anyway — inside
/// <c>MaskLimit</c> or a refusal still in force — and always stamped with its age
/// (<see cref="AiUsageReport.HeldOver"/>), so a card read the morning after says it is the morning after.
/// </para>
/// <para>Owner-only through <see cref="PrivateFile"/>, for the reason <see cref="UsageHistory"/> is: it
/// is a record of what somebody's accounts have left. Fails soft everywhere — what is lost is a card
/// for the first few minutes.</para>
/// </remarks>
public sealed class UsageSnapshots
{
    private readonly string _filePath;
    private readonly Lock _gate = new();

    public UsageSnapshots() : this(Path.Combine(AppPaths.GetUsageDirectory(), "last.json")) { }

    public UsageSnapshots(string filePath) => _filePath = filePath;

    public IReadOnlyDictionary<string, UsageSnapshot> Load()
    {
        lock (_gate)
        {
            try
            {
                return File.Exists(_filePath)
                    ? JsonSerializer.Deserialize<Dictionary<string, UsageSnapshot>>(File.ReadAllText(_filePath))
                      ?? []
                    : [];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                           or NotSupportedException)
            {
                Trace.TraceWarning("The last usage readings could not be read: {0}", ex.Message);
                return new Dictionary<string, UsageSnapshot>();
            }
        }
    }

    public void Save(IReadOnlyDictionary<string, UsageSnapshot> snapshots)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
                PrivateFile.WriteAllText(_filePath, JsonSerializer.Serialize(snapshots));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                Trace.TraceWarning("The last usage readings could not be written: {0}", ex.Message);
            }
        }
    }
}
