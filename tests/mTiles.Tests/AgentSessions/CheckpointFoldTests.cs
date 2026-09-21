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

    /// <summary>
    /// The summary's chevron folds the list, which is the reading a chevron above a list has. It used
    /// to open every file's diff instead — tens of thousands of rows and one git process per file on a
    /// large turn, stopped part way by a budget that nothing on screen accounted for.
    /// </summary>
    [Fact]
    public void The_header_folds_the_list_and_reads_nothing()
    {
        var asked = new List<ChangedFile?>();
        var turn = Build(asked);

        // Folded to begin with: the line already says how many files changed and by how much, and a
        // turn of thirty paths otherwise stands between the reply and whatever was said next.
        Assert.False(turn.IsExpanded);

        turn.ToggleDiffCommand.Execute(null);

        Assert.True(turn.IsExpanded);
        Assert.Empty(asked);
        Assert.All(turn.Files, f => Assert.False(f.IsExpanded));

        turn.ToggleDiffCommand.Execute(null);

        Assert.False(turn.IsExpanded);
        Assert.Empty(asked);
    }

    /// <summary>Folding the summary away is about the room on screen, not about each file.</summary>
    [Fact]
    public async Task A_file_left_open_is_still_open_when_the_list_comes_back()
    {
        var asked = new List<ChangedFile?>();
        var turn = Build(asked);

        await turn.Files[1].ToggleCommand.ExecuteAsync(null);
        Assert.True(turn.AnyFileIsOpen);

        turn.ToggleDiffCommand.Execute(null);
        turn.ToggleDiffCommand.Execute(null);

        Assert.True(turn.Files[1].IsExpanded);
        Assert.Single(asked);
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

    /// <summary>What the rows say, for the tooltip that explains what folding the list hides.</summary>
    [Fact]
    public async Task Any_file_open_follows_the_rows()
    {
        var turn = Build([]);

        Assert.False(turn.AnyFileIsOpen);

        await turn.Files[0].ToggleCommand.ExecuteAsync(null);
        Assert.True(turn.AnyFileIsOpen);

        await turn.Files[0].ToggleCommand.ExecuteAsync(null);
        Assert.False(turn.AnyFileIsOpen);
    }
}
