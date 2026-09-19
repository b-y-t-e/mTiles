using System;
using System.IO;
using mTiles.Services;
using Xunit;

namespace mTiles.Tests;

public class SentMessagesFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mtiles-sent-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void What_was_sent_is_read_back_by_the_next_instance_in_order()
    {
        var path = Path.Combine(_dir, "goal.sent.json");
        var first = new SentMessagesFile(path);
        first.Add("  one ");
        first.Add("   ");
        first.Add("two");

        Assert.Equal(["one", "two"], new SentMessagesFile(path).Entries);
    }

    [Fact]
    public void An_unreadable_file_is_an_empty_history()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "goal.sent.json");
        File.WriteAllText(path, "{ not a list");

        Assert.Empty(new SentMessagesFile(path).Entries);
    }

    [Fact]
    public void Sits_beside_the_goal_it_belongs_to()
    {
        Assert.Equal(Path.Combine("goals", "abc.sent.json"),
            SentMessagesFile.BesideGoal(Path.Combine("goals", "abc.json")));
    }
}
