using System.Text.RegularExpressions;
using mTiles.AgentSessions.Events;

namespace mTiles.AgentSessions;

/// <summary>
/// Where an image stands in the words of a message: <c>[Image #n]</c>, the marker both composers insert at
/// the caret, and the rule that turns a message holding markers into the ordered pieces an agent is sent.
/// </summary>
/// <remarks>
/// <para><b>The order of the images is the order of their markers in the text.</b> "It used to look like
/// [Image #1] and now it should look like [Image #2]" says nothing once the two pictures are a list beside
/// the sentence, which is what the Agent tile sent before it had markers. So the text carries the order, and
/// an <see cref="AgentTurnInput"/>'s images are numbered by it: the first image is <c>[Image #1]</c>.</para>
/// <para>Here rather than in the application because every session reads it — the ones whose protocol
/// takes interleaved blocks (Claude Code, ACP, codex, opencode) split the text at the markers, and the rest
/// keep the markers in the text and send the pictures in the same numbered order.</para>
/// <para>The spelling is Claude Code's own, which is what somebody pasting a screenshot at an agent
/// already expects to see appear where their caret was.</para>
/// </remarks>
public static partial class ImageMarkers
{
    public static string For(int index) => $"[Image #{index}]";

    /// <summary>The numbers of the markers in <paramref name="text"/>, in the order they first appear.</summary>
    public static IReadOnlyList<int> InOrder(string? text)
    {
        if (string.IsNullOrEmpty(text)) return [];
        var seen = new List<int>();
        foreach (Match match in Pattern().Matches(text))
            if (int.TryParse(match.Groups[1].Value, out var index) && !seen.Contains(index))
                seen.Add(index);
        return seen;
    }

    /// <summary>
    /// The text with every marker removed whose number is not in <paramref name="kept"/>, together with the
    /// one space inserted after it — so a removal does not leave a double space in a sentence.
    /// </summary>
    public static string DropExcept(string text, IReadOnlyCollection<int> kept) =>
        string.IsNullOrEmpty(text)
            ? text
            : PatternWithSpace().Replace(text, match =>
                int.TryParse(match.Groups[1].Value, out var index) && kept.Contains(index) ? match.Value : "");

    /// <summary>Rewrites every marker's number through <paramref name="map"/>; a number not in it is left.</summary>
    public static string Renumber(string text, IReadOnlyDictionary<int, int> map) =>
        string.IsNullOrEmpty(text)
            ? text
            : Pattern().Replace(text, match =>
                int.TryParse(match.Groups[1].Value, out var index) && map.TryGetValue(index, out var to)
                    ? For(to)
                    : match.Value);

    /// <summary>
    /// The message as the pieces an interleaving protocol is sent: text up to a marker, the image it names,
    /// the text after it, and so on. The marker itself stays at the end of the text before its image, so the
    /// model can still read "[Image #2]" as a name the sentence refers to.
    /// </summary>
    /// <remarks>
    /// An image no marker names — a message from before markers existed, or one a caller built by hand — goes
    /// after the text, which is where every image went before this. A marker naming no image is left in the
    /// text as words. Blank text between two markers is dropped rather than sent as an empty block, which
    /// Anthropic's API refuses — except when nothing else would be sent: a message is never no blocks at
    /// all, so a blank one is its own text, once.
    /// </remarks>
    public static IReadOnlyList<TurnPart> Interleave(string text, IReadOnlyList<ImageAttachment> images)
    {
        var parts = new List<TurnPart>();
        var placed = new bool[images.Count];
        var from = 0;
        foreach (Match match in Pattern().Matches(text ?? ""))
        {
            if (!int.TryParse(match.Groups[1].Value, out var index) || index < 1 || index > images.Count ||
                placed[index - 1])
                continue;
            AddText(parts, text![from..(match.Index + match.Length)]);
            parts.Add(new TurnPart.Image(images[index - 1]));
            placed[index - 1] = true;
            from = match.Index + match.Length;
        }

        AddText(parts, (text ?? "")[from..]);
        for (var i = 0; i < images.Count; i++)
            if (!placed[i]) parts.Add(new TurnPart.Image(images[i]));
        if (parts.Count == 0) parts.Add(new TurnPart.Text(text ?? ""));
        return parts;
    }

    private static void AddText(List<TurnPart> parts, string text)
    {
        if (!string.IsNullOrWhiteSpace(text)) parts.Add(new TurnPart.Text(text));
    }

    [GeneratedRegex(@"\[Image #(\d+)\]")]
    private static partial Regex Pattern();

    [GeneratedRegex(@"\[Image #(\d+)\] ?")]
    private static partial Regex PatternWithSpace();
}

/// <summary>One piece of a message, in the order it is said.</summary>
public abstract record TurnPart
{
    /// <summary>This piece in a protocol's own shape — one answer per kind, so a session cannot forget one.</summary>
    public abstract T Map<T>(Func<string, T> text, Func<ImageAttachment, T> image);

    public sealed record Text(string Value) : TurnPart
    {
        public override T Map<T>(Func<string, T> text, Func<ImageAttachment, T> image) => text(Value);
    }

    public sealed record Image(ImageAttachment Attachment) : TurnPart
    {
        public override T Map<T>(Func<string, T> text, Func<ImageAttachment, T> image) => image(Attachment);
    }
}
