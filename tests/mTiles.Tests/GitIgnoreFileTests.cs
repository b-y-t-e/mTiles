using mTiles.Services;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// Editing a file that belongs to the user's project. Every test here is really the same question asked
/// about a different starting state: did anything change that we were not asked to change? A wrong
/// answer does not throw — it shows up in someone's commit.
/// </summary>
public sealed class GitIgnoreFileTests : IDisposable
{
    private const string Entry = ".mtiles/";
    private const string Marker = "# mTiles workspace state";

    private readonly string _repo =
        Path.Combine(Path.GetTempPath(), "mtiles-tests", Guid.NewGuid().ToString("N"));

    public GitIgnoreFileTests() => Directory.CreateDirectory(_repo);

    public void Dispose()
    {
        try { Directory.Delete(_repo, recursive: true); } catch { /* a temp directory */ }
    }

    private string GitIgnorePath => Path.Combine(_repo, ".gitignore");
    private string Content => File.ReadAllText(GitIgnorePath);
    private void Given(string content) => File.WriteAllText(GitIgnorePath, content);

    // ---- adding ----------------------------------------------------------------

    [Fact]
    public async Task An_entry_is_added_to_a_repository_with_no_gitignore_at_all()
    {
        Assert.True(await GitIgnoreFile.EnsureAsync(_repo, Entry));

        Assert.Equal($"{Marker}\n{Entry}\n", Content);
    }

    /// <summary>The existing content is what matters here, not the new line: a user's file must come
    /// back byte for byte with one block added, not reformatted, reordered or re-sorted.</summary>
    [Fact]
    public async Task Adding_an_entry_leaves_everything_already_in_the_file_alone()
    {
        Given("bin/\nobj/\n\n# my own notes\n*.user\n");

        await GitIgnoreFile.EnsureAsync(_repo, Entry);

        Assert.StartsWith("bin/\nobj/\n\n# my own notes\n*.user\n", Content);
        Assert.EndsWith($"{Marker}\n{Entry}\n", Content);
    }

    /// <summary>"Never rewrite" has to cover trailing whitespace too. A file the user left ending in
    /// blank lines used to come back with them trimmed — a change nobody asked for, made by the very
    /// operation that claims only to add a line.</summary>
    [Fact]
    public async Task Blank_lines_the_user_left_at_the_end_survive()
    {
        Given("bin/\n\n\n");

        await GitIgnoreFile.EnsureAsync(_repo, Entry);

        Assert.StartsWith("bin/\n\n\n", Content);
    }

    [Fact]
    public async Task A_file_with_no_trailing_newline_does_not_get_the_entry_glued_to_its_last_line()
    {
        Given("bin/\nobj/");        // no newline at end, as editors often leave it

        await GitIgnoreFile.EnsureAsync(_repo, Entry);

        Assert.Contains("obj/\n", Content);
        Assert.DoesNotContain("obj/#", Content);
    }

    /// <summary>Called on every refresh, so "already there" has to be the common case and has to be
    /// free. A second copy would grow the file by a line every time the tile looked at it.</summary>
    [Theory]
    [InlineData(".mtiles/")]
    [InlineData(".mtiles")]      // git ignores the same directory either way
    [InlineData("  .mtiles/  ")] // and a user's spacing is still the same entry
    public async Task An_entry_that_is_already_listed_is_not_added_again(string existing)
    {
        Given($"bin/\n{existing}\nobj/\n");

        Assert.False(await GitIgnoreFile.EnsureAsync(_repo, Entry));
        Assert.Equal($"bin/\n{existing}\nobj/\n", Content);
    }

    /// <summary>A commented-out entry is not an entry — the user turned it off on purpose, and reading
    /// it as "already ignored" would leave the setting silently doing nothing.</summary>
    [Fact]
    public async Task A_commented_out_entry_does_not_count_as_listed()
    {
        Given("#.mtiles/\n");

        Assert.True(await GitIgnoreFile.EnsureAsync(_repo, Entry));
    }

    // ---- line endings ----------------------------------------------------------

    /// <summary>A repository using CRLF must not come back as one modified line per line — and the lines
    /// we add have to match, not just the ones already there. Asserting only on the start of the file
    /// let LF endings be appended to a CRLF file, a whitespace change in a diff for no reason.</summary>
    [Fact]
    public async Task A_file_with_windows_line_endings_gets_windows_line_endings_added()
    {
        Given("bin/\r\nobj/\r\n");

        await GitIgnoreFile.EnsureAsync(_repo, Entry);

        Assert.Equal($"bin/\r\nobj/\r\n\r\n{Marker}\r\n{Entry}\r\n", Content);
    }

    [Fact]
    public async Task A_file_with_unix_line_endings_gets_unix_line_endings_added()
    {
        Given("bin/\nobj/\n");

        await GitIgnoreFile.EnsureAsync(_repo, Entry);

        Assert.Equal($"bin/\nobj/\n\n{Marker}\n{Entry}\n", Content);
    }

    /// <summary>The terminator added to a file that ended without one is the file's, not always LF.
    /// Getting it wrong leaves a lone LF line in the middle of a CRLF file — the one place a mixed
    /// ending is invisible in the source and obvious in a diff.</summary>
    [Fact]
    public async Task A_windows_file_with_no_trailing_newline_is_terminated_with_crlf()
    {
        Given("bin/\r\nobj/");

        await GitIgnoreFile.EnsureAsync(_repo, Entry);

        Assert.Equal($"bin/\r\nobj/\r\n\r\n{Marker}\r\n{Entry}\r\n", Content);
    }

    /// <summary>A leading slash means "at this level only" and a trailing one "directory only" — not the
    /// same pattern in general, but for a directory sitting right here they all ignore it. Comparing the
    /// text literally read a user's own spelling as absent and added a second line beneath it.</summary>
    [Theory]
    [InlineData("/.mtiles/")]
    [InlineData("/.mtiles")]
    public async Task A_user_spelling_of_the_same_directory_is_not_duplicated(string existing)
    {
        Given($"bin/\n{existing}\n");

        Assert.False(await GitIgnoreFile.EnsureAsync(_repo, Entry));
        Assert.Equal($"bin/\n{existing}\n", Content);
    }

    // ---- removing --------------------------------------------------------------

    /// <summary>
    /// Turning the setting off puts the file back exactly as it was — byte for byte, whatever was in it
    /// and however many times the user changes their mind. Our block goes and nothing else does: not the
    /// lines around it, not blank lines that were already there, and no residue accumulating per flip.
    /// </summary>
    /// <remarks>
    /// One theory rather than a test per starting file, because it is one claim — <c>Ensure</c> followed
    /// by <c>Remove</c> is the identity — and the starting content is the only thing that varied. The
    /// blank-line case is the one that has actually failed: this class puts a blank line in front of its
    /// own marker, and taking one blank line too many out is invisible until a user's own spacing is
    /// gone. Toggling repeatedly is the same claim iterated, which is how a per-flip residue shows up.
    /// </remarks>
    [Theory]
    [InlineData("bin/\n", 1)]
    [InlineData("bin/\nobj/\n*.user\n", 1)]     // the lines around our block are the user's
    [InlineData("bin/\n\n\nobj/\n", 1)]          // including their own blank lines
    [InlineData("bin/\nobj/\n", 3)]              // making up their mind, three times over
    public async Task Adding_our_block_and_taking_it_back_out_leaves_the_file_as_it_was(
        string original, int rounds)
    {
        Given(original);

        for (int i = 0; i < rounds; i++)
        {
            await GitIgnoreFile.EnsureAsync(_repo, Entry);
            Assert.True(await GitIgnoreFile.RemoveAsync(_repo, Entry));
        }

        Assert.Equal(original, Content);
    }

    /// <summary>
    /// The same text, but not our line. A user who listed <c>.mtiles/</c> themselves — quite possibly
    /// long before this setting existed — keeps it. Turning a setting off is not permission to delete
    /// something somebody else put there, and matching on the text alone could not tell the two apart.
    /// </summary>
    [Fact]
    public async Task An_entry_the_user_wrote_themselves_is_not_removed()
    {
        Given("bin/\n.mtiles/\nobj/\n");

        Assert.False(await GitIgnoreFile.RemoveAsync(_repo, Entry));
        Assert.Equal("bin/\n.mtiles/\nobj/\n", Content);
    }

    /// <summary>
    /// The file is never deleted, even when our block was all of it. This class cannot tell a
    /// <c>.gitignore</c> it created from an empty one the user committed, and deleting a tracked file
    /// turns into a deletion in their history — a worse outcome than leaving an empty file behind.
    /// </summary>
    [Fact]
    public async Task A_gitignore_left_empty_is_kept_rather_than_deleted()
    {
        await GitIgnoreFile.EnsureAsync(_repo, Entry);

        Assert.True(await GitIgnoreFile.RemoveAsync(_repo, Entry));
        Assert.True(File.Exists(GitIgnorePath));
        Assert.Equal("", Content);
    }

    /// <summary>
    /// A UTF-8 BOM, and by extension any encoding that is not UTF-8, survives. Reading the file as text
    /// and writing it back re-encodes all of it: the BOM disappears, and a file saved in another
    /// encoding comes back as mojibake that cannot be undone.
    /// </summary>
    [Fact]
    public async Task A_byte_order_mark_and_the_bytes_around_it_survive()
    {
        byte[] original = [0xEF, 0xBB, 0xBF, .. "bin/\n"u8.ToArray()];
        await File.WriteAllBytesAsync(GitIgnorePath, original);

        await GitIgnoreFile.EnsureAsync(_repo, Entry);

        var after = await File.ReadAllBytesAsync(GitIgnorePath);
        Assert.Equal(original, after[..original.Length]);   // untouched, BOM included
        Assert.EndsWith($"{Marker}\n{Entry}\n", System.Text.Encoding.UTF8.GetString(after));
    }

    /// <summary>Bytes that are not valid UTF-8 — a Windows-1250 comment, say — are left exactly as they
    /// were. Decoding and re-encoding them is the corruption this class exists to avoid.</summary>
    [Fact]
    public async Task Bytes_that_are_not_utf8_are_left_alone()
    {
        byte[] original = [.. "# zażółć"u8.ToArray()[..3], 0xBF, 0xF3, (byte)'\n'];
        await File.WriteAllBytesAsync(GitIgnorePath, original);

        await GitIgnoreFile.EnsureAsync(_repo, Entry);

        var after = await File.ReadAllBytesAsync(GitIgnorePath);
        Assert.Equal(original, after[..original.Length]);
    }

    [Fact]
    public async Task Removing_an_entry_that_is_not_there_changes_nothing()
    {
        Given("bin/\nobj/\n");

        Assert.False(await GitIgnoreFile.RemoveAsync(_repo, Entry));
        Assert.Equal("bin/\nobj/\n", Content);
    }

    [Fact]
    public async Task Removing_from_a_repository_with_no_gitignore_is_not_an_error()
        => Assert.False(await GitIgnoreFile.RemoveAsync(_repo, Entry));

    // ---- concurrency and failure -----------------------------------------------

    /// <summary>
    /// Two Git tiles refresh independently and can be looking at the same repository — two workspaces
    /// under one checkout, or the same one open twice. Without serialising the read-modify-write, both
    /// read a file with no entry and both append one.
    /// </summary>
    [Fact]
    public async Task Two_tiles_asking_at_once_add_the_entry_only_once()
    {
        Given("bin/\n");

        await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => GitIgnoreFile.EnsureAsync(_repo, Entry))));

        Assert.Equal(1, Content.Split('\n').Count(l => l.Trim() == Entry));
        Assert.Equal(1, Content.Split('\n').Count(l => l.Trim() == Marker));
    }

    /// <summary>
    /// A <c>.gitignore</c> another process is holding comes back untouched, and the failure is reported
    /// rather than swallowed.
    /// <para>Honest about what this does <em>not</em> prove: it does not distinguish a read that is
    /// allowed to fail from one that is caught and turned into "no file". A lock strong enough to stop
    /// the read stops the write too, so both versions leave the file alone here. The reason the catch
    /// is gone anyway is that it made the two indistinguishable <em>in the code</em> — "could not read
    /// it" answered the caller with an empty list, and the caller's answer to an empty list is to write
    /// its own lines. Nothing drove that, and the cost of being wrong is somebody's file.</para>
    /// </summary>
    [Fact]
    public async Task A_gitignore_another_process_is_holding_is_left_alone()
    {
        const string original = "bin/\nobj/\n*.user\n";
        Given(original);

        using (File.Open(GitIgnorePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Assert.ThrowsAnyAsync<IOException>(() => GitIgnoreFile.EnsureAsync(_repo, Entry));
        }

        Assert.Equal(original, Content);
    }
}
