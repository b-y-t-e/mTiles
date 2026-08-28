namespace mTiles.Services;

/// <summary>
/// The paths a mention can offer, with the two things the scorer would otherwise work out again on
/// every keystroke.
/// </summary>
/// <remarks>
/// <para>The file index a popup like this keeps, reduced to what this needs. It holds a
/// lower-cased copy of every path beside the real one and a 26-bit map of which letters <c>a</c>–<c>z</c>
/// the path contains, and both are the difference between a scorer that reads a tree and one that reads
/// it on every letter typed.</para>
/// <para><b>The lower-cased copy.</b> Folding inside the loop is one string allocated per candidate per
/// keystroke — at <see cref="WorkspaceFileMentionSource"/>'s ceiling of two hundred thousand paths,
/// measured at 15 MB a letter, on the UI thread. Scanning with <c>char.ToLowerInvariant</c> instead
/// removes the allocation but gives up the vectorised <c>IndexOf</c> and comes out slower still. Folded
/// once per reading, both problems go.</para>
/// <para><b>The bitmap.</b> A path that does not contain every letter of the query cannot possibly
/// contain them in order, and that is one <c>and</c> against a precomputed <c>int</c> — so most of the
/// corpus is rejected without being read at all. It is why the query length barely moves the cost.</para>
/// <para>Built once per reading of the tree, which is already cached, so its own cost is paid where the
/// git call is paid and not where the keystroke is.</para>
/// </remarks>
public sealed class FileMentionCorpus
{
    /// <summary>Nothing to offer.</summary>
    public static FileMentionCorpus Empty { get; } = new([]);

    /// <summary>The paths as they are spelled, which is what a completed mention writes.</summary>
    public IReadOnlyList<string> Paths { get; }

    /// <summary>
    /// The same paths folded to lower case, <b>index for index</b>.
    /// </summary>
    /// <remarks>
    /// The alignment is what the scorer rests on: it finds positions in this copy and reads its bonuses
    /// — a match after <c>/</c>, a capital in <c>bashTool</c> — out of the original. <see
    /// cref="string.ToLowerInvariant"/> holds it because invariant casing is a one-character-for-one
    /// mapping, unlike the full case mapping a culture can apply, where <c>İ</c> becomes two characters
    /// and every later position in that path goes out by one. That is a property of somebody else's
    /// runtime rather than of this code, so it is measured in <c>FileMentionMatcherTests</c> across the
    /// whole of the BMP instead of being assumed here.
    /// </remarks>
    internal string[] Lowered { get; }

    /// <summary>Which of <c>a</c>–<c>z</c> each lowered path contains, one bit each.</summary>
    internal int[] Letters { get; }

    public int Count => Paths.Count;

    public FileMentionCorpus(IReadOnlyList<string> paths)
    {
        Paths = paths;
        Lowered = new string[paths.Count];
        Letters = new int[paths.Count];

        for (var i = 0; i < paths.Count; i++)
        {
            var lowered = paths[i].ToLowerInvariant();

            Lowered[i] = lowered;
            Letters[i] = LettersIn(lowered);
        }
    }

    /// <summary>The letters of a query, as the bits a candidate has to have all of.</summary>
    /// <remarks>Only <c>a</c>–<c>z</c>. Everything else — digits, separators, accented letters — is
    /// simply not represented, so it contributes no bit and rejects nothing; the scorer still has to
    /// look. A filter that is allowed to miss but never to lie.</remarks>
    internal static int LettersIn(string lowered)
    {
        var bits = 0;

        foreach (var c in lowered)
            if (c is >= 'a' and <= 'z')
                bits |= 1 << (c - 'a');

        return bits;
    }
}
