using System.Text.Json.Nodes;
using mTiles.Services.Agents;
using Xunit;

namespace mTiles.Tests;

public class ClaudeDiffPanelTests
{
    private static string TempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"mtiles-claude-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    [Fact]
    public void A_panel_opened_last_session_starts_closed_and_nothing_else_moves()
    {
        var file = Path.Combine(TempDirectory(), ".claude.json");
        File.WriteAllText(file, """{ "numStartups": 12, "diffSidebarOpen": true, "projects": { "a": { "x": 1 } } }""");

        ClaudeDiffPanel.KeepClosed(file);

        var document = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        Assert.False(document["diffSidebarOpen"]!.GetValue<bool>());
        Assert.Equal(12, document["numStartups"]!.GetValue<int>());
        Assert.Equal(1, document["projects"]!["a"]!["x"]!.GetValue<int>());
    }

    [Fact]
    public void A_file_already_saying_closed_is_not_rewritten()
    {
        var file = Path.Combine(TempDirectory(), ".claude.json");
        const string text = """{"diffSidebarOpen": false,   "keep":"exactly"}""";
        File.WriteAllText(file, text);

        ClaudeDiffPanel.KeepClosed(file);

        Assert.Equal(text, File.ReadAllText(file));
    }

    [Fact]
    public void A_sign_in_the_cli_has_not_written_yet_gets_the_one_key()
    {
        var file = Path.Combine(TempDirectory(), ".claude.json");

        ClaudeDiffPanel.KeepClosed(file);

        Assert.False(JsonNode.Parse(File.ReadAllText(file))!["diffSidebarOpen"]!.GetValue<bool>());
    }

    [Fact]
    public void A_directory_that_is_not_there_is_left_alone()
    {
        var file = Path.Combine(Path.GetTempPath(), $"mtiles-missing-{Guid.NewGuid():N}", ".claude.json");

        ClaudeDiffPanel.KeepClosed(file);

        Assert.False(File.Exists(file));
    }
}
