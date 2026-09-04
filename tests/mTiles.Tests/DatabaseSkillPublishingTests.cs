using System.Net;
using System.Net.Sockets;
using mTiles.Models;
using mTiles.Services;
using mTiles.Services.Agents;
using mTiles.Services.Database;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// When the database skill is published, and — the part that costs somebody's repository — when its
/// line in the <c>.gitignore</c> is taken back out.
/// </summary>
/// <remarks>
/// There are three answers, not two. A skill is written; a skill is <em>withdrawn</em>, which is a
/// decision (the service switched off, the last database unticked) and takes the line with it; and a
/// skill simply cannot be built yet, because discovery runs on a timer off the thread pool while a
/// restored tile publishes from its own constructor. Reading the third as the second edited a tracked
/// <c>.gitignore</c> on every launch and left the skill missing for the rest of the session.
/// </remarks>
public sealed class DatabaseSkillPublishingTests : IDisposable
{
    private readonly TempSettings _settings = new();
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "mtiles-dbskill-" + Guid.NewGuid().ToString("N"));
    private readonly DatabaseServiceManager _manager;
    private readonly WorkspaceAgentFiles _agentFiles;

    private const string Skill = DatabaseSkillWriter.SkillName;
    private const string Key = "localhost/shop";

    public DatabaseSkillPublishingTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, ".git"));   // ignoring only happens in a repository

        var db = _settings.Service.Settings.Database;
        db.Enabled = true;
        db.HttpPort = FreePort();
        db.SqlServer.Enabled = false;                            // no discovery: this test provides the
        db.PostgreSql.Enabled = false;                           // registry's answers itself

        _manager = new DatabaseServiceManager(_settings.Service);
        _manager.Start();
        Assert.True(_manager.IsRunning, _manager.LastError);

        _agentFiles = new WorkspaceAgentFiles(_dir);
        _agentFiles.Follow([AiAgentCatalog.Find("claude")!]);
    }

    public void Dispose()
    {
        _manager.Dispose();
        _settings.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* a temp directory */ }
    }

    /// <summary>A port nothing else on this machine holds, so a busy 18090 cannot fail the run.
    /// </summary>
    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static List<WorkspaceDatabaseConfig> Selected() => [new() { DatabaseKey = Key }];

    private void RegistryFinds() => _manager.Registry.Register(new DatabaseInstance
    {
        Server = "localhost",
        Database = "shop",
        Provider = DbProviderType.PostgreSQL,
        ConnectionString = "Host=localhost;Database=shop"
    });

    private string SkillFile => Path.Combine(_dir, ".claude", "skills", Skill, "SKILL.md");
    private string IgnoreFile => Path.Combine(_dir, ".gitignore");

    [Fact]
    public async Task A_database_the_registry_knows_is_offered_and_its_line_is_written()
    {
        RegistryFinds();

        _manager.UpdateDatabaseSkill(_agentFiles, Selected());
        await GitIgnoreEditQueue.Pending;

        Assert.Contains("localhost/shop", File.ReadAllText(SkillFile));
        Assert.Contains($".claude/skills/{Skill}/", File.ReadAllText(IgnoreFile));
    }

    /// <summary>The finding this class was written for: at startup the tile publishes before discovery
    /// has registered anything, and that must not read as the user withdrawing access.</summary>
    [Fact]
    public async Task A_registry_that_has_not_answered_yet_keeps_the_gitignore_line()
    {
        RegistryFinds();
        _manager.UpdateDatabaseSkill(_agentFiles, Selected());
        await GitIgnoreEditQueue.Pending;

        _manager.Registry.Remove(Key);                      // as at startup: selected, but unknown
        _manager.UpdateDatabaseSkill(_agentFiles, Selected());
        await GitIgnoreEditQueue.Pending;

        // The SKILL.md still goes — it would name an address the bridge is not publishing.
        Assert.False(File.Exists(SkillFile));
        Assert.Contains($".claude/skills/{Skill}/", File.ReadAllText(IgnoreFile));
    }

    /// <summary>And nothing is lost by waiting: the registry answers, the tile asks again, the skill is
    /// back.</summary>
    [Fact]
    public void A_database_the_registry_finds_later_gets_its_skill_back()
    {
        _manager.UpdateDatabaseSkill(_agentFiles, Selected());
        Assert.False(File.Exists(SkillFile));

        RegistryFinds();
        _manager.UpdateDatabaseSkill(_agentFiles, Selected());

        Assert.Contains("localhost/shop", File.ReadAllText(SkillFile));
    }

    /// <summary>The other half of the rule, unchanged: unticking the last database is a decision, so
    /// the line goes with the skill.</summary>
    [Fact]
    public async Task Unticking_the_last_database_takes_the_gitignore_line_with_it()
    {
        RegistryFinds();
        _manager.UpdateDatabaseSkill(_agentFiles, Selected());
        await GitIgnoreEditQueue.Pending;

        _manager.UpdateDatabaseSkill(_agentFiles, []);
        await GitIgnoreEditQueue.Pending;

        Assert.False(File.Exists(SkillFile));
        Assert.DoesNotContain(Skill, File.ReadAllText(IgnoreFile));
    }
}
