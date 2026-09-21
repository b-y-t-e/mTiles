using System.Text.RegularExpressions;

namespace mTiles.Services;

/// <summary>
/// What a long paste becomes in a composer: a file under <c>.mtiles/attachments/</c> and an <c>@</c> mention
/// of it where the caret was, rather than the whole of it in the box.
/// </summary>
/// <remarks>
/// <para><b>Why not the text itself.</b> A composer is three lines tall; a pasted review, a stack trace or a
/// page of notes fills it and pushes everything the user meant to write around it off the screen — and in
/// the Goal tile the prompt is an <c>argv</c> with a budget (<see cref="CommandLineLength"/>), so a long
/// paste there does not merely look bad, it does not fit. So the paste is handed over the way every other
/// non-image attachment is (<see cref="ComposerFileReference"/>): a path, never the contents, and the agent
/// reads it for itself.</para>
/// <para><b>It is an ordinary attachment and deliberately nothing new.</b> The mention is what the chip is
/// drawn from, what the agent is sent and what is saved with the draft, so a pasted note survives a reload,
/// is taken back out by deleting its mention, and — being under <c>.mtiles/attachments/</c> — is already
/// excluded from the Goal tile's scope filter by <see cref="AttachmentStore.IsContextOnly"/>: something to
/// read, never part of the work's scope.</para>
/// <para><b>The word count is in the file's name</b> (<c>notes-125-words.md</c>) and that is load-bearing
/// twice over: the chip reads it back without opening the file — it is redrawn on every keystroke — and the
/// agent, which is handed the name and nothing else, learns from it what it has been given.</para>
/// </remarks>
public static partial class PastedNote
{
    /// <summary>Past either of these a paste is folded into a note instead of going into the box.</summary>
    /// <remarks>Two measures rather than one because they fail apart: a pasted diff or stack trace is forty
    /// lines and well under two thousand characters, and a wall of prose is one line and far over it.</remarks>
    public const int LongEnoughCharacters = 2000;

    public const int LongEnoughLines = 20;

    /// <summary>Whether this paste is long enough to be folded into a note.</summary>
    public static bool IsLong(string? text) =>
        text is not null &&
        (text.Length > LongEnoughCharacters || text.AsSpan().Count('\n') + 1 > LongEnoughLines);

    /// <summary>How many words the note holds — what its chip says and what its name carries.</summary>
    public static int WordsIn(string? text) =>
        string.IsNullOrWhiteSpace(text) ? 0 : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>What a note of <paramref name="words"/> words is called, before the store's timestamp.</summary>
    public static string FileNameFor(int words) => $"notes-{words}-words.md";

    /// <summary>"125 words" for a note's file name, null for any other file.</summary>
    /// <remarks>Read off the name so the chip costs no read: it is redrawn on every keystroke. A file of the
    /// user's own that happens to be called this reads as a note, which is a chip with the right number on
    /// it — the name says how many words it claims, and nothing else here depends on the answer.</remarks>
    public static string? LabelFor(string? fileName) =>
        fileName is not null && NamePattern().Match(fileName) is { Success: true } match
            ? $"{match.Groups[1].Value} words"
            : null;

    /// <summary>
    /// Writes <paramref name="text"/> beside the workspace's other attachments and answers the file, or null
    /// when there is nowhere to put it — a tile with no workspace directory, or a disk that refused.
    /// </summary>
    /// <remarks>Off the caller's thread, as <see cref="AttachmentStore.PlaceAsync"/> is, and for the same
    /// reason: this is called from a keystroke. A refusal is a null rather than a throw, because the paste
    /// then simply goes into the box as it always did — a note that cannot be written must not be a paste
    /// that is lost.</remarks>
    public static Task<string?> WriteAsync(string text, string workspaceDirectory) =>
        Task.Run<string?>(() =>
        {
            if (string.IsNullOrEmpty(workspaceDirectory)) return null;
            try
            {
                var directory = AttachmentStore.CopiesDirectory(workspaceDirectory);
                Directory.CreateDirectory(directory);
                var path = AttachmentStore.FreePath(directory, FileNameFor(WordsIn(text)));
                File.WriteAllText(path, text);
                return path;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Trace.TraceWarning($"[Attachments] A pasted note could not be written: {ex.Message}");
                return null;
            }
        });

    [GeneratedRegex(@"notes-(\d+)-words\.md$", RegexOptions.IgnoreCase)]
    private static partial Regex NamePattern();
}
