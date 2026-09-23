using mTiles.AgentSessions;
using mTiles.ViewModels;

namespace mTiles.Services;

/// <summary>
/// A composer's text and where its caret stands, and the three edits both composers make to them: an
/// attachment put in at the caret, and an image's marker or a file's mention taken back out.
/// </summary>
/// <remarks>One rule for the Agent and the Goal tile, because the two had drifted: one padded an insertion
/// and the other did not, so a file dropped straight after a word was welded onto it and stopped being a
/// mention at all — no chip, no scope.</remarks>
public readonly record struct ComposerEdit(string Text, int Caret)
{
    /// <summary>Puts <paramref name="insertion"/> where the caret is, and leaves the caret after it.</summary>
    /// <remarks>A space before it when it would otherwise be welded onto the word the caret stands after —
    /// a mention is read only after whitespace — and one after it so the next word typed is not welded on.</remarks>
    public ComposerEdit Insert(string insertion)
    {
        var at = Math.Clamp(Caret, 0, Text.Length);
        if (at > 0 && !char.IsWhiteSpace(Text[at - 1])) insertion = " " + insertion;
        if (at == Text.Length || !char.IsWhiteSpace(Text[at])) insertion += " ";
        return new ComposerEdit(Text.Insert(at, insertion), at + insertion.Length);
    }

    /// <summary>Takes out every image marker whose number is not in <paramref name="kept"/>.</summary>
    /// <remarks>The caret moves back only by what was taken out in front of it, so a caret standing before
    /// the marker stays where it was.</remarks>
    public ComposerEdit DropImageMarkersExcept(IReadOnlyCollection<int> kept)
    {
        var caret = Math.Clamp(Caret, 0, Text.Length);
        var textBeforeCaret = ImageMarkers.DropExcept(Text[..caret], kept);
        var text = ImageMarkers.DropExcept(Text, kept);
        return new ComposerEdit(text, Math.Min(textBeforeCaret.Length, text.Length));
    }

    /// <summary>Takes one image's marker out, and leaves every other marker where it stands.</summary>
    public ComposerEdit RemoveImage(int index) =>
        DropImageMarkersExcept([.. ImageMarkers.InOrder(Text).Where(named => named != index)]);

    /// <summary>Takes a file's mention out; a caret after it moves back with the text, one inside it goes to
    /// where the mention began.</summary>
    public ComposerEdit RemoveFile(ComposerFile file)
    {
        var text = file.RemoveFrom(Text);
        var removed = Text.Length - text.Length;
        var caret = Caret <= file.Start ? Caret : Math.Max(file.Start, Caret - removed);
        return new ComposerEdit(text, Math.Clamp(caret, 0, text.Length));
    }
}
