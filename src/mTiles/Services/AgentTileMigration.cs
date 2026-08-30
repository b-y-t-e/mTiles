using System.Diagnostics;
using System.Text.Json.Nodes;
using mTiles.Models;
using mTiles.Services.Agents;
using mTiles.Services.Tiles;

namespace mTiles.Services;

/// <summary>
/// Turns the terminal tiles that were only ever an AI CLI in a shell into agent tiles.
/// </summary>
/// <remarks>
/// <para>Without this an existing installation gets no agent tile at all. Every AI tile anybody has
/// today is a terminal whose <c>userProfileId</c> names one of the four seeded profiles — Claude Code,
/// OpenCode, Codex, Pi Agent — and the profiles are what this stage removes, so those leaves would come
/// back on the next launch as plain shells with no startup script and no conversation to resume.</para>
/// <para><b>Matched by the profile's required binary, not by its name.</b> The name is the user's to
/// change and several have; the binary is what the profile was filtering on and it is the same string
/// the agent answers with. A profile naming no binary, or one nothing here recognises, is left as a
/// terminal — a shell tile losing a script the user wrote is a smaller loss than an agent tile started
/// on flags they never asked for.</para>
/// <para><b>This code has an expiry date.</b> One release after the profiles are gone there is nothing
/// left on anybody's disk for it to find; the copy it asks for (<c>{id}.pre-agents.json</c>) is what
/// makes deleting it safe.</para>
/// </remarks>
public static class AgentTileMigration
{
    /// <summary>
    /// Rewrites every leaf that a seeded AI profile made, in place.
    /// </summary>
    /// <returns>Whether anything changed — which is what tells the caller to keep a copy of the file as
    /// it was and to write the new one.</returns>
    public static bool Apply(TileNode? root, AppSettings settings)
    {
        if (root is null) return false;

        var changed = false;

        foreach (var leaf in Leaves(root))
        {
            if (AgentFor(leaf, settings) is not { } agent) continue;

            var instance = InstanceFor(agent, settings);

            // Mutated rather than replaced: `TileNode.Settings` merges what it is given, so assigning a
            // fresh object would leave the profile id sitting in the file beside the agent's keys — and
            // a leaf that still names a profile is one the next release's reader has to keep explaining
            // away. The shell name stays where it is, because the rollback rule reads an agent leaf as a
            // terminal on the shell it was running.
            var state = leaf.Settings!;
            state.Remove(TerminalTileKind.UserProfileIdKey);
            state[AgentTileKind.InstanceIdKey] = instance.Id;
            state[AgentTileKind.AgentIdKey] = agent.Id;

            leaf.Kind = TileKindIds.Agent;

            changed = true;
            Trace.TraceInformation("Tile {0} was a {1} profile and is now an agent tile.",
                leaf.TileId, agent.DisplayName);
        }

        return changed;
    }

    /// <summary>The agent a leaf's shell profile was starting, or null when it was not starting one.
    /// </summary>
    private static IAiAgent? AgentFor(TileNode leaf, AppSettings settings)
    {
        if (!leaf.IsLeaf || leaf.Kind != TileKindIds.Terminal) return null;

        var profileId = leaf.Settings?[TerminalTileKind.UserProfileIdKey]?.GetValue<string>();
        if (string.IsNullOrEmpty(profileId)) return null;

        var profile = settings.ShellProfiles.FirstOrDefault(p => p.Id == profileId);
        if (profile is null || profile.RequiredAiToolBinaryName is not { Length: > 0 } binary)
            return null;

        return AiAgentCatalog.All.FirstOrDefault(
            agent => agent.BinaryName.Equals(binary, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The instance a migrated tile runs on: the first one configured for that agent.</summary>
    /// <remarks>There is always one — <c>SettingsService</c> seeds one per agent and never replaces it —
    /// and the seeded fallback here is for the settings file that has not been through that yet, so the
    /// order the two run in cannot decide whether a tile survives.</remarks>
    private static AiAgentInstance InstanceFor(IAiAgent agent, AppSettings settings) =>
        settings.AiAgentInstances.FirstOrDefault(i => i.AgentId == agent.Id)
        ?? AiAgentCatalog.SeedInstanceFor(agent);

    /// <summary>Every leaf of the tree, splits walked rather than assumed to be one deep.</summary>
    private static IEnumerable<TileNode> Leaves(TileNode node)
    {
        if (node.IsLeaf)
        {
            yield return node;
            yield break;
        }

        foreach (var child in new[] { node.First, node.Second }.OfType<TileNode>())
            foreach (var leaf in Leaves(child))
                yield return leaf;
    }
}
