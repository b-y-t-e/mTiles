using mTiles.Models;
using mTiles.Services;
using Xunit;

namespace mTiles.Tests;

public class DroppedImageStoreTests
{
    private static readonly DateTime Now = new(2026, 9, 19, 14, 3, 7, DateTimeKind.Local);

    [Fact]
    public void A_kept_picture_is_named_after_when_it_arrived_and_is_unique()
    {
        var name = DroppedImageStore.NameFor(Now, Guid.NewGuid());
        Assert.StartsWith("dropped-20260919-140307-", name);
        Assert.EndsWith(".png", name);
        Assert.NotEqual(name, DroppedImageStore.NameFor(Now, Guid.NewGuid()));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(AppDefaults.LogRetentionDays - 1, false)]
    [InlineData(AppDefaults.LogRetentionDays + 1, true)]
    public void A_picture_is_kept_as_long_as_a_log_is(int daysOld, bool expired) =>
        Assert.Equal(expired, DroppedImageStore.HasExpired(Now.AddDays(-daysOld), Now));

    [Fact]
    public void Pruning_takes_the_old_pictures_and_nothing_else()
    {
        var directory = Directory.CreateTempSubdirectory("dropped-images-test").FullName;
        try
        {
            var old = Write(directory, DroppedImageStore.NameFor(Now, Guid.NewGuid()),
                Now.AddDays(-AppDefaults.LogRetentionDays - 1));
            var fresh = Write(directory, DroppedImageStore.NameFor(Now, Guid.NewGuid()), Now);
            var somebodyElses = Write(directory, "notes.txt", Now.AddYears(-1));

            DroppedImageStore.Prune(directory, Now);

            Assert.False(File.Exists(old));
            Assert.True(File.Exists(fresh));
            Assert.True(File.Exists(somebodyElses));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string Write(string directory, string name, DateTime writtenAt)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, [1, 2, 3]);
        File.SetLastWriteTime(path, writtenAt);
        return path;
    }
}
