using mTiles.Services;

namespace mTiles.ViewModels;

/// <summary>A file the composer's text names by an <c>@</c> mention — one chip above the box.</summary>
/// <remarks>
/// <para><b>Read off the text, never kept beside it</b>, as the Goal tile's image chips are: the mention is
/// what the agent is sent, so deleting it by hand takes the chip away, a mention typed through the
/// <c>@</c> list gets one too, and the two cannot disagree. Only a mention naming something that exists is
/// drawn — <c>@admin</c> in a sentence is prose. Found by <see cref="ComposerFileScanner"/>.</para>
/// </remarks>
public sealed record ComposerFile(int Start, int Length, string Mention, string FullPath, bool IsDirectory)
{
    /// <summary>What the chip says: "125 words" for a pasted note, the file's own name for anything else.</summary>
    /// <remarks>A note is a paste the composer was too small to hold (<see cref="PastedNote"/>), so its
    /// name — which is the agent's whole account of it — is not what the person who pasted it wants to read
    /// back. How many words it was is.</remarks>
    public string Label => PastedNote.LabelFor(Name) ?? Name;

    /// <summary>Whether this mention is a paste folded into a note.</summary>
    public bool IsPastedNote => PastedNote.LabelFor(Name) is not null;

    public string Name => Path.GetFileName(FullPath.TrimEnd('/', '\\')) is { Length: > 0 } name ? name : FullPath;

    /// <summary>The text with this mention taken out, with the one space after it.</summary>
    public string RemoveFrom(string text)
    {
        if (Start + Length > text.Length || text.Substring(Start, Length) != Mention) return text;
        var length = Start + Length < text.Length && text[Start + Length] == ' ' ? Length + 1 : Length;
        return text.Remove(Start, length);
    }
}
