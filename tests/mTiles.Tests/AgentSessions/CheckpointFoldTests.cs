using mTiles.AgentSessions.Conversation;
using mTiles.AgentSessions.Events;
using mTiles.ViewModels.AgentConversation;
using Xunit;

namespace mTiles.Tests.AgentSessions;

/// <summary>
/// A turn's changed files, each folding open on its own. The question the list is read with is "what
/// happened to <em>this</em> file", so that is the one a row answers.
/// </summary>
public class CheckpointFoldTests
{
    private static CheckpointEntry Turn() => new("c1", "c0",
    [
        new ChangedFile("a.cs", FileChangeKind.Modified, 3, 1),
        new ChangedFile("b.cs", FileChangeKind.Added, 9, 0),
        new ChangedFile("new.cs", FileChangeKind.Renamed, 1, 1, "old.cs"),
    ], Restored: false);

    private static CheckpointItemViewModel Build(List<ChangedFile?> asked) =>
        new(Turn(),
            (_, file) =>
            {
                asked.Add(file);
                return Task.FromResult($"@@ -1 +1 @@\n+{file?.Path}");
            },
            _ => Task.CompletedTask);

    private static List<string?> Paths(List<ChangedFile?> asked) => [.. asked.Select(f => f?.Path)];

    [Fact]
    public async Task A_row_asks_for_its_own_file_and_nothing_else()
    {
        var asked = new List<ChangedFile?>();
        var turn = Build(asked);

        await turn.Files[1].ToggleCommand.ExecuteAsync(null);

        Assert.Equal(["b.cs"], Paths(asked));
        Assert.True(turn.Files[1].IsExpanded);
        Assert.False(turn.Files[0].IsExpanded);
        Assert.Empty(turn.Files[0].Diff);
    }

    [Fact]
    public async Task The_diff_is_read_once_however_often_the_row_is_folded()
    {
        var asked = new List<ChangedFile?>();
        var turn = Build(asked);

        await turn.Files[0].ToggleCommand.ExecuteAsync(null);
        await turn.Files[0].ToggleCommand.ExecuteAsync(null);
        await turn.Files[0].ToggleCommand.ExecuteAsync(null);

        Assert.Equal(["a.cs"], Paths(asked));
        Assert.True(turn.Files[0].IsExpanded);
    }

    [Fact]
    public async Task The_header_opens_every_file_and_then_folds_them_all_away()
    {
        var asked = new List<ChangedFile?>();
        var turn = Build(asked);

        await turn.ToggleDiffCommand.ExecuteAsync(null);

        Assert.Equal(3, asked.Count);
        Assert.All(turn.Files, f => Assert.True(f.IsExpanded));
        Assert.True(turn.IsExpanded);

        await turn.ToggleDiffCommand.ExecuteAsync(null);

        Assert.All(turn.Files, f => Assert.False(f.IsExpanded));
        Assert.False(turn.IsExpanded);
    }

    /// <summary>git applies a pathspec before it looks for renames, so the row has to hand over the file
    /// itself — the only thing that knows the name it had before.</summary>
    [Fact]
    public async Task A_renamed_row_asks_with_both_of_its_names()
    {
        var asked = new List<ChangedFile?>();
        var turn = Build(asked);

        await turn.Files[2].ToggleCommand.ExecuteAsync(null);

        var file = Assert.Single(asked);
        Assert.Equal("new.cs", file!.Path);
        Assert.Equal("old.cs", file.OldPath);
    }

    /// <summary>git is run without throwing, so a failure comes back as no patch at all. A row that
    /// opened on a blank space would read as a file nothing happened to, and a read judged by the lines
    /// it drew would be made again every time the row was folded and opened.</summary>
    [Fact]
    public async Task A_read_that_came_back_with_nothing_says_so_and_is_not_made_again()
    {
        var asked = new List<ChangedFile?>();
        var turn = new CheckpointItemViewModel(Turn(),
            (_, file) =>
            {
                asked.Add(file);
                return Task.FromResult("");
            },
            _ => Task.CompletedTask);

        await turn.Files[0].ToggleCommand.ExecuteAsync(null);

        Assert.True(turn.Files[0].HasNothingToShow);

        await turn.Files[0].ToggleCommand.ExecuteAsync(null);
        await turn.Files[0].ToggleCommand.ExecuteAsync(null);

        Assert.Equal(["a.cs"], Paths(asked));
    }

    /// <summary>With a flag of its own the header would point down at a folded-away list.</summary>
    [Fact]
    public async Task The_header_follows_the_rows()
    {
        var turn = Build([]);

        await turn.Files[0].ToggleCommand.ExecuteAsync(null);
        Assert.True(turn.IsExpanded);

        await turn.Files[0].ToggleCommand.ExecuteAsync(null);
        Assert.False(turn.IsExpanded);
    }
}
