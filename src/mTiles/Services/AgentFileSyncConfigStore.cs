using System.Diagnostics;
using System.Text.Json;
using mTiles.Models;

namespace mTiles.Services;

/// <summary>
/// Reads and writes one workspace's answer to the CLAUDE.md/AGENTS.md question, at
/// <c>.mtiles/agent-file-sync.json</c>.
/// </summary>
/// <remarks>
/// <para>A class of its own because it is the one part of the feature with its own reason to change —
/// where the answer lives and what shape it is on disk — while <see cref="AgentFileSyncCoordinator"/>
/// changes when the rules for asking do.</para>
/// <para>Every read answers, and an unreadable file answers "never asked" rather than throwing: this is
/// reached from a fire-and-forget evaluation on every tile-tree change, so an exception here would
/// surface as nothing more than an unobserved task. The cost of the fallback is one wizard the user has
/// already answered, which is recoverable; the cost of throwing is a silent dead evaluation.</para>
/// </remarks>
public static class AgentFileSyncConfigStore
{
    private const string ConfigFileName = "agent-file-sync.json";

    public static string PathFor(string workspaceDir) =>
        WorkspacePaths.Combine(workspaceDir, ConfigFileName);

    public static WorkspaceAgentFileSyncConfig Load(string workspaceDir)
    {
        try
        {
            var path = PathFor(workspaceDir);
            if (!File.Exists(path)) return new WorkspaceAgentFileSyncConfig();
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<WorkspaceAgentFileSyncConfig>(json, JsonDefaults.Options)
                   ?? new WorkspaceAgentFileSyncConfig();
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Could not read agent file sync config for '{0}': {1}", workspaceDir,
                ex.Message);
            return new WorkspaceAgentFileSyncConfig();
        }
    }

    public static void Save(string workspaceDir, WorkspaceAgentFileSyncConfig config)
    {
        try
        {
            var path = PathFor(workspaceDir);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(config, JsonDefaults.Options));
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Could not save agent file sync config for '{0}': {1}", workspaceDir,
                ex.Message);
        }
    }
}
