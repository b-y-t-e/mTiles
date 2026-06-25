using System.Text.Json;
using mTiles.Models;

namespace mTiles.Services;

internal sealed class GoalStatePersistence
{
    public void Save(string filePath, GoalTileState state)
    {
        var dir = Path.GetDirectoryName(filePath)!;
        Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(state, JsonDefaults.Options);
        File.WriteAllText(filePath, json);
    }

    public GoalTileState? Load(string filePath)
    {
        if (!File.Exists(filePath)) return null;
        var json = File.ReadAllText(filePath);
        return JsonSerializer.Deserialize<GoalTileState>(json, JsonDefaults.Options);
    }
}
