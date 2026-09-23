namespace mTiles.Views;

/// <summary>
/// How typing into a model field narrows a provider's catalogue.
/// </summary>
/// <remarks>
/// <para>The rule itself is <see cref="mTiles.Controls.PickerSearch"/>, which is where it belongs now that
/// the picker in the composer is the main thing that searches: a control library that cannot say what
/// matching means is one whose search box every host has to hand a lambda to. This name stays because the
/// remaining callers are model fields - Settings, AI page, the two <c>AutoCompleteBox</c>es - and
/// <c>ItemFilter</c> there wants exactly this signature.</para>
/// <para>Deliberately a forwarder and not a copy. Two spellings of one rule is how a model typed in
/// Settings and the same model picked in a tile come to disagree about whether they match.</para>
/// </remarks>
public static class ModelSearch
{
    /// <summary>Whether <paramref name="candidate"/> matches every word in <paramref name="search"/>.</summary>
    public static bool Matches(string? search, string? candidate) =>
        mTiles.Controls.PickerSearch.Matches(search, candidate);
}
