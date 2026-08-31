using System.Collections.Concurrent;
using mTiles.Models;
using mTiles.Services.Providers;

namespace mTiles.Services.Agents;

/// <summary>
/// The agents this application knows, which of them this machine has, and the one instance each starts
/// life with.
/// </summary>
/// <remarks>
/// <para>One list — adding an agent is a class and a line here, which is the same argument that made
/// <c>ShellTerminalCatalog</c> and the tile registry lists rather than switches.</para>
/// <para><b>Five agents is not a filtered seventeen.</b> What this replaces was a table of binary names
/// and <c>--version</c> arguments, which could say whether something was installed and nothing else.
/// An agent here is a class that knows how to resume its own conversation and how to be told what it
/// may do, and there is no way to write one of those from a row in a settings grid.</para>
/// </remarks>
public static class AiAgentCatalog
{
    /// <summary>Every agent, in the order a chooser should offer them.</summary>
    public static IReadOnlyList<IAiAgent> All { get; } =
    [
        new ClaudeAgent(),
        new OpenCodeAgent(),
        new CodexAgent(),
        new PiAgent(),
        new AntigravityAgent(),
    ];

    /// <summary>The agent a stored id refers to, or null — a tile naming an agent this build does not
    /// have falls back at the call site rather than failing to load.</summary>
    public static IAiAgent? Find(string? agentId) =>
        string.IsNullOrWhiteSpace(agentId)
            ? null
            : All.FirstOrDefault(a => a.Id.Equals(agentId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Where an agent's binary is on this machine, or null when it is not installed.
    /// </summary>
    /// <remarks>
    /// <para>Walks <c>PATH</c> and the handful of places a global npm, go, cargo or per-tool install
    /// puts a binary, because a windowed process does not inherit the <c>PATH</c> a login shell builds
    /// — see <see cref="ExecutableFinder.Anywhere"/>. Four passes over the whole of <c>PATH</c> for a
    /// Windows binary, so it is emphatically not free.</para>
    /// <para><b>The answer is held for <see cref="LocationValidFor"/>.</b> The callers that ask are the
    /// tile chooser and the layout being restored, both on the UI thread and both asking once per
    /// agent — which without this is hundreds of <c>File.Exists</c> calls every time a tile is offered
    /// or a workspace is opened. The same window the AI tools table used, for the same reason: an agent
    /// installed while mTiles is running becomes choosable half a minute later, which nobody notices,
    /// while a chooser that stalls is the one thing this cost shows up as.</para>
    /// </remarks>
    public static string? Locate(IAiAgent agent)
    {
        var now = DateTimeOffset.UtcNow;
        if (Located.TryGetValue(agent.BinaryName, out var known)
            && now - known.Asked < LocationValidFor)
            return known.Path;

        var path = ExecutableFinder.Anywhere(agent.BinaryName);
        Located[agent.BinaryName] = (now, path);
        return path;
    }

    /// <summary>How long a scan's answer stands. See <see cref="Locate"/>.</summary>
    private static readonly TimeSpan LocationValidFor = TimeSpan.FromSeconds(30);

    /// <summary>Keyed by binary name rather than by agent, because that is what was scanned for — two
    /// agents sharing a binary would share the scan. Concurrent because a session capture asks from a
    /// background thread while the chooser asks from the UI one.</summary>
    private static readonly ConcurrentDictionary<string, (DateTimeOffset Asked, string? Path)> Located =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether this instance can be chosen: the agent is installed, and the provider it names is one
    /// this build has and one the user has actually configured.
    /// </summary>
    /// <remarks>
    /// <para>The chooser <em>hides</em> what is unavailable rather than showing it disabled, so this
    /// is a filter and not a decoration. A settings page still wants the reason, which is why the
    /// agent's absence is reported as a null path rather than as a bare false.</para>
    /// <para><b>There is no shorter overload.</b> One that asked only whether the binary was there
    /// read like the availability rule and answered a different question, so a caller reaching for it
    /// would offer an instance pointing at a deleted or incompatible provider — a tile that starts on
    /// the CLI's own configuration, on another account, with nothing on screen saying so.</para>
    /// <para>An instance naming a provider instance that has since been deleted is <b>not</b> offered:
    /// it would launch, silently, on the agent's own configuration — a different account, possibly a
    /// different model — and nothing on screen would say so. An instance naming no provider is a
    /// different thing entirely and is offered, because "the agent's own configuration" is a choice
    /// somebody made and the one every seeded instance starts in.</para>
    /// <para>The compatibility check is here too, and it is the flavor intersection and nothing else —
    /// see <c>AiProviderCatalog.IsCompatible</c> for why codex plus a local server must not be
    /// offered.</para>
    /// </remarks>
    public static bool IsAvailable(AiAgentInstance instance, AppSettings settings)
    {
        // Installed is a fact about the machine; the rest is a fact about the configuration, and lives
        // in one place so that what the chooser hides and what the row explains cannot disagree - see
        // AgentAvailability, which was written after they did.
        if (Find(instance.AgentId) is not { } agent || Locate(agent) is null)
            return false;

        // The overload taking the agent we have just found, rather than the one that finds it again.
        return AgentAvailability.Problem(instance, settings, agent) is null;
    }

    /// <summary>
    /// One instance per agent, as a first run gets them.
    /// </summary>
    /// <remarks>
    /// <para>Every agent has <b>at least one</b> instance, whether or not it is installed: an instance
    /// is configuration, and hiding the row for an agent somebody is about to install would mean the
    /// list changed under them for reasons they could not see. What availability decides is whether it
    /// can be <em>chosen</em>, not whether it exists.</para>
    /// <para>Pure — it returns instances rather than saving them — so the seeding rule can be read in a
    /// test without a settings file behind it, and so the caller decides whether a first run is what
    /// this is.</para>
    /// </remarks>
    public static IReadOnlyList<AiAgentInstance> SeedInstances() => [.. All.Select(SeedInstanceFor)];

    /// <summary>
    /// The instance an agent starts life with — its own name, its own defaults, its own configuration.
    /// </summary>
    /// <remarks>Also what a caller uses while the tile is still resolving its agent by binary name
    /// rather than by instance (stages 4–6): asking an agent what it supports needs an instance, and
    /// the seeded one is the honest answer for a tile that has not been given another.</remarks>
    public static AiAgentInstance SeedInstanceFor(IAiAgent agent) =>
        new() { AgentId = agent.Id, Name = agent.DisplayName };
}
