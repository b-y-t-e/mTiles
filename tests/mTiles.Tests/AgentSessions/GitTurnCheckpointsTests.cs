using System.Diagnostics;
using mTiles.AgentSessions.Checkpoints;
using mTiles.AgentSessions.Events;
using Xunit;

namespace mTiles.Tests.AgentSessions;

/// <summary>Checkpoints against a real repository — see <see cref="GitTurnCheckpoints"/>.</summary>
public class GitTurnCheckpointsTests : IDisposable
{
    /// <summary>A repository holding one commit of a tracked file and a <c>.gitignore</c>, made once per
    /// run; each test works in a copy of it, which costs no git process at all.</summary>
    private static readonly Lazy<string> Committed = new(() =>
    {
        var template = new GitTestRepo(prefix: "checkpoints-template");
        template.Write("tracked.txt", "one\n");
        template.Write(".gitignore", "ignored/\n");
        template.CommitAll("init");
        return template.Path;
    });

    private readonly GitTestRepo _repo = new(init: false, prefix: "checkpoints");
    private readonly string _root;

    public GitTurnCheckpointsTests()
    {
        _root = _repo.Path;
        foreach (var dir in Directory.EnumerateDirectories(Committed.Value, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(_root, Path.GetRelativePath(Committed.Value, dir)));
        foreach (var file in Directory.EnumerateFiles(Committed.Value, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(_root, Path.GetRelativePath(Committed.Value, file)));
    }

    public void Dispose() => _repo.Dispose();

    [Fact]
    [Trait("Category", "Slow")] // many real git processes; close to the budget on a Windows runner
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

    /// <summary>A restore puts back what was there, removes what was not and leaves ignored files alone —
    /// and what it replaces is kept where git can bring it back, even once the conversation is forgotten.
    /// </summary>
    [Fact]
    [Trait("Category", "Slow")] // many real git processes; close to the budget on a Windows runner
    public async Task Restoring_puts_back_what_was_there_and_keeps_what_it_replaces()
    {
        Write("untracked-then.txt", "was here\n");
        var checkpoints = new GitTurnCheckpoints(_root);
        var before = await checkpoints.CaptureAsync("conv", 0, CancellationToken.None);

        Write("tracked.txt", "edited next door\n");
        File.Delete(Path.Combine(_root, "untracked-then.txt"));
        Write("created-next-door.txt", "mine\n");
        Write("ignored/keep.log", "ignored stays\n");

        var replaced = await checkpoints.RestoreAsync(before!, CancellationToken.None);
        await checkpoints.ForgetAsync("conv", CancellationToken.None);

        Assert.Equal("one\n", Read("tracked.txt"));
        Assert.Equal("was here\n", Read("untracked-then.txt"));
        Assert.False(File.Exists(Path.Combine(_root, "created-next-door.txt")));
        Assert.True(File.Exists(Path.Combine(_root, "ignored", "keep.log")));

        Assert.Equal("edited next door\n", Git($"show {replaced}:tracked.txt").Replace("\r\n", "\n"));
        Assert.Equal("mine\n", Git($"show {replaced}:created-next-door.txt").Replace("\r\n", "\n"));

        // Forgetting takes the conversation's refs, and only those.
        Assert.Empty(Git($"for-each-ref {GitTurnCheckpoints.RefPrefix}").Trim());
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
        using var plain = new TempDirectory();

        Assert.Null(await new GitTurnCheckpoints(plain.Path).CaptureAsync("conv", 0, CancellationToken.None));
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

    /// <summary>A pathspec is applied before git looks for renames, so the new name on its own answers
    /// with the whole file as added lines. Asked under both names it is the rename it really was.</summary>
    [Fact]
    public async Task A_renamed_file_is_asked_for_under_both_of_its_names()
    {
        Write("before.txt", "one\ntwo\nthree\n");
        Git("add -A");
        Git("commit -m first");
        var checkpoints = new GitTurnCheckpoints(_root);
        var before = await checkpoints.CaptureAsync("conv", 0, CancellationToken.None);
        File.Move(Path.Combine(_root, "before.txt"), Path.Combine(_root, "after.txt"));
        var after = await checkpoints.CaptureAsync("conv", 1, CancellationToken.None);

        var file = Assert.Single(
            await checkpoints.ChangesAsync(before!, after!, CancellationToken.None),
            f => f.Kind == FileChangeKind.Renamed);
        var diff = await checkpoints.DiffAsync(before!, after!, [file.OldPath!, file.Path], CancellationToken.None);

        Assert.Contains("rename", diff, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("+one", diff);
    }

    /// <summary>Git reads a bare pathspec as a glob, so a file whose own name holds glob characters
    /// would not match itself and its row would open on an empty patch.</summary>
    [Fact]
    public async Task A_file_whose_name_looks_like_a_glob_is_matched_literally()
    {
        Write("Data[1].json", "one\n");
        Git("add -A");
        Git("commit -m first");
        var checkpoints = new GitTurnCheckpoints(_root);
        var before = await checkpoints.CaptureAsync("conv", 0, CancellationToken.None);
        Write("Data[1].json", "one\ntwo\n");
        var after = await checkpoints.CaptureAsync("conv", 1, CancellationToken.None);

        var diff = await checkpoints.DiffAsync(before!, after!, ["Data[1].json"], CancellationToken.None);

        Assert.Contains("+two", diff);
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
