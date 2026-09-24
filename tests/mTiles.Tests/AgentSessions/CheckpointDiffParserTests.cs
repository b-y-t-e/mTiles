using mTiles.AgentSessions.Checkpoints;
using mTiles.AgentSessions.Events;
using Xunit;

namespace mTiles.Tests.AgentSessions;

/// <summary>What git's two listings of a turn's changes add up to — pure, so no repository.</summary>
public class CheckpointDiffParserTests
{
    [Fact]
    public void Numstat_and_name_status_are_read_together_renames_included()
    {
        var numstat = "3\t1\tsrc/a.cs\0-\t-\timg.png\0" + "0\t0\t\0old.cs\0new.cs\0";
        var nameStatus = "M\0src/a.cs\0A\0img.png\0R100\0old.cs\0new.cs\0";

        Assert.Equal(
        [
            new ChangedFile("img.png", FileChangeKind.Added, 0, 0),
            new ChangedFile("new.cs", FileChangeKind.Renamed, 0, 0, "old.cs"),
            new ChangedFile("src/a.cs", FileChangeKind.Modified, 3, 1),
        ], CheckpointDiffParser.Parse(numstat, nameStatus));
    }
}
