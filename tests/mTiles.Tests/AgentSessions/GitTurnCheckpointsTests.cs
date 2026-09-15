using System.Diagnostics;
using mTiles.AgentSessions.Checkpoints;
using mTiles.AgentSessions.Events;
using Xunit;

namespace mTiles.Tests.AgentSessions;

/// <summary>Checkpoints against a real repository — see <see cref="GitTurnCheckpoints"/>.</summary>
public class GitTurnCheckpointsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"mtiles-checkpoints-{Guid.NewGuid():N}");

    public GitTurnCheckpointsTests()
    {
        Directory.CreateDirectory(_root);
        Git("init -q");
        Git("config user.name test");
        Git("config user.email test@example.com");
        Write("tracked.txt", "one\n");
        Write(".gitignore", "ignored/\n");
        Git("add -A");
        Git("commit -q -m init");
    }

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task A_turn_s_changes_are_read_tree_against_tree_untracked_files_included()
    {
        var checkpoints = new GitTurnCheckpoints(_root);
        var before = await checkpoints.CaptureAsync("conv", 0, CancellationToken.None);
        Assert.NotNull(before);

        Write("tracked.txt", "one\ntwo\n");
        Write("new.txt", "fresh\n");
        Write("ignored/build.log", "noise\n");
        var after = await checkpoints.CaptureAsync("conv", 1, CancellationToken.None);

        var changes = await checkpoints.ChangesAsync(before!, after!, CancellationToken.None);

        Assert.Equal(
            [new ChangedFile("new.txt", FileChangeKind.Added, 1, 0), new ChangedFile("tracked.txt", FileChangeKind.Modified, 1, 0)],
            changes);
        Assert.Contains("+two", await checkpoints.DiffAsync(before!, after!, null, CancellationToken.None));
        Assert.Empty(Git("diff --cached --name-only").Trim()); // nothing was staged in the user's own index
    }

    [Fact]
    public async Task Restoring_puts_back_what_was_there_and_removes_what_was_not()
    {
        Write("untracked-then.txt", "was here\n");
        var checkpoints = new GitTurnCheckpoints(_root);
        var before = await checkpoints.CaptureAsync("conv", 0, CancellationToken.None);

        Write("tracked.txt", "changed\n");
        File.Delete(Path.Combine(_root, "untracked-then.txt"));
        Write("added-later.txt", "remove me\n");
        Write("ignored/keep.log", "ignored stays\n");

        await checkpoints.RestoreAsync(before!, CancellationToken.None);

        Assert.Equal("one\n", Read("tracked.txt"));
        Assert.Equal("was here\n", Read("untracked-then.txt"));
        Assert.False(File.Exists(Path.Combine(_root, "added-later.txt")));
        Assert.True(File.Exists(Path.Combine(_root, "ignored", "keep.log")));
    }

    [Fact]
    public async Task Restoring_keeps_the_files_it_replaces_where_git_can_bring_them_back()
    {
        var checkpoints = new GitTurnCheckpoints(_root);
        var before = await checkpoints.CaptureAsync("conv", 0, CancellationToken.None);
        Write("tracked.txt", "edited next door\n");
        Write("created-next-door.txt", "mine\n");

        var replaced = await checkpoints.RestoreAsync(before!, CancellationToken.None);
        await checkpoints.ForgetAsync("conv", CancellationToken.None);

        Assert.False(File.Exists(Path.Combine(_root, "created-next-door.txt")));
        Assert.Equal("edited next door\n", Git($"show {replaced}:tracked.txt").Replace("\r\n", "\n"));
        Assert.Equal("mine\n", Git($"show {replaced}:created-next-door.txt").Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task A_workspace_inside_a_larger_repository_restores_only_itself()
    {
        Write("sub/inside.txt", "a\n");
        Git("add -A");
        Git("commit -q -m sub");

        var checkpoints = new GitTurnCheckpoints(Path.Combine(_root, "sub"));
        var before = await checkpoints.CaptureAsync("conv", 0, CancellationToken.None);

        Write("sub/inside.txt", "b\n");
        Write("outside.txt", "not mine\n");
        await checkpoints.RestoreAsync(before!, CancellationToken.None);

        Assert.Equal("a\n", Read("sub/inside.txt"));
        Assert.True(File.Exists(Path.Combine(_root, "outside.txt")));
    }

    [Fact]
    public async Task A_directory_without_a_repository_has_no_checkpoints_and_says_so_quietly()
    {
        var plain = Path.Combine(Path.GetTempPath(), $"mtiles-plain-{Guid.NewGuid():N}");
        Directory.CreateDirectory(plain);
        try
        {
            Assert.Null(await new GitTurnCheckpoints(plain).CaptureAsync("conv", 0, CancellationToken.None));
        }
        finally
        {
            Directory.Delete(plain, true);
        }
    }

    [Fact]
    public async Task A_checkpoint_taken_again_under_the_same_index_leaves_the_earlier_one_where_it_was()
    {
        var checkpoints = new GitTurnCheckpoints(_root);
        var earlier = await checkpoints.CaptureAsync("conv", 3, CancellationToken.None);
        var earlierCommit = Git($"rev-parse {earlier}").Trim();
        File.WriteAllText(Path.Combine(_root, "later.txt"), "later");

        var later = await checkpoints.CaptureAsync("conv", 3, CancellationToken.None);

        Assert.NotEqual(earlier, later);
        Assert.Equal(earlierCommit, Git($"rev-parse {earlier}").Trim());
    }

    [Fact]
    public async Task Forgetting_removes_the_conversation_s_refs()
    {
        var checkpoints = new GitTurnCheckpoints(_root);
        await checkpoints.CaptureAsync("conv", 0, CancellationToken.None);

        await checkpoints.ForgetAsync("conv", CancellationToken.None);

        Assert.Empty(Git($"for-each-ref {GitTurnCheckpoints.RefPrefix}").Trim());
    }

    [Fact]
    public void Numstat_and_name_status_are_read_together_renames_included()
    {
        var numstat = "3\t1\tsrc/a.cs\0-\t-\timg.png\0" + "0\t0\t\0old.cs\0new.cs\0";
        var nameStatus = "M\0src/a.cs\0A\0img.png\0R100\0old.cs\0new.cs\0";

        Assert.Equal(
        [
            new ChangedFile("img.png", FileChangeKind.Added, 0, 0),
            new ChangedFile("new.cs", FileChangeKind.Renamed, 0, 0),
            new ChangedFile("src/a.cs", FileChangeKind.Modified, 3, 1),
        ], CheckpointDiffParser.Parse(numstat, nameStatus));
    }

    private void Write(string relative, string text)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private string Read(string relative) => File.ReadAllText(Path.Combine(_root, relative)).Replace("\r\n", "\n");

    private string Git(string arguments)
    {
        var psi = new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        using var process = Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output;
    }
}
