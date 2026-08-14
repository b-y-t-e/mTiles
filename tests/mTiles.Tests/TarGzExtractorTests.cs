using System.IO.Compression;
using System.Text;
using mTiles.Services.Speech;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The archive reader, against the shape of archive it actually has to read.
/// </summary>
/// <remarks>
/// These are not hypothetical cases. Handy's model archives are built on macOS, so every file is
/// preceded by a PAX extended header carrying <c>LIBARCHIVE.xattr.com.apple.*</c> records and shadowed
/// by an AppleDouble <c>._</c> stub — and <see cref="System.Formats.Tar.TarFile"/> throws
/// "The extended header contains invalid records" on the first one, which is why this reader exists.
/// The fixtures below reproduce that header byte for byte.
/// </remarks>
public class TarGzExtractorTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "mtiles-tests", Guid.NewGuid().ToString("N"));

    public TarGzExtractorTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    /// <summary>Writes an archive to a file, because the extractor opens paths rather than streams.</summary>
    private string WriteArchive(Action<MemoryStream> build)
    {
        var path = Path.Combine(_directory, $"fixture-{Guid.NewGuid():N}.tar.gz");
        File.WriteAllBytes(path, TarGzFixture.Build(build));
        return path;
    }

    // ---- tests ----

    /// <summary>
    /// A GNU base-256 size stops the extraction instead of being read as zero.
    /// </summary>
    /// <remarks>
    /// The top bit of the field marks it binary; the octal parse then finds no digits and returns zero,
    /// so the entry's data blocks are never stepped over and the next header is read out of the middle
    /// of a file. Everything after that point is invented — an empty file where the model should be,
    /// then whatever the bytes happen to look like. Refusing is what makes it recoverable: the archive
    /// is kept and the failure names its reason.
    /// </remarks>
    [Fact]
    public void A_binary_size_field_is_refused_rather_than_read_as_nothing()
    {
        var archive = WriteArchive(tar =>
        {
            var block = new byte[512];
            System.Text.Encoding.UTF8.GetBytes("model.onnx").CopyTo(block, 0);
            block[124] = 0x80;                      // base-256 marker, then a big-endian size
            block[134] = 0x10;
            block[156] = (byte)'0';
            for (var i = 148; i < 156; i++) block[i] = (byte)' ';
            var sum = block.Aggregate(0, (acc, b) => acc + b);
            System.Text.Encoding.UTF8.GetBytes(Convert.ToString(sum, 8).PadLeft(6, '0') + "\0")
                .CopyTo(block, 148);
            tar.Write(block);
        });

        var destination = Path.Combine(_directory, "out");
        Assert.Throws<InvalidDataException>(() => TarGzExtractor.ExtractToDirectory(archive, destination));
    }

    /// <summary>
    /// An extended header claiming a size no real one has is skipped, not read into memory.
    /// </summary>
    /// <remarks>
    /// The size is an eleven-digit octal field the archive controls, so it can ask for eight gigabytes,
    /// and the entry is allocated whole. Real PAX blocks hold a path and a few xattr records. The
    /// archive here also stays readable afterwards — skipping means stepping over the bytes, not
    /// abandoning the file.
    /// </remarks>
    [Fact]
    public void An_absurdly_large_extended_header_is_skipped_and_the_rest_still_extracts()
    {
        var oversized = new byte[(1 << 20) + 1_024];
        var archive = WriteArchive(tar =>
        {
            TarGzFixture.Append(tar, "model/PaxHeader/graph.onnx", oversized, 'x');
            TarGzFixture.Append(tar, "model/graph.onnx", Encoding.UTF8.GetBytes("kept"));
        });

        TarGzExtractor.ExtractToDirectory(archive, _directory);

        Assert.Equal("kept", File.ReadAllText(Path.Combine(_directory, "model", "graph.onnx")));
    }

    /// <summary>
    /// A record whose declared length does not even cover its own digits is skipped rather than thrown
    /// on — "ignore what does not parse" is the whole difference between this reader and the framework's.
    /// </summary>
    [Fact]
    public void A_malformed_record_length_does_not_throw()
    {
        var archive = WriteArchive(tar =>
        {
            TarGzFixture.Append(tar, "model/PaxHeader/graph.onnx", Encoding.UTF8.GetBytes("1 path=x\n"), 'x');
            TarGzFixture.Append(tar, "model/graph.onnx", Encoding.UTF8.GetBytes("kept"));
        });

        TarGzExtractor.ExtractToDirectory(archive, _directory);

        Assert.Equal("kept", File.ReadAllText(Path.Combine(_directory, "model", "graph.onnx")));
    }

    [Fact]
    public void A_macos_archive_extracts_where_the_framework_refuses()
    {
        var content = Encoding.UTF8.GetBytes("model bytes");
        var archive = WriteArchive(tar =>
        {
            TarGzFixture.Append(tar, "model/", [], '5');
            TarGzFixture.Append(tar, "model/PaxHeader/graph.onnx", TarGzFixture.ApplePaxBlock(), 'x');
            TarGzFixture.Append(tar, "model/graph.onnx", content);
        });

        // The reason this class exists: prove the framework still cannot read it.
        Assert.Throws<InvalidDataException>(() =>
        {
            using var file = File.OpenRead(archive);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            System.Formats.Tar.TarFile.ExtractToDirectory(gzip,
                Directory.CreateDirectory(Path.Combine(_directory, "framework")).FullName, true);
        });

        var into = Path.Combine(_directory, "ours");
        TarGzExtractor.ExtractToDirectory(archive, into);

        Assert.Equal(content, File.ReadAllBytes(Path.Combine(into, "model", "graph.onnx")));
    }

    [Fact]
    public void AppleDouble_stubs_are_left_out()
    {
        var archive = WriteArchive(tar =>
        {
            TarGzFixture.Append(tar, "model/._graph.onnx", Encoding.UTF8.GetBytes("resource fork"));
            TarGzFixture.Append(tar, "model/graph.onnx", Encoding.UTF8.GetBytes("real"));
        });

        var into = Path.Combine(_directory, "out");
        TarGzExtractor.ExtractToDirectory(archive, into);

        Assert.True(File.Exists(Path.Combine(into, "model", "graph.onnx")));
        Assert.False(File.Exists(Path.Combine(into, "model", "._graph.onnx")));
    }

    /// <summary>
    /// An archive fetched over the network must not be able to write outside the directory it is being
    /// unpacked into, however it spells the way out.
    /// </summary>
    [Theory]
    [InlineData("../escaped.txt")]
    [InlineData("model/../../escaped.txt")]
    [InlineData("model/../../../../../../escaped.txt")]
    [InlineData("..\\escaped.txt")]                  // the same way out, spelled for Windows
    public void Entries_that_would_escape_the_destination_are_refused(string name)
    {
        var archive = WriteArchive(tar =>
        {
            TarGzFixture.Append(tar, name, Encoding.UTF8.GetBytes("hostile"));
            TarGzFixture.Append(tar, "model/graph.onnx", Encoding.UTF8.GetBytes("real"));
        });

        var into = Path.Combine(_directory, "out");
        TarGzExtractor.ExtractToDirectory(archive, into);

        Assert.True(File.Exists(Path.Combine(into, "model", "graph.onnx")));
        Assert.False(File.Exists(Path.Combine(_directory, "escaped.txt")));
        Assert.Empty(Directory.EnumerateFiles(into, "escaped.txt", SearchOption.AllDirectories));
    }

    /// <summary>
    /// A leading slash is stripped, not treated as an escape — which is what tar itself does.
    /// </summary>
    /// <remarks>
    /// It was sitting in the escape table above, where it proved nothing: <c>/absolute.txt</c> becomes
    /// <c>absolute.txt</c> <em>inside</em> the destination, so the assertions about a file called
    /// escaped.txt held for an entry that never tried to escape. Two different rules, and each needs its
    /// own assertion.
    /// </remarks>
    [Fact]
    public void A_leading_slash_is_stripped_rather_than_refused()
    {
        var archive = WriteArchive(tar =>
            TarGzFixture.Append(tar, "/absolute.txt", Encoding.UTF8.GetBytes("kept")));

        var into = Path.Combine(_directory, "out");
        TarGzExtractor.ExtractToDirectory(archive, into);

        Assert.Equal("kept", File.ReadAllText(Path.Combine(into, "absolute.txt")));
        Assert.False(File.Exists(Path.Combine(_directory, "absolute.txt")));
    }

    /// <summary>
    /// A name arriving through an extended header is checked like any other.
    /// </summary>
    /// <remarks>
    /// This is the easiest way past the guard if the guard is in the wrong place: the PAX record
    /// replaces the name <em>after</em> the header fields have been read, so a check that lived on
    /// <c>ReadName</c> would never see it. The entry has to be dropped whole — name refused and its data
    /// skipped — or the reader loses its place in the stream and everything after it is invented.
    /// </remarks>
    [Fact]
    public void A_path_record_that_would_escape_is_refused_like_any_other_name()
    {
        var archive = WriteArchive(tar =>
        {
            TarGzFixture.Append(tar, "PaxHeader/x", TarGzFixture.ApplePaxBlock("../escaped.txt"), 'x');
            TarGzFixture.Append(tar, "placeholder", Encoding.UTF8.GetBytes("hostile"));
            TarGzFixture.Append(tar, "model/graph.onnx", Encoding.UTF8.GetBytes("real"));
        });

        var into = Path.Combine(_directory, "out");
        TarGzExtractor.ExtractToDirectory(archive, into);

        Assert.False(File.Exists(Path.Combine(_directory, "escaped.txt")));
        Assert.False(File.Exists(Path.Combine(into, "placeholder")));

        // And the archive is still readable afterwards: refusing an entry means stepping over its data,
        // not abandoning the file.
        Assert.Equal("real", File.ReadAllText(Path.Combine(into, "model", "graph.onnx")));
    }

    /// <summary>A GNU long name is taken the same way a PAX <c>path=</c> record is.</summary>
    [Fact]
    public void A_gnu_long_name_header_renames_the_entry_that_follows()
    {
        var longName = "model/" + new string('n', 150) + ".onnx";
        var archive = WriteArchive(tar =>
        {
            TarGzFixture.Append(tar, "././@LongLink", Encoding.UTF8.GetBytes(longName + "\0"), 'L');
            TarGzFixture.Append(tar, "model/truncated", Encoding.UTF8.GetBytes("bytes"));
        });

        var into = Path.Combine(_directory, "out");
        TarGzExtractor.ExtractToDirectory(archive, into);

        Assert.True(File.Exists(Path.Combine(into, longName)));
        Assert.False(File.Exists(Path.Combine(into, "model", "truncated")));
    }

    /// <summary>
    /// Links are stepped over, not followed.
    /// </summary>
    /// <remarks>
    /// A symlink entry pointing at somewhere outside the destination, followed by a file that writes
    /// "through" it, is the classic way an archive writes where it should not — and the whole of the
    /// defence is a <c>default:</c> arm in a switch, which anybody rearranging that switch could remove
    /// without noticing. No model archive contains one; this is what says so out loud.
    /// </remarks>
    [Theory]
    [InlineData('1')]                                // hard link
    [InlineData('2')]                                // symbolic link
    public void Link_entries_are_skipped(char type)
    {
        var archive = WriteArchive(tar =>
        {
            TarGzFixture.Append(tar, "model/link", [], type);
            TarGzFixture.Append(tar, "model/graph.onnx", Encoding.UTF8.GetBytes("real"));
        });

        var into = Path.Combine(_directory, "out");
        TarGzExtractor.ExtractToDirectory(archive, into);

        Assert.False(File.Exists(Path.Combine(into, "model", "link")));
        Assert.Equal("real", File.ReadAllText(Path.Combine(into, "model", "graph.onnx")));
    }

    [Fact]
    public void A_path_record_in_an_extended_header_renames_the_entry_that_follows()
    {
        var archive = WriteArchive(tar =>
        {
            TarGzFixture.Append(tar, "model/PaxHeader/short", TarGzFixture.ApplePaxBlock("model/a-much-longer-name.onnx"), 'x');
            TarGzFixture.Append(tar, "model/short", Encoding.UTF8.GetBytes("bytes"));
        });

        var into = Path.Combine(_directory, "out");
        TarGzExtractor.ExtractToDirectory(archive, into);

        Assert.True(File.Exists(Path.Combine(into, "model", "a-much-longer-name.onnx")));
        Assert.False(File.Exists(Path.Combine(into, "model", "short")));
    }

    [Fact]
    public void Files_that_are_not_a_multiple_of_the_block_size_keep_their_exact_length()
    {
        var content = new byte[513];
        Random.Shared.NextBytes(content);
        var archive = WriteArchive(tar =>
        {
            TarGzFixture.Append(tar, "model/odd.bin", content);
            TarGzFixture.Append(tar, "model/after.txt", Encoding.UTF8.GetBytes("still readable"));
        });

        var into = Path.Combine(_directory, "out");
        TarGzExtractor.ExtractToDirectory(archive, into);

        Assert.Equal(content, File.ReadAllBytes(Path.Combine(into, "model", "odd.bin")));
        Assert.Equal("still readable", File.ReadAllText(Path.Combine(into, "model", "after.txt")));
    }
}
