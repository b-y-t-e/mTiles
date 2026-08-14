using System.Diagnostics;
using System.IO.Compression;
using System.Text;

namespace mTiles.Services.Speech;

/// <summary>
/// Unpacks a <c>.tar.gz</c> holding a model, tolerating what the archive actually contains.
/// </summary>
/// <remarks>
/// <para><see cref="System.Formats.Tar.TarFile"/> cannot read Handy's model archives. They are written
/// on macOS, so every file carries a PAX extended header with <c>LIBARCHIVE.xattr.com.apple.*</c>
/// records — quarantine flags and download provenance — and .NET's reader rejects the block outright
/// ("The extended header contains invalid records"), which ends the extraction on the first entry.
/// Measured, not guessed: it is what the released archive does.</para>
/// <para>So this reads the format directly and ignores everything a model does not need. Extended
/// headers are skipped rather than parsed, except for a <c>path=</c> record, since nothing else in them
/// affects the files on disk. macOS AppleDouble stubs (<c>._name</c>) are dropped: they are resource
/// forks, not model data, and writing them would leave junk beside every graph.</para>
/// <para><b>Only for an archive whose digest has already been checked.</b> This is a permissive reader
/// by design — it steps over what it does not understand rather than refusing the file — so it is not a
/// defence against an archive built to attack it. It contains what it can: entries are kept inside the
/// destination however they spell the way out, links are skipped rather than followed, header entries
/// are bounded, and an unreadable size stops the extraction instead of desynchronising the stream. That
/// is damage control, not authentication. What actually decides that these bytes are NVIDIA's model is
/// the SHA-256 in the catalogue, verified by <see cref="SpeechModelStore"/> <em>before</em> anything
/// here is called — and that ordering is the load-bearing part. Calling this on bytes that have not
/// been through it would be a different program with a different threat model.</para>
/// </remarks>
internal static class TarGzExtractor
{
    private const int BlockSize = 512;

    /// <summary>How the containment check compares paths. Shared with everything else that asks —
    /// see <see cref="FileHelper.PathComparison"/> for why the answer is per-platform.</summary>
    private static StringComparison PathComparison => FileHelper.PathComparison;

    /// <summary>
    /// The most a header block may claim before it is skipped rather than read into memory.
    /// </summary>
    /// <remarks>
    /// A PAX or GNU-long-name entry is read whole, and its length comes from an octal field an archive
    /// from the network controls — eleven digits, so up to 8 GB, allocated in one go. Real ones hold a
    /// path and a handful of xattr records. A megabyte is far more than any of them and far less than
    /// what it takes to end the process.
    /// </remarks>
    private const long MaxHeaderEntry = 1 << 20;

    public static void ExtractToDirectory(string archivePath, string destinationDirectory)
    {
        using var file = File.OpenRead(archivePath);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        ExtractToDirectory(gzip, destinationDirectory);
    }

    public static void ExtractToDirectory(Stream tar, string destinationDirectory)
    {
        var root = Path.GetFullPath(destinationDirectory);
        Directory.CreateDirectory(root);

        var header = new byte[BlockSize];
        string? pendingName = null;
        var emptyBlocks = 0;

        while (ReadFully(tar, header, BlockSize))
        {
            if (IsAllZero(header))
            {
                // Two zero blocks end the archive; one on its own is padding worth tolerating.
                if (++emptyBlocks >= 2)
                    return;
                continue;
            }
            emptyBlocks = 0;

            var size = ReadOctal(header, 124, 12);
            var type = (char)header[156];

            // The two header kinds are handled before the name is taken, and that ordering is the point:
            // they describe the *next* entry rather than being one, so a name waiting from an earlier
            // header has to survive them. Consuming it here — which is what reading it above the switch
            // did — meant a GNU long name followed by a PAX block lost the long name, and made the
            // `?? pendingName` below unreachable: it could only ever fall back on the null just written.
            switch (type)
            {
                case 'x' or 'g':                    // PAX extended header
                    if (size > MaxHeaderEntry)
                    {
                        Trace.TraceWarning("Skipping an extended header of {0} bytes.", size);
                        Skip(tar, size);
                        continue;
                    }
                    // A block with no path= record (every macOS xattr block) leaves the name as it was.
                    pendingName = ReadPathRecord(ReadEntry(tar, size)) ?? pendingName;
                    continue;

                case 'L':                           // GNU long name
                    if (size > MaxHeaderEntry)
                    {
                        Trace.TraceWarning("Skipping a long-name header of {0} bytes.", size);
                        Skip(tar, size);
                        continue;
                    }
                    pendingName = Encoding.UTF8.GetString(ReadEntry(tar, size)).TrimEnd('\0');
                    continue;
            }

            // A real entry: it takes the pending name, and clears it for whatever comes next.
            var name = pendingName ?? ReadName(header);
            pendingName = null;

            switch (type)
            {
                case '5':                           // directory
                    Skip(tar, size);
                    if (Resolve(root, name) is { } directory)
                        Directory.CreateDirectory(directory);
                    continue;

                case '0' or '\0':                   // file
                    break;

                default:                            // links, devices, anything else a model never needs
                    Skip(tar, size);
                    continue;
            }

            var target = Resolve(root, name);
            if (target is null || IsAppleDouble(name))
            {
                Skip(tar, size);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using (var output = File.Create(target))
                Copy(tar, output, size);

            SkipPadding(tar, size);
        }
    }

    /// <summary>The full path an entry may be written to, or null when it must not be written at all.
    /// An archive from the network must not be able to place a file outside the directory it is being
    /// unpacked into.</summary>
    private static string? Resolve(string root, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var relative = name.Replace('\\', '/').TrimStart('/');
        if (relative.Length == 0 || Path.IsPathRooted(relative))
            return null;

        var full = Path.GetFullPath(Path.Combine(root, relative));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, PathComparison) && !full.Equals(root, PathComparison))
        {
            Trace.TraceWarning("Refusing archive entry outside the destination: {0}", name);
            return null;
        }

        return full;
    }

    /// <summary>macOS resource-fork stubs, which every archive built on a Mac carries beside the real
    /// files.</summary>
    private static bool IsAppleDouble(string name) =>
        Path.GetFileName(name.Replace('\\', '/').TrimEnd('/')).StartsWith("._", StringComparison.Ordinal);

    private static string ReadName(ReadOnlySpan<byte> header)
    {
        var name = Trim(header[..100]);
        var prefix = Trim(header.Slice(345, 155));   // ustar splits long names in two
        return prefix.Length == 0 ? name : prefix + "/" + name;
    }

    private static string Trim(ReadOnlySpan<byte> field)
    {
        var end = field.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? field : field[..end]).Trim();
    }

    /// <summary>
    /// A tar numeric field: octal digits, padded, terminated by a space or a NUL.
    /// </summary>
    /// <remarks>
    /// GNU's <b>base-256</b> encoding — the top bit of the first byte set, the rest a big-endian integer
    /// — is refused rather than read. It appears for sizes above 8 GB, which no model archive is, and
    /// this reader has no way to test it. What matters is that it must not be <em>ignored</em>: the
    /// digits parse as nothing, the size comes out zero, and the entry's data blocks are then never
    /// skipped — so the next header is read out of the middle of a file and everything after it is
    /// invented. An extraction that stops with a reason is recoverable; one that quietly writes an empty
    /// file and then garbage is not.
    /// </remarks>
    private static long ReadOctal(ReadOnlySpan<byte> header, int offset, int length)
    {
        var field = header.Slice(offset, length);
        if (field.Length > 0 && (field[0] & 0x80) != 0)
            throw new InvalidDataException(
                "This archive uses GNU base-256 numeric fields, which this reader does not support.");

        var text = Trim(field);
        if (text.Length == 0)
            return 0;

        long value = 0;
        foreach (var c in text)
        {
            if (c is < '0' or > '7')
                break;
            value = value * 8 + (c - '0');
        }
        return value;
    }

    /// <summary>
    /// The one thing worth taking from an extended header: a name too long for the ustar fields.
    /// Records are <c>"len key=value\n"</c>; anything that does not parse is skipped rather than
    /// treated as corruption, which is the whole difference from the framework's reader.
    /// </summary>
    private static string? ReadPathRecord(byte[] block)
    {
        var offset = 0;
        while (offset < block.Length)
        {
            var space = Array.IndexOf(block, (byte)' ', offset);
            if (space < 0)
                return null;

            var header = Encoding.ASCII.GetString(block, offset, space - offset);
            if (!int.TryParse(header, out var length) || length <= 0 || offset + length > block.Length)
                return null;

            // The declared length must reach past its own digits and the space, or the substring below
            // is a negative count — an exception out of the extractor instead of the "skip what does not
            // parse" this reader exists to do.
            var count = offset + length - space - 2;
            if (count < 0)
                return null;

            var record = Encoding.UTF8.GetString(block, space + 1, count);
            if (record.StartsWith("path=", StringComparison.Ordinal))
                return record[5..];

            offset += length;
        }
        return null;
    }

    private static byte[] ReadEntry(Stream tar, long size)
    {
        var data = new byte[size];
        if (!ReadFully(tar, data, (int)size))
            throw new EndOfStreamException("The archive ended inside an entry.");

        SkipPadding(tar, size);
        return data;
    }

    private static void Copy(Stream tar, Stream output, long size)
    {
        var buffer = new byte[1 << 16];
        var remaining = size;
        while (remaining > 0)
        {
            var read = tar.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read <= 0)
                throw new EndOfStreamException("The archive ended inside a file.");

            output.Write(buffer, 0, read);
            remaining -= read;
        }
    }

    private static void Skip(Stream tar, long size)
    {
        Copy(tar, Stream.Null, size);
        SkipPadding(tar, size);
    }

    /// <summary>Entries are padded to a whole number of 512-byte blocks.</summary>
    private static void SkipPadding(Stream tar, long size)
    {
        var padding = (BlockSize - (int)(size % BlockSize)) % BlockSize;
        if (padding > 0)
            Copy(tar, Stream.Null, padding);
    }

    private static bool ReadFully(Stream stream, byte[] buffer, int count)
    {
        var offset = 0;
        while (offset < count)
        {
            var read = stream.Read(buffer, offset, count - offset);
            if (read <= 0)
                return offset == 0 ? false : throw new EndOfStreamException("The archive is truncated.");
            offset += read;
        }
        return true;
    }

    private static bool IsAllZero(ReadOnlySpan<byte> block)
    {
        foreach (var b in block)
            if (b != 0)
                return false;
        return true;
    }
}
