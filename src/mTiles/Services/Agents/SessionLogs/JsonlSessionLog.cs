using System.Text;
using System.Text.Json;
using mTiles.Models;

namespace mTiles.Services.Agents.SessionLogs;

/// <summary>
/// A store that is one directory per working directory, holding one append-only <c>.jsonl</c> file per
/// conversation.
/// </summary>
/// <remarks>
/// <para><b>Claude Code and pi both file their sessions this way</b> (measured 2026-09-18 against
/// <c>~/.claude/projects/&lt;slug&gt;/&lt;id&gt;.jsonl</c> and
/// <c>~/.pi/agent/sessions/--&lt;slug&gt;--/&lt;stamp&gt;_&lt;id&gt;.jsonl</c>), so the walking, the
/// ordering and the reading of the last useful line are written once here. What each of them spells
/// differently — where the root is, how the path becomes a directory name, where the id is in the file
/// name, and what a usage object is called inside — is four small overrides.</para>
/// <para><b>The file is read backwards, and that is not an optimisation.</b> These files run to
/// megabytes for a long conversation and the only line that matters is the last one carrying a usage
/// object; parsing forwards means parsing the whole transcript to answer a question about its final
/// line, on a read that happens every time the file changes. A store that also records a price per turn
/// needs every line, and is read forwards instead — once, and then only what was appended.</para>
/// </remarks>
public abstract class JsonlSessionLog : AgentSessionLog
{
    /// <summary>The directory this CLI keeps its per-project session directories under.</summary>
    protected abstract string SessionsRoot(AiSignIn? signIn);

    /// <summary>What this CLI calls the directory holding one working directory's sessions.</summary>
    protected abstract string DirectoryNameFor(string workspaceDir);

    /// <summary>The session id inside a file name, or null when the name is not a session's.</summary>
    protected abstract string? SessionIdIn(string fileName);

    /// <summary>What one line of the transcript says about tokens and cost, or null when it says
    /// nothing.</summary>
    /// <remarks>Answering null is the ordinary case — most lines of these files are messages, tool
    /// calls and bookkeeping — and is what makes "the last line that carries a usage object" the rule
    /// rather than "the last line".</remarks>
    protected abstract LineReading? ReadLine(JsonElement line);

    /// <summary>Whether this store records a price per turn, which is then added up across the whole
    /// file.</summary>
    /// <remarks>False by default, and then the read stops at the last line that carries tokens: the
    /// transcript runs to megabytes and is read every time it changes, and without a price to add up
    /// nothing before that line has anything to say. True reads the file forwards once and then only
    /// what was appended (<see cref="JsonlFoldTail{TState}"/>), because the sum needs every line.</remarks>
    protected virtual bool RecordsCost => false;

    /// <summary>What one transcript line contributes.</summary>
    /// <param name="UsedTokens">The context occupied after that line, where the line says so.</param>
    /// <param name="CostUsd">What that line itself cost, where the CLI records it. Added up across the
    /// file, because these stores record a turn's price and not a running total.</param>
    /// <param name="Model">What that turn ran on, where the line says.</param>
    protected readonly record struct LineReading(long? UsedTokens, decimal? CostUsd, string? Model = null);

    /// <inheritdoc />
    public override string? WatchDirectory(AiSignIn? signIn, string workspaceDir) =>
        Path.Combine(SessionsRoot(signIn), DirectoryNameFor(workspaceDir));

    /// <inheritdoc />
    protected override IEnumerable<SessionEntry> Enumerate(AiSignIn? signIn, string workspaceDir) =>
        FilesIn(WatchDirectory(signIn, workspaceDir), "*.jsonl")
            .Select(file => (File: file, Id: SessionIdIn(file.Name)))
            .Where(found => found.Id is { Length: > 0 })
            .Select(found => new SessionEntry(found.Id!, StartedAt(found.File),
                found.File.LastWriteTimeUtc, found.File));

    /// <inheritdoc />
    protected override AgentSessionReading? Read(AiSignIn? signIn, string workspaceDir, SessionEntry entry)
    {
        if (FileOf(entry) is not { } file) return null;

        var summary = RecordsCost ? CostTail.Read(file) : LastTokens(file.FullName);
        return new AgentSessionReading(entry.Id, file.LastWriteTimeUtc, summary.UsedTokens,
            CostUsd: summary.AnyCost ? summary.CostUsd : null, Model: summary.Model);
    }

    /// <summary>What the transcript says so far: the last tokens and model, and every price added up.
    /// </summary>
    private readonly record struct Summary(long? UsedTokens, string? Model, decimal CostUsd, bool AnyCost);

    /// <summary>The running sum a store that <see cref="RecordsCost"/> is read into, kept per file so a
    /// turn costs the lines it appended rather than the whole conversation again.</summary>
    private JsonlFoldTail<Summary> CostTail => _costTail ??= new JsonlFoldTail<Summary>(Fold, default);

    private JsonlFoldTail<Summary>? _costTail;

    /// <summary>One line, read forwards: the later tokens and model win, and a price is added.</summary>
    private Summary Fold(Summary summary, string line) =>
        Parse(line) is not { } found
            ? summary
            : new Summary(found.UsedTokens ?? summary.UsedTokens, found.Model ?? summary.Model,
                summary.CostUsd + (found.CostUsd ?? 0), summary.AnyCost || found.CostUsd is not null);

    /// <summary>The last line carrying tokens, read backwards so only the tail of the file is read.
    /// </summary>
    private Summary LastTokens(string path)
    {
        foreach (var line in ReadLinesBackwards(path))
        {
            if (Parse(line) is { UsedTokens: { } used } found)
                return new Summary(used, found.Model, 0, false);
        }

        return default;
    }

    /// <summary>One line of the transcript, or null when it says nothing or is not whole yet.</summary>
    /// <remarks>A half-written last line is the ordinary case here: the CLI is appending to this file
    /// while we read it. Skipped rather than fatal — the line before it is as good an answer.</remarks>
    private LineReading? Parse(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            return ReadLine(document.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The transcript a session entry was enumerated from, or null when it has gone since.
    /// </summary>
    /// <remarks>The entry carries its file (<see cref="Enumerate"/> found it by that file), so this is a
    /// <c>stat</c> rather than a second listing of a directory holding every transcript ever made in the
    /// project — asked on every debounced write during a turn. Refreshed, because the file is still being
    /// appended to since it was listed.</remarks>
    protected static FileInfo? FileOf(SessionEntry entry)
    {
        if (entry.File is not { } file) return null;
        file.Refresh();
        return file.Exists ? file : null;
    }

    /// <summary>The file's first lines, in order, reading no further than asked.</summary>
    /// <remarks>Shared for the reason <see cref="ReadLinesBackwards"/> is: the CLI is writing to this
    /// file while it is read.</remarks>
    protected static IEnumerable<string> ReadFirstLines(string path, int count)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        for (var read = 0; read < count && reader.ReadLine() is { } line; read++)
            yield return line;
    }

    /// <summary>The file's lines, last first, without holding the whole file in memory.</summary>
    /// <remarks><para><b>Read with sharing, because the CLI has this file open and is writing to it.</b>
    /// Opened without <see cref="FileShare.ReadWrite"/> the read fails on Windows every time the agent is
    /// actually running, which is the only time anybody wants the answer.</para>
    /// <para>Read in blocks from the end, so a caller that stops early has read only the tail. Split on
    /// the newline byte, which UTF-8 never uses inside a character; a line half-written at the end is
    /// yielded as it stands and left to the parser to refuse.</para></remarks>
    private static IEnumerable<string> ReadLinesBackwards(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var position = stream.Length;
        var carry = Array.Empty<byte>();

        while (position > 0)
        {
            var size = (int)Math.Min(BackwardsBlockSize, position);
            position -= size;
            var block = new byte[size + carry.Length];
            stream.Position = position;
            stream.ReadExactly(block, 0, size);
            carry.CopyTo(block, size);

            var end = block.Length;
            for (var i = block.Length - 1; i >= 0; i--)
            {
                if (block[i] != (byte)'\n') continue;
                if (LineOf(block, i + 1, end) is { } line) yield return line;
                end = i;
            }

            carry = block[..end];
        }

        var first = HasUtf8Bom(carry) ? Utf8Bom.Length : 0;
        if (LineOf(carry, first, carry.Length) is { } firstLine) yield return firstLine;
    }

    private const int BackwardsBlockSize = 64 * 1024;

    private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];

    private static bool HasUtf8Bom(byte[] bytes) => bytes.AsSpan().StartsWith(Utf8Bom);

    /// <summary>The text between two offsets less a trailing carriage return, or null when empty.</summary>
    private static string? LineOf(byte[] bytes, int start, int end)
    {
        if (end > start && bytes[end - 1] == (byte)'\r') end--;
        return end > start ? Encoding.UTF8.GetString(bytes, start, end - start) : null;
    }

    /// <summary>The slug five of these CLIs make of a path: everything that is not a letter or a digit
    /// becomes a dash.</summary>
    /// <remarks>Measured 2026-09-18 — <c>D:\work\sources\kursalpha.eu</c> is filed by Claude Code as
    /// <c>D--work-sources-kursalpha-eu</c>, so the dot goes the same way as the colon and the separator.
    /// Pure, and pinned by a table test, because it is somebody else's naming scheme and the whole
    /// reading misses the directory by one character if it moves.</remarks>
    protected static string Slug(string path) =>
        string.Create(path.Length, path, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
                span[i] = char.IsAsciiLetterOrDigit(source[i]) ? source[i] : '-';
        });
}
