namespace mTiles.Services;

/// <summary>
/// Walking back through what was sent from a composer, the way a shell walks its history — pure, so the
/// rule is argued in a test rather than through a window.
/// </summary>
/// <remarks>
/// <para><b>The draft is kept.</b> Whatever was in the box when the walk began is put back when the walk
/// comes forward past the newest message, so looking something up never costs the message being written.</para>
/// <para><b>Any edit ends the walk</b> (<see cref="Reset"/>): an edited old message is a new draft, and the
/// next Up starts from the newest message again.</para>
/// </remarks>
public sealed class ComposerHistory
{
    private readonly Func<IReadOnlyList<string>> _entries;
    private int _index = -1; // -1: not walking; otherwise an index into the entries, 0 = oldest.
    private string _draft = "";

    /// <param name="entries">What was sent, oldest first; asked afresh at the start of every walk.</param>
    public ComposerHistory(Func<IReadOnlyList<string>> entries) => _entries = entries;

    public bool IsWalking => _index >= 0;

    /// <summary>The message before the one showing, or null when there is none.</summary>
    public string? Older(string current)
    {
        var entries = _entries();
        if (entries.Count == 0) return null;
        if (_index < 0)
        {
            _draft = current;
            _index = entries.Count;
        }
        if (_index > entries.Count) _index = entries.Count;
        if (_index == 0) return null;
        return entries[--_index];
    }

    /// <summary>The message after the one showing, the draft past the newest, or null when not walking.</summary>
    public string? Newer()
    {
        if (_index < 0) return null;
        var entries = _entries();
        if (++_index < entries.Count) return entries[_index];
        _index = -1;
        return _draft;
    }

    /// <summary>
    /// Goes straight to one message — picked from the list rather than walked to — keeping the draft exactly as
    /// a walk would, so coming forward past the newest message still gives it back.
    /// </summary>
    /// <returns>The message at <paramref name="index"/>, or null when there is no such message.</returns>
    public string? JumpTo(int index, string current)
    {
        var entries = _entries();
        if (index < 0 || index >= entries.Count) return null;
        if (_index < 0) _draft = current;
        _index = index;
        return entries[index];
    }

    /// <summary>
    /// What a composer's history is made of: blank messages dropped, and a message sent again straight after
    /// itself kept once — so a repeat is one step back, not two. The same message sent earlier stays where it
    /// was, because where it was is when it was sent.
    /// </summary>
    public static IReadOnlyList<string> CollapseRepeats(IEnumerable<string> sent)
    {
        var list = new List<string>();
        foreach (var entry in sent)
            if (!string.IsNullOrWhiteSpace(entry) && (list.Count == 0 || list[^1] != entry))
                list.Add(entry);
        return list;
    }

    /// <summary>Ends the walk: the box now holds something of its own.</summary>
    public void Reset() => _index = -1;
}
