using mTiles.Services;
using mTiles.Services.Agents;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The two payloads a subscription's limits are read out of, recorded as they were measured.
/// </summary>
/// <remarks>
/// Both formats are somebody else's and both are undocumented — Anthropic's <c>api/oauth/usage</c> and
/// codex's own rollout file. What these pin is not the services' behaviour, which nothing here controls,
/// but this application's: a renamed field, a truncated document and a body that is not JSON at all each
/// answer with a sentence or a null, and never with a zero that reads as an account that has run out.
/// </remarks>
public class AiUsageParseTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private const string ClaudePayload = """
        {
          "five_hour":  { "utilization": 11.0, "resets_at": "2026-09-01T15:00:00Z" },
          "seven_day":  { "utilization": 2.5,  "resets_at": "2026-09-04T10:00:00Z" }
        }
        """;

    [Fact]
    public void TheClaudeAnswerBecomesTwoWindows()
    {
        var report = ClaudeUsageReader.Parse(ClaudePayload, "claude", "Claude Code", "Max", Now);

        Assert.True(report.Answered);
        Assert.Equal("Max", report.Plan);
        Assert.Collection(report.Windows,
            five =>
            {
                Assert.Equal("5h", five.Label);
                Assert.Equal(11.0, five.UsedPercent);
                Assert.Equal(new DateTimeOffset(2026, 9, 1, 15, 0, 0, TimeSpan.Zero), five.ResetsAt);
            },
            seven =>
            {
                Assert.Equal("7d", seven.Label);
                Assert.Equal(2.5, seven.UsedPercent);
            });
    }

    /// <summary>A window whose field moved is left out, not drawn as a bar at zero.</summary>
    [Fact]
    public void ARenamedWindowIsLeftOutRatherThanShownEmpty()
    {
        var report = ClaudeUsageReader.Parse(
            """{ "five_hour": { "utilisation": 11.0 }, "seven_day": { "utilization": 2.5 } }""",
            "claude", "Claude Code", null, Now);

        Assert.True(report.Answered);
        Assert.Equal("7d", Assert.Single(report.Windows).Label);
    }

    /// <summary>A document carrying neither window is a format that has moved, and it says so.</summary>
    [Fact]
    public void AnAnswerWithNoRecognisedWindowIsAProblem()
    {
        var report = ClaudeUsageReader.Parse("""{ "quota": {} }""", "claude", "Claude Code", null, Now);

        Assert.False(report.Answered);
        Assert.NotEmpty(report.Problem!);
        Assert.Empty(report.Windows);
    }

    [Fact]
    public void ATruncatedAnswerIsAProblemRatherThanAThrow()
    {
        var report = ClaudeUsageReader.Parse("""{ "five_hour": { "utiliz""", "claude", "Claude Code",
            null, Now);

        Assert.False(report.Answered);
        Assert.Empty(report.Windows);
    }

    private const string CodexLine = """
        {"timestamp":1756728000,"type":"token_count","payload":{"rate_limits":{
          "primary":   {"used_percent":34.2,"window_minutes":300,"resets_in_seconds":3600},
          "secondary": {"used_percent":12.0,"window_minutes":10080,"resets_in_seconds":86400}},
          "credits":{"balance":7.5}}}
        """;

    [Fact]
    public void TheCodexLineBecomesTwoWindowsLabelledByLength()
    {
        var report = CodexUsageReader.Parse(CodexLine, "codex", "codex", Now);

        Assert.NotNull(report);
        Assert.True(report!.Answered);
        Assert.Equal(["5h", "7d"], report.Windows.Select(window => window.Label));
        Assert.Equal(34.2, report.Windows[0].UsedPercent);
        Assert.Equal(7.5m, report.RemainingCredit);
    }

    /// <summary>The event's own timestamp, because the figures are as fresh as the last reply and no
    /// fresher.</summary>
    [Fact]
    public void TheReadingIsStampedWithTheEventRatherThanTheRead()
    {
        var report = CodexUsageReader.Parse(CodexLine, "codex", "codex", Now);

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1756728000), report!.MeasuredAt);
    }

    /// <summary>The reset is counted from the reading, not from the moment of the read.</summary>
    [Fact]
    public void ACountdownIsResolvedAgainstTheReadingsOwnInstant()
    {
        var report = CodexUsageReader.Parse(CodexLine, "codex", "codex", Now);

        Assert.Equal(report!.MeasuredAt.AddHours(1), report.Windows[0].ResetsAt);
    }

    [Fact]
    public void ALineWithoutLimitsIsNothingRatherThanAnEmptyCard() =>
        Assert.Null(CodexUsageReader.Parse("""{"type":"message","payload":{}}""", "codex", "codex", Now));

    [Fact]
    public void ALineThatIsNotJsonIsNothingRatherThanAThrow() =>
        Assert.Null(CodexUsageReader.Parse("rollout truncated mid-", "codex", "codex", Now));

    [Fact]
    public void NoLineAtAllIsNothing() =>
        Assert.Null(CodexUsageReader.Parse(null, "codex", "codex", Now));

    /// <summary>A window whose length codex does not state has no name and no pace, rather than a
    /// guessed week.</summary>
    [Fact]
    public void AWindowWithNoStatedLengthIsUnnamed()
    {
        var report = CodexUsageReader.Parse(
            """{"rate_limits":{"primary":{"used_percent":5.0}}}""", "codex", "codex", Now);

        Assert.Equal("", Assert.Single(report!.Windows).Label);
        Assert.Equal(TimeSpan.Zero, report.Windows[0].Length);
    }

    /// <summary>A large number is a unix second; a small one is a countdown from the reading.</summary>
    /// <remarks>No limit window is 31 million seconds long and no reset lands before this application
    /// existed, so the two readings cannot be confused in any case that occurs.</remarks>
    [Fact]
    public void ALargeNumberIsAUnixSecond()
    {
        using var document = System.Text.Json.JsonDocument.Parse("""{ "at": 1756728000 }""");

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_756_728_000),
            UsageInstant.From(document.RootElement, "at", Now));
    }

    [Fact]
    public void ASmallNumberIsACountdownFromTheReading()
    {
        using var document = System.Text.Json.JsonDocument.Parse("""{ "at": 300 }""");

        Assert.Equal(Now.AddMinutes(5), UsageInstant.From(document.RootElement, "at", Now));
    }

    [Fact]
    public void AnUnreadableInstantIsNullRatherThanTheYear1601()
    {
        using var document = System.Text.Json.JsonDocument.Parse("""{ "at": "soon" }""");

        Assert.Null(UsageInstant.From(document.RootElement, "at", Now));
    }

    /// <summary>
    /// A rollout that merely mentions the name must not stand in for the reading.
    /// </summary>
    /// <remarks><c>rate_limits</c> is a substring, and a conversation about rate limits puts it in a
    /// message event. Keeping the last line that <em>contains</em> it handed that message to the parser,
    /// got null back and reported "no limits" while the figures sat a line above.</remarks>
    [Fact]
    public void ALineThatOnlyMentionsTheNameDoesNotMaskTheReading()
    {
        using var codex = new TempCodexHome();
        codex.Rollout("session-1", DateTime.UtcNow,
            CodexEventLine,
            """{"type":"message","payload":{"content":"why does rate_limits say that"}}""");

        var report = CodexUsageReader.Read("codex", "Codex", codex.SessionsRoot, Now);

        Assert.True(report.Answered);
        Assert.Equal(["5h", "7d"], report.Windows.Select(window => window.Label));
    }

    /// <summary>
    /// A session opened a minute ago has no reading yet, and the one before it does.
    /// </summary>
    /// <remarks>Asking the newest file alone threw away perfectly good figures the moment a codex tile
    /// was opened: the file exists from the first event and the limits are stated only after a reply.
    /// </remarks>
    [Fact]
    public void AFreshSessionWithNoReadingFallsBackToTheOneBeforeIt()
    {
        using var codex = new TempCodexHome();
        codex.Rollout("older", DateTime.UtcNow.AddMinutes(-30), CodexEventLine);
        codex.Rollout("newest", DateTime.UtcNow, """{"type":"session_meta","payload":{}}""");

        var report = CodexUsageReader.Read("codex", "Codex", codex.SessionsRoot, Now);

        Assert.True(report.Answered);
        Assert.Equal(34.2, report.Windows[0].UsedPercent);
    }

    /// <summary>A machine where codex has run and never reported limits says so, and does not throw.</summary>
    [Fact]
    public void SessionsWithNoReadingAtAllSayWhy()
    {
        using var codex = new TempCodexHome();
        codex.Rollout("one", DateTime.UtcNow, """{"type":"session_meta","payload":{}}""");

        var report = CodexUsageReader.Read("codex", "Codex", codex.SessionsRoot, Now);

        Assert.False(report.Answered);
        Assert.Contains("only after a reply", report.Problem);
    }

    /// <summary>A codex home nothing has been written into is no account, not a broken one.</summary>
    [Fact]
    public void ACodexHomeWithNoSessionsSaysThatInstead()
    {
        using var codex = new TempCodexHome();

        var report = CodexUsageReader.Read("codex", "Codex", codex.SessionsRoot, Now);

        Assert.False(report.Answered);
        Assert.Contains("has not written a session", report.Problem);
    }

    /// <summary>The sample as codex writes it: one event on one line.</summary>
    private const string CodexEventLine =
        """{"timestamp":1756728000,"type":"token_count","payload":{"rate_limits":{"primary":{"used_percent":34.2,"window_minutes":300,"resets_in_seconds":3600},"secondary":{"used_percent":12.0,"window_minutes":10080,"resets_in_seconds":86400}},"credits":{"balance":7.5}}}""";

    /// <summary>A throwaway <c>CODEX_HOME</c> with rollouts in it, stamped so "newest" is a fact.</summary>
    private sealed class TempCodexHome : IDisposable
    {
        private readonly string _root =
            Path.Combine(Path.GetTempPath(), $"mtiles-codex-{Guid.NewGuid():N}");

        public string SessionsRoot => Path.Combine(_root, "sessions");

        public void Rollout(string name, DateTime writtenUtc, params string[] lines)
        {
            var directory = Path.Combine(SessionsRoot, "2026", "09");
            Directory.CreateDirectory(directory);

            var path = Path.Combine(directory, $"rollout-{name}.jsonl");
            File.WriteAllLines(path, lines);
            File.SetLastWriteTimeUtc(path, writtenUtc);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
            catch (IOException) { }
        }
    }
}
