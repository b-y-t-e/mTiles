namespace mTiles.Views;

/// <summary>
/// How typing into a model field narrows a provider's catalogue.
/// </summary>
/// <remarks>
/// <para><b>Every word, anywhere, in any order</b> — <c>glm 5.3</c> finds
/// <c>z-ai/glm-5.3-flash</c>, and so does <c>5.3 glm</c>. A plain "contains" cannot: model ids are
/// punctuated by whoever published them (<c>z-ai/glm-5.3-flash</c>, <c>gpt-5.5</c>,
/// <c>qwen3-embedding:4b</c>), so the one thing a user will not type correctly is the separator between
/// two parts they do remember. Against 396 OpenRouter entries that is the difference between a search
/// box and a guessing game.</para>
/// <para><b>A dot is not a separator</b>, and that is the one entry worth arguing about: it is how
/// version numbers are written, so splitting on it would let <c>5.3</c> match a model with a <c>5</c>
/// in one place and a <c>3</c> in another. Everything else that punctuates an id is treated as
/// equivalent to a space, so a word typed with one separator matches an id written with another.</para>
/// <para>Pure and in a class of its own rather than inline in the view, for the reason
/// <c>WorkspaceDisplayOrder</c> is: this is an opinion about matching, and an opinion is a thing to
/// argue with a table test rather than to rediscover from a screenshot.</para>
/// </remarks>
public static class ModelSearch
{
    /// <summary>What counts as punctuation between two words. Deliberately without <c>.</c>.</summary>
    private static readonly char[] Separators = [' ', '\t', '-', '_', '/', ':', ',', '|'];

    /// <summary>Whether <paramref name="candidate"/> matches every word in <paramref name="search"/>.
    /// </summary>
    /// <remarks>An empty search matches everything, which is what lets the field offer the whole
    /// catalogue rather than nothing at all before a key has been pressed.</remarks>
    public static bool Matches(string? search, string? candidate)
    {
        var words = Words(search);
        if (words.Length == 0) return true;
        if (string.IsNullOrEmpty(candidate)) return false;

        // Two readings of the same id, and a word may match either. Flattened, a word typed with one
        // separator finds an id written with another; stripped, a word typed with none at all finds one
        // that has them — "zai" for `z-ai`, which is exactly how a vendor's name gets typed by somebody
        // who has only ever read it. Neither is shown: the id keeps the punctuation its publisher gave
        // it, and only the searching is forgiving.
        var flattened = Flattened(candidate);
        var stripped = Stripped(candidate);

        return words.All(word =>
            flattened.Contains(word, StringComparison.OrdinalIgnoreCase)
            // The forgiving spelling is for punctuation the user left out, not for the dot that holds a
            // version together: without this, "5.3" matched `gpt-5-3-turbo` through the stripped form
            // and undid the rule the dot is excluded from Separators to keep.
            || (!word.Contains('.')
                && stripped.Contains(Stripped(word), StringComparison.OrdinalIgnoreCase)));
    }

    private static string[] Words(string? search) =>
        string.IsNullOrWhiteSpace(search)
            ? []
            : search.Split(Separators,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string Flattened(string candidate) =>
        new([.. candidate.Select(c => Separators.Contains(c) ? ' ' : c)]);

    /// <summary>The same text with the punctuation gone entirely, so a word run together matches an id
    /// that is not.</summary>
    /// <remarks>The dot goes here too, and only here: <c>5.3</c> stays one word when the search is
    /// split, so removing the dot on both sides cannot let a <c>5</c> and a <c>3</c> drift apart — it
    /// only lets <c>glm53</c> find <c>glm-5.3</c>.</remarks>
    private static string Stripped(string candidate) =>
        new([.. candidate.Where(c => !Separators.Contains(c) && c != '.')]);
}
