using mTiles.Services;
using mTiles.Services.Agents;
using mTiles.Services.Database;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// What this workspace puts where its agents look, and — the part that matters — what it takes away.
/// </summary>
/// <remarks>
/// The rule under test is "recompute the set of paths and delete the difference", which exists because
/// codex, pi and agy share <c>.agents/skills</c>: closing one of the three must not take the directory
/// the other two are still reading. See <c>docs/AGENTS-MD-SYNC.md</c>.
/// </remarks>
public sealed class WorkspaceAgentFilesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mtiles-agentfiles-" + Guid.NewGuid().ToString("N"));

    public WorkspaceAgentFilesTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* a temp directory */ }
    }

    private static IAiAgent Agent(string id) => AiAgentCatalog.Find(id)!;

    private string Path_(params string[] parts) => Path.Combine([_dir, .. parts]);

    private const string Skill = DatabaseSkillWriter.SkillName;

    [Fact]
    public void A_skill_reaches_only_the_agents_this_workspace_holds()
    {
        var files = new WorkspaceAgentFiles(_dir);
        files.WriteSkill(Skill, "body");
        files.Follow([Agent("claude")]);

        Assert.True(File.Exists(Path_(".claude", "skills", Skill, "SKILL.md")));
        Assert.False(Directory.Exists(Path_(".opencode")));
        Assert.False(Directory.Exists(Path_(".agents")));
    }

    /// <summary>The whole reason this class exists rather than the database tile doing it.</summary>
    [Fact]
    public void Closing_one_of_the_three_agents_that_share_a_directory_leaves_it_alone()
    {
        var files = new WorkspaceAgentFiles(_dir);
        files.Follow([Agent("pi"), Agent("codex")]);
        files.WriteSkill(Skill, "body");

        files.Follow([Agent("codex")]);

        Assert.True(File.Exists(Path_(".agents", "skills", Skill, "SKILL.md")));
    }

    [Fact]
    public void The_last_agent_of_a_directory_leaving_takes_the_skill_with_it()
    {
        var files = new WorkspaceAgentFiles(_dir);
        files.Follow([Agent("pi"), Agent("claude")]);
        files.WriteSkill(Skill, "body");

        files.Follow([Agent("claude")]);

        Assert.False(Directory.Exists(Path_(".agents", "skills", Skill)));
        Assert.True(File.Exists(Path_(".claude", "skills", Skill, "SKILL.md")));
    }

    /// <summary>An agent arriving later finds the skill already waiting, without anything having to
    /// ask the database tile again.</summary>
    [Fact]
    public void An_agent_added_after_the_skill_was_written_still_gets_it()
    {
        var files = new WorkspaceAgentFiles(_dir);
        files.Follow([Agent("claude")]);
        files.WriteSkill(Skill, "body");

        files.Follow([Agent("claude"), Agent("opencode")]);

        Assert.True(File.Exists(Path_(".opencode", "skills", Skill, "SKILL.md")));
    }

    /// <summary>The security rule: withdrawing reaches every path any agent could read, whatever is in
    /// the workspace now.</summary>
    [Fact]
    public void Withdrawing_a_skill_clears_every_directory_any_agent_reads()
    {
        foreach (var agent in AiAgentCatalog.All)
        {
            var directory = Path.Combine(agent.SkillsDirectory(_dir)!, Skill);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "SKILL.md"), "a live database address");
        }

        new WorkspaceAgentFiles(_dir).RemoveSkill(Skill);

        foreach (var agent in AiAgentCatalog.All)
            Assert.False(Directory.Exists(Path.Combine(agent.SkillsDirectory(_dir)!, Skill)));
    }

    [Fact]
    public void Withdrawing_a_skill_leaves_the_user_own_skills_beside_it()
    {
        var mine = Path_(".claude", "skills", "my-skill");
        Directory.CreateDirectory(mine);
        File.WriteAllText(Path.Combine(mine, "SKILL.md"), "mine");

        WorkspaceAgentFiles.RemoveSkillEverywhere(_dir, Skill);

        Assert.True(File.Exists(Path.Combine(mine, "SKILL.md")));
    }

    /// <summary>A splitter dragged is a layout change, and the unchanged set has to be nothing to do,
    /// or every drag frame rewrites every SKILL.md.</summary>
    [Fact]
    public void An_unchanged_layout_rewrites_nothing()
    {
        var files = new WorkspaceAgentFiles(_dir);
        files.WriteSkill(Skill, "body");
        files.Follow([Agent("claude")]);

        var skill = Path_(".claude", "skills", Skill, "SKILL.md");
        var written = File.GetLastWriteTimeUtc(skill);
        File.SetLastWriteTimeUtc(skill, written.AddDays(-1));

        files.Follow([Agent("claude")]);

        Assert.Equal(written.AddDays(-1), File.GetLastWriteTimeUtc(skill));
    }

    /// <summary>Instruction-file content — CLAUDE.md/AGENTS.md — is no longer this class's concern at
    /// all: it never creates, reconciles or removes either one. That is opt-in per workspace, in
    /// <c>AgentFileSyncEngine</c>/<c>AgentFileSyncCoordinator</c>.</summary>
    [Fact]
    public void No_agent_ever_gets_an_instruction_file_written_for_it_here()
    {
        new WorkspaceAgentFiles(_dir).Follow([Agent("claude"), Agent("codex"), Agent("opencode")]);

        Assert.False(File.Exists(Path_("CLAUDE.md")));
        Assert.False(File.Exists(Path_("AGENTS.md")));
    }

    [Fact]
    public void A_hand_written_instruction_file_is_never_touched()
    {
        var claudeMd = Path_("CLAUDE.md");
        File.WriteAllText(claudeMd, "# My project\n\nRules a person wrote.\n");

        var files = new WorkspaceAgentFiles(_dir);
        files.Follow([Agent("claude")]);
        Assert.Contains("Rules a person wrote", File.ReadAllText(claudeMd));

        files.Follow([Agent("codex")]);
        Assert.Contains("Rules a person wrote", File.ReadAllText(claudeMd));
    }

    // ── The one-time clear-up of what the old writer left behind ──

    /// <summary>Emptied rather than deleted: the old writer created these files where there were none,
    /// so one the user has since committed would otherwise be deleted out of their working tree — the
    /// rule <see cref="GitIgnoreFile"/> already follows for a <c>.gitignore</c> we emptied.</summary>
    [Fact]
    public void The_old_section_is_cut_out_and_the_file_it_emptied_is_left_in_place()
    {
        var stale = Path_("claude.local.md");
        File.WriteAllText(stale, "# Database access\n\nSQL queries via local HTTP bridge. …\n");

        LegacyDatabaseSectionCleanup.Run(_dir);

        Assert.True(File.Exists(stale));
        Assert.DoesNotContain("Database access", File.ReadAllText(stale));
    }

    [Fact]
    public void The_old_database_section_is_cut_out_of_the_canon_and_the_rest_is_kept()
    {
        var canon = Path_("AGENTS.md");
        File.WriteAllText(canon,
            "# Project\n\nHow to build.\n\n# Database access\n\nSQL queries via local HTTP bridge. …\n\n"
            + "# Conventions\n\nNaming.\n");

        LegacyDatabaseSectionCleanup.Run(_dir);

        var content = File.ReadAllText(canon);
        Assert.DoesNotContain("Database access", content);
        Assert.Contains("How to build", content);
        Assert.Contains("Naming", content);
    }

    /// <summary>A heading somebody wrote themselves is theirs: the clear-up looks for the sentence the
    /// old writer wrote, not for the heading it wrote it under.</summary>
    [Fact]
    public void A_database_access_section_the_user_wrote_is_left_alone()
    {
        var canon = Path_("AGENTS.md");
        const string mine = "# Database access\n\nAsk Ola for the credentials.\n";
        File.WriteAllText(canon, mine);

        LegacyDatabaseSectionCleanup.Run(_dir);

        Assert.Equal(mine, File.ReadAllText(canon));
    }

    /// <summary>On Windows <c>claude.local.md</c> is <c>CLAUDE.local.md</c> — somebody's own local
    /// instructions, which the old writer appended to rather than owned.</summary>
    [Fact]
    public void The_old_claude_local_md_keeps_what_a_person_wrote_in_it()
    {
        var stale = Path_("claude.local.md");
        File.WriteAllText(stale,
            "# My notes\n\nThe staging box is flaky.\n\n# Database access\n\n"
            + "SQL queries via local HTTP bridge. …\n");

        LegacyDatabaseSectionCleanup.Run(_dir);

        var content = File.ReadAllText(stale);
        Assert.Contains("The staging box is flaky", content);
        Assert.DoesNotContain("Database access", content);
    }

    /// <summary>The section was renamed twice, and an older spelling left in place keeps a live bridge
    /// address in a committed file after database access has been switched off.</summary>
    [Theory]
    [InlineData("# Database Service")]
    [InlineData("# List databases")]
    public void An_older_spelling_of_the_old_section_is_cut_out_too(string heading)
    {
        var canon = Path_("AGENTS.md");
        File.WriteAllText(canon,
            $"# Project\n\nHow to build.\n\n{heading}\n\n"
            + "- **Sales** (SqlServer, read-only): `GET http://localhost:18090/query/BOX/Sales?sql=SELECT+1`\n");

        LegacyDatabaseSectionCleanup.Run(_dir);

        var content = File.ReadAllText(canon);
        Assert.DoesNotContain("localhost:18090", content);
        Assert.Contains("How to build", content);
    }

    /// <summary>A subsection of the user's own, whose text happens to mention <c>/query/</c> — the
    /// shape that used to be cut in half: the plain search matched the second <c>#</c>, the cut began
    /// mid-line and took their section with it.</summary>
    [Fact]
    public void A_deeper_heading_of_the_same_name_is_not_our_section()
    {
        var canon = Path_("AGENTS.md");
        const string mine =
            "# API\n\nRoutes.\n\n## Database access\n\nThe reporting service exposes /query/ for us.\n";
        File.WriteAllText(canon, mine);

        LegacyDatabaseSectionCleanup.Run(_dir);

        Assert.Equal(mine, File.ReadAllText(canon));
    }

    /// <summary>The edit goes into somebody's repository, so what it does not touch has to come back
    /// out byte for byte: a BOM stays, and content written in another encoding is not decoded as UTF-8
    /// and put back mangled — <see cref="GitIgnoreFile"/>'s "bytes, not text" rule.</summary>
    [Fact]
    public void What_the_clear_up_keeps_is_written_back_byte_for_byte()
    {
        var canon = Path_("AGENTS.md");
        // A BOM, then "# Zasady\n\nZ\xC3\xB3\xC5\x82w." — the last byte before our section is 0xA0, a
        // whitespace character to anything decoding these bytes as text and half a character here.
        byte[] mine =
        [
            0xEF, 0xBB, 0xBF,
            .. "# Zasady\n\nZ"u8.ToArray(), 0xC3, 0xB3, 0xC5, 0x82, .. "w,"u8.ToArray(), 0xC2, 0xA0,
        ];
        byte[] ours = "\n\n# Database access\n\nSQL queries via local HTTP bridge.\n"u8.ToArray();
        File.WriteAllBytes(canon, [.. mine, .. ours]);

        LegacyDatabaseSectionCleanup.Run(_dir);

        Assert.Equal(mine, File.ReadAllBytes(canon));
    }

    // -- The line in the user's .gitignore --

    /// <summary>A <c>SKILL.md</c> carries this machine's port, servers and database names: untracked and
    /// unignored, it waits in every <c>git status</c> for a <c>git add .</c> — which is the first of the
    /// three faults the section became a skill to be rid of.</summary>
    [Fact]
    public async Task The_skill_directory_is_listed_in_the_gitignore()
    {
        Directory.CreateDirectory(Path_(".git"));
        var files = new WorkspaceAgentFiles(_dir);

        files.WriteSkill(Skill, "body");
        files.Follow([Agent("claude"), Agent("codex")]);
        await GitIgnoreEditQueue.Pending;

        var ignore = File.ReadAllText(Path_(".gitignore"));
        Assert.Contains($".claude/skills/{Skill}/", ignore);
        Assert.Contains($".agents/skills/{Skill}/", ignore);
        // Never the skills directory itself: the user keeps their own skills beside ours.
        Assert.DoesNotContain(".claude/skills\n", ignore.Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task Withdrawing_the_skill_takes_its_gitignore_line_with_it()
    {
        Directory.CreateDirectory(Path_(".git"));
        var files = new WorkspaceAgentFiles(_dir);
        files.Follow([Agent("claude")]);
        files.WriteSkill(Skill, "body");

        files.RemoveSkill(Skill);
        await GitIgnoreEditQueue.Pending;

        Assert.DoesNotContain(Skill, File.ReadAllText(Path_(".gitignore")));
    }

    /// <summary>A closing tile is not a withdrawal: the application disposes every tile on its way out,
    /// so unlisting the entry there would edit a tracked <c>.gitignore</c> on every exit and put the
    /// block back on every launch.</summary>
    [Fact]
    public async Task A_closing_tile_takes_the_skill_and_leaves_its_gitignore_line()
    {
        Directory.CreateDirectory(Path_(".git"));
        var files = new WorkspaceAgentFiles(_dir);
        files.Follow([Agent("claude")]);
        files.WriteSkill(Skill, "body");
        await GitIgnoreEditQueue.Pending;

        files.ForgetSkill(Skill);
        await GitIgnoreEditQueue.Pending;

        Assert.False(Directory.Exists(Path_(".claude", "skills", Skill)));
        Assert.Contains($".claude/skills/{Skill}/", File.ReadAllText(Path_(".gitignore")));
    }

    /// <summary>The same rule one level down: an agent tile leaving is not the user withdrawing
    /// database access.</summary>
    [Fact]
    public async Task An_agent_leaving_the_workspace_leaves_the_gitignore_line()
    {
        Directory.CreateDirectory(Path_(".git"));
        var files = new WorkspaceAgentFiles(_dir);
        files.Follow([Agent("claude")]);
        files.WriteSkill(Skill, "body");
        await GitIgnoreEditQueue.Pending;

        files.Follow([]);
        await GitIgnoreEditQueue.Pending;

        Assert.False(Directory.Exists(Path_(".claude", "skills", Skill)));
        Assert.Contains($".claude/skills/{Skill}/", File.ReadAllText(Path_(".gitignore")));
    }

    /// <summary>Shutdown's one wait: the chain is otherwise the only work here nobody joins, and an
    /// edit abandoned mid-write leaves a <c>.gitignore.mtiles-tmp</c> in the user's repository.
    /// </summary>
    [Fact]
    public void Waiting_for_the_ignore_edits_finishes_them()
    {
        Directory.CreateDirectory(Path_(".git"));
        var files = new WorkspaceAgentFiles(_dir);
        files.WriteSkill(Skill, "body");
        files.Follow([Agent("claude")]);

        GitIgnoreEditQueue.WaitForAll(TimeSpan.FromSeconds(10));

        Assert.Contains($".claude/skills/{Skill}/", File.ReadAllText(Path_(".gitignore")));
        Assert.False(File.Exists(Path_(".gitignore.mtiles-tmp")));
    }

    /// <summary>Creating a <c>.gitignore</c> in a folder that is not a repository is the littering the
    /// skill directories themselves are careful to avoid.</summary>
    [Fact]
    public async Task A_workspace_that_is_not_a_repository_gets_no_gitignore()
    {
        var files = new WorkspaceAgentFiles(_dir);
        files.WriteSkill(Skill, "body");
        files.Follow([Agent("claude")]);
        await GitIgnoreEditQueue.Pending;

        Assert.False(File.Exists(Path_(".gitignore")));
    }

    /// <summary>The set difference can only see within one session; this is the other half.</summary>
    /// <remarks>An opencode tile taken out of the layout between two sessions leaves nothing for
    /// <c>Missing</c> to compare against, so its <c>SKILL.md</c> — carrying the bridge's address —
    /// would otherwise wait in the repository for good.</remarks>
    [Fact]
    public void A_skill_left_by_a_previous_session_goes_even_though_no_tile_names_its_directory()
    {
        var orphan = Path_(".opencode", "skills", Skill);
        Directory.CreateDirectory(orphan);
        File.WriteAllText(Path.Combine(orphan, "SKILL.md"), "http://localhost:18090/query/");

        var files = new WorkspaceAgentFiles(_dir);
        files.WriteSkill(Skill, "body");
        files.Follow([Agent("claude")]);

        Assert.False(Directory.Exists(orphan));
        Assert.True(File.Exists(Path_(".claude", "skills", Skill, "SKILL.md")));
    }

    /// <summary>The sweep runs once per skill, so a directory that leaves later is still the set
    /// difference's business and a skill written again is not swept out from under itself.</summary>
    [Fact]
    public void The_sweep_does_not_repeat_and_a_rewritten_skill_stays_where_a_tile_wants_it()
    {
        var files = new WorkspaceAgentFiles(_dir);
        files.WriteSkill(Skill, "body");
        files.Follow([Agent("claude"), Agent("codex")]);

        files.WriteSkill(Skill, "second body");

        Assert.Equal("second body", File.ReadAllText(Path_(".claude", "skills", Skill, "SKILL.md")));
        Assert.Equal("second body", File.ReadAllText(Path_(".agents", "skills", Skill, "SKILL.md")));
    }

    /// <summary>Blank lines in front of the old heading used to send the cut into a loop that never
    /// ended — on the UI thread that opens the workspace.</summary>
    [Fact]
    public void An_old_section_behind_a_run_of_blank_lines_is_cut_out_rather_than_looped_on()
    {
        var canon = Path_("AGENTS.md");
        File.WriteAllText(canon,
            "# Project" + new string(Lf, 20)
            + "# Database access" + Lf + Lf + "SQL queries via local HTTP bridge. …" + Lf);

        LegacyDatabaseSectionCleanup.Run(_dir);

        var content = File.ReadAllText(canon);
        Assert.DoesNotContain("Database access", content);
        Assert.Contains("# Project", content);
    }

    // -- The shim the sync replaced --

    /// <summary>A CLAUDE.md still holding the old one-line import reads to the sync wizard as a file
    /// whose content differs, and picking it as the current one replaces the whole of AGENTS.md with
    /// that line.</summary>
    [Fact]
    public void The_old_instruction_shim_is_removed()
    {
        File.WriteAllText(Path_("AGENTS.md"), "# Project" + Lf + Lf + "How to build." + Lf);
        File.WriteAllText(Path_("CLAUDE.md"), "@AGENTS.md" + Lf);

        LegacyInstructionShimCleanup.Run(_dir);

        Assert.False(File.Exists(Path_("CLAUDE.md")));
        Assert.Contains("How to build", File.ReadAllText(Path_("AGENTS.md")));
    }

    /// <summary>Content says what a file is: anything else in it is the user's own instructions.
    /// </summary>
    [Fact]
    public void A_claude_md_the_user_wrote_is_left_alone()
    {
        File.WriteAllText(Path_("AGENTS.md"), "canon");
        var mine = "@AGENTS.md" + Lf + Lf + "And one thing only Claude Code needs." + Lf;
        File.WriteAllText(Path_("CLAUDE.md"), mine);

        LegacyInstructionShimCleanup.Run(_dir);

        Assert.Equal(mine, File.ReadAllText(Path_("CLAUDE.md")));
    }

    /// <summary>A shim whose target has gone is still a shim: it imports a file that is not there and
    /// holds none of the user's words, and left unrecognised it is seeded into a new AGENTS.md whose
    /// whole content is the circular <c>@AGENTS.md</c> — which codex, pi and agy read as the project's
    /// instructions.</summary>
    [Fact]
    public void A_shim_whose_target_is_gone_is_still_recognised_and_taken_out()
    {
        File.WriteAllText(Path_("CLAUDE.md"), "@AGENTS.md" + Lf);

        Assert.True(LegacyInstructionShimCleanup.IsPresentIn(_dir));

        LegacyInstructionShimCleanup.Run(_dir);

        Assert.False(File.Exists(Path_("CLAUDE.md")));
        Assert.False(File.Exists(Path_("AGENTS.md")));
    }

    private const char Lf = '\n';
}
