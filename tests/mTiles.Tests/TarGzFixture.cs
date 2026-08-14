using System.IO.Compression;
using System.Text;

namespace mTiles.Tests;

/// <summary>
/// Builds `.tar.gz` archives in memory, in the shape the real model archives have.
/// </summary>
/// <remarks>
/// Shared because two suites need it for different reasons: one checks that the reader copes with what
/// macOS puts in these files, the other that the store unpacks what it downloads.
/// </remarks>
internal static class TarGzFixture
{
    private const int BlockSize = 512;

    /// <summary>An archive of plain files at the given paths.</summary>
    public static byte[] Build(params (string Path, string Content)[] entries) =>
        Build(tar =>
        {
            foreach (var (path, content) in entries)
                Append(tar, path, Encoding.UTF8.GetBytes(content));
        });

    public static byte[] Build(Action<MemoryStream> build)
    {
        using var tar = new MemoryStream();
        build(tar);
        tar.Write(new byte[BlockSize * 2]);        // two zero blocks end the archive

        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            gzip.Write(tar.ToArray());
        return compressed.ToArray();
    }

    public static void Append(MemoryStream tar, string name, byte[] content, char type = '0')
    {
        tar.Write(Header(name, content.Length, type));
        tar.Write(content);
        tar.Write(new byte[(BlockSize - content.Length % BlockSize) % BlockSize]);
    }

    /// <summary>The extended header macOS actually writes, records and all.</summary>
    public static byte[] ApplePaxBlock(string? path = null)
    {
        var records = new StringBuilder();

        // A PAX record is "len key=value\n" where len counts its own digits too, so the length has to
        // settle: adding a digit can push the total past the next power of ten.
        void Record(string body)
        {
            var length = body.Length + 2;                       // the space and the newline
            while (length.ToString().Length + body.Length + 2 != length)
                length = length.ToString().Length + body.Length + 2;

            records.Append($"{length} {body}\n");
        }

        Record("mtime=1757362879.084705033");
        Record("LIBARCHIVE.xattr.com.apple.quarantine=MDA4Mzs2OGJmM2FiZjtTYWZhcmk7RjE5MkQ2QzI=");
        Record("SCHILY.xattr.com.apple.quarantine=0083;68bf3abf;Safari;F192D6C2");
        if (path is not null)
            Record($"path={path}");

        return Encoding.UTF8.GetBytes(records.ToString());
    }

    private static byte[] Header(string name, long size, char type)
    {
        var block = new byte[BlockSize];
        void Write(string text, int offset, int length)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            Array.Copy(bytes, 0, block, offset, Math.Min(bytes.Length, length - 1));
        }

        Write(name, 0, 100);
        Write("000644 ", 100, 8);
        Write(Convert.ToString(size, 8).PadLeft(11, '0'), 124, 12);
        block[156] = (byte)type;
        Write("ustar", 257, 6);
        Write("00", 263, 2);

        // The checksum is computed with the field itself read as spaces.
        for (var i = 148; i < 156; i++) block[i] = (byte)' ';
        var sum = block.Aggregate(0, (acc, b) => acc + b);
        Write(Convert.ToString(sum, 8).PadLeft(6, '0') + "\0", 148, 8);
        return block;
    }
}
