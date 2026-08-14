namespace mTiles.Services.Speech;

/// <summary>
/// A Parakeet <c>vocab.txt</c>: one <c>token id</c> per line, indexed by id.
/// </summary>
/// <remarks>
/// SentencePiece marks the start of a word with <c>▁</c> (U+2581) rather than a space, so that is
/// turned back into a space on the way in — which is what makes joining the tokens produce text.
/// A port of <c>decode/tokens.rs::load_vocab</c> in transcribe-rs.
/// </remarks>
internal sealed class ParakeetVocabulary
{
    private readonly string[] _tokens;

    private ParakeetVocabulary(string[] tokens, int blankIndex)
    {
        _tokens = tokens;
        BlankIndex = blankIndex;
    }

    public int Count => _tokens.Length;

    /// <summary>Index of <c>&lt;blk&gt;</c> — the transducer's "nothing here", not a token of text.</summary>
    public int BlankIndex { get; }

    public string this[int id] => id >= 0 && id < _tokens.Length ? _tokens[id] : "";

    public static ParakeetVocabulary Load(string path) => Parse(File.ReadLines(path));

    internal static ParakeetVocabulary Parse(IEnumerable<string> lines)
    {
        var byId = new List<(string Token, int Id)>();
        var maxId = 0;
        int? blank = null;

        foreach (var line in lines)
        {
            // The token is everything before the last space, and that includes a token that *is* a
            // space — which the guard used to reject by requiring a separator past position 0, quietly
            // contradicting this comment. Nothing in the shipped vocabulary looks like that (checked:
            // 8193 lines, none with its last space at the start), so this is the rule being right
            // rather than a bug being fixed. A line with no id still falls out at the parse.
            var trimmed = line.TrimEnd('\r', '\n');
            var separator = trimmed.LastIndexOf(' ');
            // A negative id falls out with the rest: it parses, but it indexes nothing, and letting it
            // through means an IndexOutOfRangeException from the array built below rather than the "a
            // line without an id simply drops" this claims to do. The file is digest-checked, so this
            // guards against a shape the shipped vocabulary cannot have — for the price of one clause.
            if (separator < 0 || !int.TryParse(trimmed[(separator + 1)..], out var id) || id < 0)
                continue;

            var token = trimmed[..separator];
            if (token == "<blk>")
                blank = id;

            byId.Add((token, id));
            maxId = Math.Max(maxId, id);
        }

        if (byId.Count == 0 || blank is null)
            throw new InvalidDataException("The vocabulary is empty or has no <blk> token.");

        var tokens = new string[maxId + 1];
        Array.Fill(tokens, "");
        foreach (var (token, id) in byId)
            tokens[id] = token.Replace('▁', ' ');

        return new ParakeetVocabulary(tokens, blank.Value);
    }
}
