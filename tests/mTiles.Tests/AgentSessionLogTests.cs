using System.Text;
using mTiles.Models;
using mTiles.Services.Agents;
using mTiles.Services.Agents.SessionLogs;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The five session-store readers, against the shapes their CLIs were measured writing.
/// </summary>
/// <remarks>
/// <para>Every file built here is a transcription of a real one, read off this machine on 2026-09-18 —
/// Claude Code 2.1.274, codex-cli, opencode 1.18.18, pi 0.84.3 and Grok 1.0.34. That is what these tests
/// are for: the readers parse somebody else's format, so what has to be pinned is the format, and a
/// fixture is the only place a measurement can be written down in a way that fails the build when it
/// stops being true.</para>
/// <para>The naming rules get tests of their own because they are the half that fails <em>silently</em>:
/// a slug wrong by one character finds no directory, which reads exactly like an agent that has never
/// been run in this workspace.</para>
/// </remarks>
public class AgentSessionLogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "mtiles-session-logs-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private string Dir(params string[] parts)
    {
        var path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Writes a fixture exactly as its CLI would, less one detail: the recorded working
    /// directory is spelled with forward slashes.</summary>
    /// <remarks>These CLIs write the path the shell gave them, which on Windows carries backslashes
    /// and therefore a JSON escape. Every reader here compares <em>full</em> paths, and
    /// Path.GetFullPath normalises either spelling to the same place — so the fixtures use the one
    /// that cannot be mangled on its way into the file, and that the comparison is insensitive to
    /// spelling is itself part of what is being asserted: opencode records whichever spelling it
    /// happened to be started in.</remarks>
    private static void Write(string path, string content) =>
        File.WriteAllText(path, content, new UTF8Encoding(false));

    // ---------------------------------------------------------------- Claude Code

    [Fact]
    public async Task Claude_reads_the_context_out_of_the_last_turn()
    {
        // Measured: the session id is the file's own name, and the usage object is Anthropic's. The
        // cache is counted because it is what will be sent again — a turn of 6 669 input against
        // 116 608 cache-read is a conversation of some 124 000 tokens, not of 6 669.
        var project = Dir(".claude", "projects", "D--work-sources-mterminal");
        Write(Path.Combine(project, "328bc7f9-c063-42a5-a349-8b0b1aca386a.jsonl"),
            """
            {"type":"queue-operation","sessionId":"328bc7f9-c063-42a5-a349-8b0b1aca386a"}
            {"type":"assistant","message":{"usage":{"input_tokens":10,"cache_read_input_tokens":20,"cache_creation_input_tokens":0,"output_tokens":5}}}
            {"type":"assistant","message":{"usage":{"input_tokens":6669,"cache_read_input_tokens":116608,"cache_creation_input_tokens":0,"output_tokens":283}}}
            """);

        var log = new ClaudeSessionLog(_ => Path.Combine(_root, ".claude"));
        var reading = await log.ReadLatestAsync(null, @"D:\work\sources\mterminal", DateTimeOffset.MinValue);

        Assert.NotNull(reading);
        Assert.Equal("328bc7f9-c063-42a5-a349-8b0b1aca386a", reading.SessionId);
        Assert.Equal(6669 + 116608 + 283, reading.UsedTokens);
        // Claude Code names neither, and null is what lets the caller fill the window in from the
        // provider rather than draw a bar against a figure nobody gave.
        Assert.Null(reading.ContextWindow);
        Assert.Null(reading.CostUsd);
    }

    [Fact]
    public async Task A_transcript_longer_than_one_block_is_read_from_its_end()
    {
        // Read backwards in 64 KiB blocks: a line straddling a block boundary, CRLF endings, a BOM at
        // the start and a half-written line at the end must all come out as the CLI meant them.
        var project = Dir(".claude", "projects", "D--work-sources-mterminal");
        var filler = "{\"type\":\"user\",\"message\":{\"content\":\"" + new string('x', 1000) + "\"}}";
        var content = new StringBuilder("\uFEFF")
            .Append("{\"type\":\"assistant\",\"message\":{\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}}\r\n");
        for (var i = 0; i < 200; i++) content.Append(filler).Append("\r\n");
        content.Append("{\"type\":\"assistant\",\"message\":{\"model\":\"claude-opus-5\",\"usage\":{\"input_tokens\":700,\"output_tokens\":42}}}\r\n");
        content.Append("{\"type\":\"assistant\",\"mess");
        File.WriteAllText(Path.Combine(project, "big.jsonl"), content.ToString(), new UTF8Encoding(false));

        var log = new ClaudeSessionLog(_ => Path.Combine(_root, ".claude"));
        var reading = await log.ReadAsync(null, @"D:\work\sources\mterminal", "big");

        Assert.NotNull(reading);
        Assert.Equal(742, reading.UsedTokens);
        Assert.Equal("claude-opus-5", reading.Model);
    }

    [Fact]
    public async Task A_turn_that_never_reached_the_model_is_not_a_context_of_nothing()
    {
        // An auth failure writes a usage object of zeroes. Read as the answer it would empty the gauge
        // behind a conversation that is still there, so the turn before it is what stands.
        var project = Dir(".claude", "projects", "D--work-sources-mterminal");
        Write(Path.Combine(project, "aaaaaaaa-0000-0000-0000-000000000001.jsonl"),
            """
            {"type":"assistant","message":{"usage":{"input_tokens":900,"output_tokens":100}}}
            {"type":"assistant","message":{"usage":{"input_tokens":0,"output_tokens":0}}}
            """);

        var log = new ClaudeSessionLog(_ => Path.Combine(_root, ".claude"));
        var reading = await log.ReadLatestAsync(null, @"D:\work\sources\mterminal", DateTimeOffset.MinValue);

        Assert.Equal(1000, reading?.UsedTokens);
    }

    [Theory]
    // Measured against this machine's own ~/.claude/projects: the dot in a directory name goes the same
    // way as the colon and the separator, which is the case a "replace the separators" rule gets wrong.
    [InlineData(@"D:\work\sources\mterminal", "D--work-sources-mterminal")]
    [InlineData(@"D:\work\sources\kursalpha.eu", "D--work-sources-kursalpha-eu")]
    [InlineData(@"C:\Users\andrz", "C--Users-andrz")]
    public void The_project_directory_is_the_path_with_every_other_character_dashed(string path, string slug)
    {
        var log = new ClaudeSessionLog(_ => "root");
        Assert.Equal(Path.Combine("root", "projects", slug), log.WatchDirectory(null, path));
    }

    // ---------------------------------------------------------------- pi

    [Fact]
    public async Task Pi_reads_its_own_spelling_and_adds_the_turns_up()
    {
        // Measured: the directory is wrapped in dashes, the file name carries a stamp before the id, the
        // usage object is pi's own, and the cost is per turn — so it is summed while the tokens are not.
        var project = Dir(".pi", "agent", "sessions", "--D--work-sources-mterminal--");
        Write(Path.Combine(project, "2026-09-01T10-23-04-860Z_01a05c7e-6e9c-7eb2-8e34-4e8a1226621c.jsonl"),
            """
            {"type":"session","version":3,"id":"01a05c7e-6e9c-7eb2-8e34-4e8a1226621c","cwd":"D:/work/sources/mterminal"}
            {"type":"message","message":{"role":"assistant","usage":{"input":100,"output":10,"cacheRead":0,"cacheWrite":0,"cost":{"total":0.01}}}}
            {"type":"message","message":{"role":"assistant","usage":{"input":900,"output":90,"cacheRead":50,"cacheWrite":10,"cost":{"total":0.02}}}}
            """);

        var log = new PiSessionLog(_ => Path.Combine(_root, ".pi", "agent"));
        var reading = await log.ReadLatestAsync(null, @"D:\work\sources\mterminal", DateTimeOffset.MinValue);

        Assert.NotNull(reading);
        Assert.Equal("01a05c7e-6e9c-7eb2-8e34-4e8a1226621c", reading.SessionId);
        Assert.Equal(900 + 90 + 50 + 10, reading.UsedTokens);
        Assert.Equal(0.03m, reading.CostUsd);
    }

    [Fact]
    public async Task Pi_adds_an_appended_turn_to_what_it_has_read_and_counts_an_unfinished_line_once()
    {
        // Read forwards once and then only what was appended. The fixture's last line has no newline
        // yet — the CLI is still writing it — so it is in the first answer and must not be added again
        // when the rest of the file arrives.
        var project = Dir(".pi", "agent", "sessions", "--D--work-sources-mterminal--");
        var transcript = Path.Combine(project, "2026-09-01T10-23-04-860Z_01a05c7e-6e9c-7eb2-8e34-4e8a1226621c.jsonl");
        Write(transcript,
            """
            {"type":"session","version":3,"id":"01a05c7e-6e9c-7eb2-8e34-4e8a1226621c","cwd":"D:/work/sources/mterminal"}
            {"type":"message","message":{"role":"assistant","usage":{"input":100,"output":10,"cacheRead":0,"cacheWrite":0,"cost":{"total":0.01}}}}
            {"type":"message","message":{"role":"assistant","usage":{"input":900,"output":90,"cacheRead":50,"cacheWrite":10,"cost":{"total":0.02}}}}
            """);
        var log = new PiSessionLog(_ => Path.Combine(_root, ".pi", "agent"));
        const string workspace = @"D:\work\sources\mterminal";
        const string id = "01a05c7e-6e9c-7eb2-8e34-4e8a1226621c";

        var before = await log.ReadAsync(null, workspace, id);
        File.AppendAllText(transcript,
            "\n" + """{"type":"message","message":{"role":"assistant","usage":{"input":2000,"output":100,"cacheRead":0,"cacheWrite":0,"cost":{"total":0.04}}}}""" + "\n");
        var after = await log.ReadAsync(null, workspace, id);

        Assert.Equal(0.03m, before?.CostUsd);
        Assert.Equal(0.07m, after?.CostUsd);
        Assert.Equal(2100, after?.UsedTokens);
    }

    [Fact]
    public void Pis_project_directory_is_the_slug_wrapped_in_its_own_dashes()
    {
        // The dashes round it are pi's, not a separator of ours: the directory for D:\work\sources\mterminal
        // is literally --D--work-sources-mterminal--.
        var log = new PiSessionLog(_ => "root");
        Assert.Equal(Path.Combine("root", "sessions", "--C--Users-andrz--"),
            log.WatchDirectory(null, @"C:\Users\andrz"));
    }

    // ---------------------------------------------------------------- Grok

    [Fact]
    public void Groks_directory_is_the_path_url_encoded_rather_than_slugged()
    {
        // Measured against this machine's own ~/.grok/sessions. The colon and the separators survive as
        // themselves, which is the whole difference from the other four — a slug would find nothing.
        Assert.Equal("C%3A%5CUsers%5Candrz", Uri.EscapeDataString(@"C:\Users\andrz"));
    }

    [Fact]
    public async Task Grok_reports_its_last_turn_rather_than_the_running_total()
    {
        // Measured: session.totalTokens is the sum of every turn (33 526 = 16 719 + 16 807) while the
        // context holds only the last one, so reading the total drew the gauge double after two turns.
        const string workspace = @"C:\Users\andrz";
        var conversation = Dir(".grok", "sessions", Uri.EscapeDataString(workspace),
            "01a0afb2-bfea-7333-83ea-c1701122445f");
        Write(Path.Combine(conversation, "usage.json"), """
            {"session":{"totalTokens":33526,"costUsdTicks":423320000,"primaryModelId":"grok-4.6"},
             "turns":[{"totalTokens":16719},{"totalTokens":16807}]}
            """);

        var reading = await new GrokSessionLog(_root)
            .ReadAsync(null, workspace, "01a0afb2-bfea-7333-83ea-c1701122445f");

        Assert.NotNull(reading);
        Assert.Equal(16807, reading.UsedTokens);
        Assert.Equal(0.042332m, reading.CostUsd);
        Assert.Equal("grok-4.6", reading.Model);
    }

    [Fact]
    public async Task A_grok_tile_captures_nothing_and_so_reads_no_store()
    {
        // An Agent tile's ACP session or a Goal run writes into the same directory after this tile starts,
        // and nothing in Grok's store says which is which — so the newest there is not this tile's to take.
        // With no conversation of its own there is nothing to read the store by, so none is wired in.
        const string workspace = @"C:\Users\andrz";
        var startedAt = DateTimeOffset.UtcNow.AddSeconds(-5);
        var agent = new GrokAgent();

        var captured = await agent.CaptureSessionAsync(
            new AiAgentInstance(), new SessionCaptureRequest("grok", workspace, startedAt, Guid.NewGuid().ToString()),
            CancellationToken.None);

        Assert.Null(captured);
        Assert.Null(agent.SessionLog);
    }

    // ---------------------------------------------------------------- opencode

    [Fact]
    public async Task Opencode_finds_the_project_by_its_worktree_rather_than_by_a_hash()
    {
        // Measured: the project id looks like a hash of the path and is not one — SHA-1 of the worktree
        // in every plausible spelling misses it. So the index is read, which is both right today and
        // free the next time opencode changes how it derives the id.
        Write(Path.Combine(Dir(".local", "share", "opencode", "storage", "project"), "4ea88192.json"),
            """{"id":"4ea88192","worktree":"D:/work/sources/kursalpha.eu","vcs":"git"}""");
        Write(Path.Combine(Dir(".local", "share", "opencode", "storage", "session", "4ea88192"),
                "ses_3d1d74b74ffeYuhhLstdW0w25Q.json"),
            """{"id":"ses_3d1d74b74ffeYuhhLstdW0w25Q","directory":"D:/work/sources/kursalpha.eu"}""");
        var messages = Dir(".local", "share", "opencode", "storage", "message",
            "ses_3d1d74b74ffeYuhhLstdW0w25Q");
        Write(Path.Combine(messages, "msg_0001.json"), """{"role":"user"}""");
        Write(Path.Combine(messages, "msg_0002.json"),
            """{"role":"assistant","modelID":"glm-4.7","cost":0.25,"tokens":{"input":13994,"output":250,"reasoning":135,"cache":{"read":475,"write":0}}}""");

        var log = new OpenCodeSessionLog(_ => Path.Combine(_root, ".local", "share"));
        // A different spelling from the index's on purpose: opencode records whichever the shell was
        // started in, and on Windows that is the same place.
        var reading = await log.ReadLatestAsync(null, @"d:\work\sources\kursalpha.eu", DateTimeOffset.MinValue);

        Assert.NotNull(reading);
        Assert.Equal("ses_3d1d74b74ffeYuhhLstdW0w25Q", reading.SessionId);
        // The reasoning tokens are not counted: what occupies the window next turn is what will be sent
        // again, and the thinking is not.
        Assert.Equal(13994 + 250 + 475, reading.UsedTokens);
        Assert.Equal(0.25m, reading.CostUsd);
    }

    [Fact]
    public async Task Opencode_adds_a_new_turn_to_the_turns_it_has_already_read()
    {
        Write(Path.Combine(Dir(".local", "share", "opencode", "storage", "project"), "4ea88192.json"),
            """{"id":"4ea88192","worktree":"D:/work/sources/kursalpha.eu","vcs":"git"}""");
        Write(Path.Combine(Dir(".local", "share", "opencode", "storage", "session", "4ea88192"),
                "ses_a.json"),
            """{"id":"ses_a","directory":"D:/work/sources/kursalpha.eu"}""");
        var messages = Dir(".local", "share", "opencode", "storage", "message", "ses_a");
        var first = Path.Combine(messages, "msg_0001.json");
        Write(first, """{"role":"assistant","modelID":"glm-4.7","cost":0.25,"tokens":{"input":1000,"output":0}}""");
        File.SetLastWriteTimeUtc(first, DateTime.UtcNow.AddMinutes(-2));
        var log = new OpenCodeSessionLog(_ => Path.Combine(_root, ".local", "share"));
        const string workspace = @"D:\work\sources\kursalpha.eu";

        var before = await log.ReadAsync(null, workspace, "ses_a");
        Write(Path.Combine(messages, "msg_0002.json"),
            """{"role":"assistant","modelID":"glm-4.7","cost":0.5,"tokens":{"input":3000,"output":0}}""");
        var after = await log.ReadAsync(null, workspace, "ses_a");

        Assert.Equal(0.25m, before?.CostUsd);
        Assert.Equal(0.75m, after?.CostUsd);
        Assert.Equal(3000, after?.UsedTokens);
    }

    [Fact]
    public async Task A_workspace_opencode_has_never_seen_answers_nothing()
    {
        Dir(".local", "share", "opencode", "storage", "project");

        var log = new OpenCodeSessionLog(_ => Path.Combine(_root, ".local", "share"));

        Assert.Null(log.WatchDirectory(null, @"D:\somewhere\else"));
        Assert.Null(await log.ReadLatestAsync(null, @"D:\somewhere\else", DateTimeOffset.MinValue));
    }

    [Fact]
    public async Task Opencode_sees_a_project_filed_after_the_first_miss()
    {
        // The index is walked only when it has changed, so that a tile in a workspace opencode has never
        // run in does not read and parse every project the user has ever had, every few seconds, for as
        // long as it is open. What must still hold is that the project opencode files a moment later is
        // found: it is a new file in that directory, which is the change the walk is spent on.
        Dir(".local", "share", "opencode", "storage", "project");
        var log = new OpenCodeSessionLog(_ => Path.Combine(_root, ".local", "share"));
        const string workspace = @"D:\work\sources\kursalpha.eu";

        Assert.Null(log.WatchDirectory(null, workspace));

        Write(Path.Combine(Dir(".local", "share", "opencode", "storage", "project"), "4ea88192.json"),
            """{"id":"4ea88192","worktree":"D:/work/sources/kursalpha.eu"}""");
        Write(Path.Combine(Dir(".local", "share", "opencode", "storage", "session", "4ea88192"),
            "ses_a.json"), """{"id":"ses_a"}""");

        Assert.NotNull(log.WatchDirectory(null, workspace));
        Assert.Equal("ses_a", (await log.ReadLatestAsync(null, workspace, DateTimeOffset.MinValue))?.SessionId);
    }

    // ---------------------------------------------------------------- codex

    [Fact]
    public async Task Codex_names_its_own_window_and_reports_the_last_request_not_the_running_total()
    {
        // The one agent of the six that says what window it is working in. And last_token_usage rather
        // than total_token_usage: the second is every token the conversation has ever spent, which runs
        // past the window after a few turns and would draw the gauge as permanently full.
        var day = Dir(".codex", "sessions", "2026", "09", "18");
        Write(Path.Combine(day, "rollout-2026-09-18T10-00-00-01a0a6f6-ab78-7db2-9dd0-459fde90ae0b.jsonl"),
            """
            {"type":"session_meta","payload":{"cwd":"D:/work/sources/mterminal"}}
            {"type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"total_tokens":999999},"last_token_usage":{"total_tokens":17491},"model_context_window":258400}}}
            """);

        var log = new CodexSessionLog(_ => Path.Combine(_root, ".codex", "sessions"));
        var reading = await log.ReadLatestAsync(null, @"D:\work\sources\mterminal", DateTimeOffset.MinValue);

        Assert.NotNull(reading);
        Assert.Equal("01a0a6f6-ab78-7db2-9dd0-459fde90ae0b", reading.SessionId);
        Assert.Equal(17491, reading.UsedTokens);
        Assert.Equal(258400, reading.ContextWindow);
    }

    [Fact]
    public async Task A_codex_rollout_is_read_on_from_where_the_last_read_stopped()
    {
        // Every codex write on the machine asks every codex tile to read again, so a rollout is read only
        // from where the previous read stopped — and a line still being written is read once it is whole.
        var day = Dir(".codex", "sessions", "2026", "09", "18");
        var rollout = Path.Combine(day, "rollout-2026-09-18T10-00-00-01a0a6f6-ab78-7db2-9dd0-459fde90ae0b.jsonl");
        const string id = "01a0a6f6-ab78-7db2-9dd0-459fde90ae0b";
        const string workspace = @"D:\work\sources\mterminal";
        File.WriteAllText(rollout,
            """{"type":"session_meta","payload":{"cwd":"D:/work/sources/mterminal"}}""" + "\n"
            + """{"type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"total_tokens":100},"model_context_window":258400}}}""" + "\n"
            + """{"type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":""");
        var log = new CodexSessionLog(_ => Path.Combine(_root, ".codex", "sessions"));

        Assert.Equal(100, (await log.ReadAsync(null, workspace, id))?.UsedTokens);

        File.AppendAllText(rollout, """{"total_tokens":250}}}}""" + "\n");
        var reading = await log.ReadAsync(null, workspace, id);

        Assert.Equal(250, reading?.UsedTokens);
        Assert.Equal(258400, reading?.ContextWindow);
    }

    [Fact]
    public async Task A_rollout_from_another_workspace_is_not_this_tiles_session()
    {
        // codex files by date and not by project, so the working directory recorded in the file is the
        // only thing that says whose conversation it is.
        var day = Dir(".codex", "sessions", "2026", "09", "18");
        Write(Path.Combine(day, "rollout-2026-09-18T10-00-00-01a0a6f6-ab78-7db2-9dd0-459fde90ae0b.jsonl"),
            """
            {"type":"session_meta","payload":{"cwd":"D:/work/sources/somewhere-else"}}
            {"type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"total_tokens":10}}}}
            """);

        var log = new CodexSessionLog(_ => Path.Combine(_root, ".codex", "sessions"));

        Assert.Null(await log.ReadLatestAsync(null, @"D:\work\sources\mterminal", DateTimeOffset.MinValue));
    }

    [Fact]
    public async Task A_codex_exec_run_or_app_server_session_is_not_adopted()
    {
        // Measured: the metadata line's payload.source is "cli" for the TUI, "exec" for `codex exec` (a
        // Goal tile's run) and "vscode" for an app-server client (an Agent tile's session).
        var day = Dir(".codex", "sessions", "2026", "09", "18");
        var tui = Path.Combine(day, "rollout-2026-09-18T10-00-00-01a0a6f6-0000-0000-0000-000000000001.jsonl");
        Write(tui, """{"type":"session_meta","payload":{"cwd":"D:/work/sources/mterminal","source":"cli"}}""");
        File.SetLastWriteTimeUtc(tui, DateTime.UtcNow.AddMinutes(-5));
        Write(Path.Combine(day, "rollout-2026-09-18T10-01-00-01a0a6f6-0000-0000-0000-000000000002.jsonl"),
            """{"type":"session_meta","payload":{"cwd":"D:/work/sources/mterminal","source":"exec"}}""");
        Write(Path.Combine(day, "rollout-2026-09-18T10-02-00-01a0a6f6-0000-0000-0000-000000000003.jsonl"),
            """{"type":"session_meta","payload":{"cwd":"D:/work/sources/mterminal","source":"vscode"}}""");

        var log = new CodexSessionLog(_ => Path.Combine(_root, ".codex", "sessions"));
        var reading = await log.ReadLatestAsync(null, @"D:\work\sources\mterminal", DateTimeOffset.MinValue);

        Assert.True(log.TellsHeadlessRunsApart);
        Assert.Equal("01a0a6f6-0000-0000-0000-000000000001", reading?.SessionId);
    }

    [Fact]
    public void Only_the_stores_measured_to_mark_headless_runs_say_they_can()
    {
        // pi, opencode and grok write nothing that tells a headless run apart, so a watcher reads them
        // by id alone rather than adopting whatever is newest.
        Assert.True(new ClaudeSessionLog(_ => "root").TellsHeadlessRunsApart);
        Assert.False(new PiSessionLog(_ => "root").TellsHeadlessRunsApart);
        Assert.False(new OpenCodeSessionLog(_ => "root").TellsHeadlessRunsApart);
    }

    // ---------------------------------------------------------------- the rules every reader shares

    [Fact]
    public async Task A_conversation_resumed_after_the_tile_started_is_followed()
    {
        // /resume goes on appending to a conversation begun before the tile was, so it is the last write
        // and not the start that says the user has moved to it.
        var project = Dir(".claude", "projects", "D--work-sources-mterminal");
        var path = Path.Combine(project, "dddddddd-0000-0000-0000-000000000001.jsonl");
        Write(path, """{"type":"assistant","entrypoint":"cli","message":{"usage":{"input_tokens":10,"output_tokens":1}}}""");
        File.SetCreationTimeUtc(path, DateTime.UtcNow.AddDays(-7));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow);

        var log = new ClaudeSessionLog(_ => Path.Combine(_root, ".claude"));
        var reading = await log.ReadLatestAsync(null, @"D:\work\sources\mterminal",
            DateTimeOffset.UtcNow.AddHours(-1));

        Assert.Equal("dddddddd-0000-0000-0000-000000000001", reading?.SessionId);
    }

    [Fact]
    public async Task A_session_older_than_the_tile_is_not_adopted()
    {
        // The rule SessionCapture already follows: these directories hold every conversation the user
        // has ever had in this project, and the newest of them is very often one from last week.
        var project = Dir(".claude", "projects", "D--work-sources-mterminal");
        var path = Path.Combine(project, "bbbbbbbb-0000-0000-0000-000000000001.jsonl");
        Write(path, """{"type":"assistant","message":{"usage":{"input_tokens":10,"output_tokens":1}}}""");
        File.SetCreationTimeUtc(path, DateTime.UtcNow.AddDays(-7));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-7));

        var log = new ClaudeSessionLog(_ => Path.Combine(_root, ".claude"));

        Assert.Null(await log.ReadLatestAsync(null, @"D:\work\sources\mterminal",
            DateTimeOffset.UtcNow.AddHours(-1)));
    }

    [Fact]
    public async Task A_session_another_tile_holds_is_passed_over()
    {
        // Two tiles of one agent in one workspace write into the same directory, so "the newest here" is
        // a question both of them answer identically — and both would answer it with the same file.
        var project = Dir(".claude", "projects", "D--work-sources-mterminal");
        var newer = Path.Combine(project, "cccccccc-0000-0000-0000-000000000002.jsonl");
        Write(newer, """{"type":"assistant","message":{"usage":{"input_tokens":20,"output_tokens":2}}}""");
        var older = Path.Combine(project, "cccccccc-0000-0000-0000-000000000001.jsonl");
        Write(older, """{"type":"assistant","message":{"usage":{"input_tokens":10,"output_tokens":1}}}""");
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddMinutes(-5));

        var log = new ClaudeSessionLog(_ => Path.Combine(_root, ".claude"));
        var reading = await log.ReadLatestAsync(null, @"D:\work\sources\mterminal", DateTimeOffset.MinValue,
            isFree: id => !id.EndsWith("2", StringComparison.Ordinal));

        Assert.Equal("cccccccc-0000-0000-0000-000000000001", reading?.SessionId);
    }

    [Fact]
    public async Task A_headless_run_in_the_same_directory_is_not_adopted()
    {
        // Measured: `claude -p` — a Goal tile's run, an Agent tile's session — files itself beside the
        // interactive conversations and says so on its message lines. Newer or not, it is never the
        // conversation a terminal tile has moved to.
        var project = Dir(".claude", "projects", "D--work-sources-mterminal");
        var headless = Path.Combine(project, "eeeeeeee-0000-0000-0000-000000000002.jsonl");
        Write(headless, """
            {"type":"queue-operation","sessionId":"eeeeeeee-0000-0000-0000-000000000002"}
            {"type":"user","entrypoint":"sdk-cli","message":{"role":"user"}}
            {"type":"assistant","entrypoint":"sdk-cli","message":{"usage":{"input_tokens":20,"output_tokens":2}}}
            """);
        var interactive = Path.Combine(project, "eeeeeeee-0000-0000-0000-000000000001.jsonl");
        Write(interactive, """
            {"type":"user","entrypoint":"cli","message":{"role":"user"}}
            {"type":"assistant","entrypoint":"cli","message":{"usage":{"input_tokens":10,"output_tokens":1}}}
            """);
        File.SetLastWriteTimeUtc(interactive, DateTime.UtcNow.AddMinutes(-5));
        var claimed = new List<string>();

        var log = new ClaudeSessionLog(_ => Path.Combine(_root, ".claude"));
        var reading = await log.ReadLatestAsync(null, @"D:\work\sources\mterminal", DateTimeOffset.MinValue,
            isFree: id => { claimed.Add(id); return true; });

        Assert.Equal("eeeeeeee-0000-0000-0000-000000000001", reading?.SessionId);
        // Passed over before it could be claimed on the way past, or no tile could ever take it.
        Assert.DoesNotContain("eeeeeeee-0000-0000-0000-000000000002", claimed);
    }

    [Fact]
    public async Task A_store_that_is_not_there_answers_null_rather_than_throwing()
    {
        // What a missing store costs is a gauge and a resume. What a thrown exception costs is the tile.
        var log = new ClaudeSessionLog(_ => Path.Combine(_root, "no-such-directory"));

        Assert.Null(await log.ReadLatestAsync(null, @"D:\nowhere", DateTimeOffset.MinValue));
        Assert.Null(await log.ReadAsync(null, @"D:\nowhere", "whatever"));
    }

    [Fact]
    public async Task A_half_written_last_line_does_not_cost_the_reading()
    {
        // The ordinary case, not the exception: the CLI is appending to this file while we read it.
        var project = Dir(".claude", "projects", "D--work-sources-mterminal");
        Write(Path.Combine(project, "dddddddd-0000-0000-0000-000000000001.jsonl"),
            "{\"type\":\"assistant\",\"message\":{\"usage\":{\"input_tokens\":500,\"output_tokens\":50}}}\n"
            + "{\"type\":\"assist");

        var log = new ClaudeSessionLog(_ => Path.Combine(_root, ".claude"));

        Assert.Equal(550, (await log.ReadLatestAsync(null, @"D:\work\sources\mterminal",
            DateTimeOffset.MinValue))?.UsedTokens);
    }

    [Fact]
    public void Every_agent_either_reads_its_store_or_says_why_it_cannot()
    {
        // The point of the port: four answer, and two answer null on purpose. Asserted so that an
        // agent added later is added with a measurement rather than with a silence nobody notices.
        var reading = AiAgentCatalog.All
            .Where(agent => agent.SessionLog is not null)
            .Select(agent => agent.Id)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("claude", reading);
        Assert.Contains("codex", reading);
        Assert.Contains("opencode", reading);
        Assert.Contains("pi", reading);
        // grok's store is readable, but a terminal Grok tile knows no conversation to read it by — see
        // GrokAgent.SessionLog.
        Assert.DoesNotContain("grok", reading);
        // agy keeps its conversations as protobuf blobs in SQLite, with the working directory buried
        // inside them and no token counts anywhere — see AntigravityAgent.SessionLog.
        Assert.DoesNotContain("antigravity", reading);
    }


    [Fact]
    public async Task The_reading_names_the_model_the_turn_actually_ran_on()
    {
        // The instance very often does not: a Claude Code tile on a subscription is configured with no
        // model at all, the CLI picks its own, and the launch therefore had nothing to ask the account
        // how large a window it serves. The transcript is the only place that knows — and it keeps
        // knowing after the user changes model from inside the TUI.
        var project = Dir(".claude", "projects", "D--work-sources-mterminal");
        Write(Path.Combine(project, "eeeeeeee-0000-0000-0000-000000000001.jsonl"),
            """
            {"type":"assistant","message":{"model":"claude-opus-4-5-20251101","usage":{"input_tokens":10,"output_tokens":1}}}
            {"type":"assistant","message":{"model":"claude-opus-5","usage":{"input_tokens":900,"output_tokens":100}}}
            """);

        var log = new ClaudeSessionLog(_ => Path.Combine(_root, ".claude"));
        var reading = await log.ReadLatestAsync(null, @"D:\work\sources\mterminal", DateTimeOffset.MinValue);

        // The last turn's model, not the first: what the window has to be asked about is what it is
        // running on now.
        Assert.Equal("claude-opus-5", reading?.Model);
    }

    [Fact]
    public async Task Opencode_names_its_model_too()
    {
        Write(Path.Combine(Dir(".local", "share", "opencode", "storage", "project"), "p.json"),
            """{"id":"p","worktree":"D:/work/sources/mterminal"}""");
        Write(Path.Combine(Dir(".local", "share", "opencode", "storage", "session", "p"), "ses_x.json"),
            """{"id":"ses_x"}""");
        Write(Path.Combine(Dir(".local", "share", "opencode", "storage", "message", "ses_x"), "msg_1.json"),
            """{"role":"assistant","modelID":"glm-4.7","tokens":{"input":100,"output":10}}""");

        var log = new OpenCodeSessionLog(_ => Path.Combine(_root, ".local", "share"));
        var reading = await log.ReadLatestAsync(null, @"D:\work\sources\mterminal", DateTimeOffset.MinValue);

        Assert.Equal("glm-4.7", reading?.Model);
    }
}
