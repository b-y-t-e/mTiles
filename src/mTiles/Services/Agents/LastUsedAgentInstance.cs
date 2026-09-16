using mTiles.Models;

namespace mTiles.Services.Agents;

/// <summary>The instance a new Agent tile opens on: the one the last Agent tile was using.</summary>
public static class LastUsedAgentInstance
{
    /// <summary>Makes <paramref name="instance"/> the one a new Agent tile opens on.</summary>
    /// <remarks>Asked when a message is sent as well as when the chooser moves: a tile restored from a layout and
    /// used all day without touching the chooser is still the instance in use.</remarks>
    public static void Remember(SettingsService settings, AiAgentInstance instance)
    {
        if (settings.Settings.LastAgentInstanceId == instance.Id) return;
        settings.Settings.LastAgentInstanceId = instance.Id;
        settings.DebouncedSave();
    }
}
