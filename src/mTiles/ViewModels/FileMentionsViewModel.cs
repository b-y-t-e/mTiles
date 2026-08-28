using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using mTiles.Services;

namespace mTiles.ViewModels;

/// <summary>
/// The file suggestions behind an <c>@</c>: what is being offered, which row is picked, and whether any
/// of it is on screen.
/// </summary>
/// <remarks>
/// <para>One of these serves every text box in a tile. The boxes never both have the keyboard, so the
/// state cannot be in two places at once — and sharing it also shares the reading of the working tree,
/// which is the part that costs anything.</para>
/// <para><b>UI thread only.</b> <see cref="UpdateAsync"/> awaits the source and resumes on Avalonia's
/// synchronisation context, so the collection is touched from one thread.</para>
/// </remarks>
public sealed partial class FileMentionsViewModel(IFileMentionSource source) : ObservableObject, IDisposable
{
    /// <summary>
    /// Cancelled when the tile goes, so a reading of the tree stops with it.
    /// </summary>
    /// <remarks>
    /// <para>The source folds this into its own budget rather than replacing it — see
    /// <c>WorkspaceFileMentionSource.ReadTimeout</c> — and that sentence was written before anything
    /// passed a token at all: every call was <c>GetPathsAsync()</c>, so the only thing that ever ended
    /// a reading was the ten-second budget. A closed tile left a git process running out its clock over
    /// a workspace nobody is looking at.</para>
    /// <para>Small, and worth having anyway: it is what makes the comment on the other side true, and
    /// closing a tile mid-read is exactly when the answer has stopped mattering.</para>
    /// </remarks>
    private readonly CancellationTokenSource _closed = new();

    /// <summary>
    /// Cancelled and <b>not disposed</b>, the convention the rest of this tile follows.
    /// </summary>
    /// <remarks>
    /// A disposed source throws from <c>Token</c>, and the only readers of that property are on the
    /// keystroke path — so disposing it turns "the tile is closing" into an exception inside the very
    /// guard that is there to keep a keystroke quiet. A cancelled token says the same thing and can
    /// still be read. The cost is a token source held until the tile is collected, which is what it was
    /// anyway.
    /// </remarks>
    public void Dispose()
    {
        // Idempotent: the tile's own Dispose is, and this is reached from it.
        if (!_closed.IsCancellationRequested) _closed.Cancel();
    }

    /// <summary>
    /// Which update the popup currently belongs to.
    /// <para>A keystroke does not wait for the one before it: the tree is read once and cached, but the
    /// first read of a large repository takes long enough for three more letters to arrive, and without
    /// this the answer to <c>@g</c> would land on top of the answer to <c>@goal</c>.</para>
    /// </summary>
    private int _revision;

    public ObservableCollection<string> Suggestions { get; } = [];

    [ObservableProperty] private bool _isOpen;

    [ObservableProperty] private int _selectedIndex = -1;

    /// <summary>The path the user would get by pressing Enter, or null when there is nothing to take.</summary>
    public string? SelectedPath =>
        SelectedIndex >= 0 && SelectedIndex < Suggestions.Count ? Suggestions[SelectedIndex] : null;

    /// <summary>
    /// Offers what fits the mention under the caret, or puts the popup away when there is no mention there.
    /// </summary>
    public async Task UpdateAsync(string? text, int caretIndex)
    {
        if (FileMentionToken.At(text, caretIndex) is not { } token)
        {
            Close();
            return;
        }

        var revision = ++_revision;

        // Deliberately no delay before this. Tools of this kind debounce the search by around 50 ms
        // because they filter an index of a quarter of a million paths; here the tree is cached and the scorer is
        // one pass over a few thousand strings, so a wait would only add latency to every keystroke.
        // It is also not free to add: `Task.Delay` yields unconditionally, and the continuation lands
        // on whatever thread has it when there is no synchronisation context to post back to — which is
        // this collection, bound to a list, being rewritten off the UI thread. The revision guard below
        // is what a debounce was wanted for, and it does not have to leave the thread to do it.
        // The matching is inside the guard as well as the reading, and that is not tidiness: this runs
        // from `_ = UpdateAsync(...)` on a keystroke, so anything thrown here is an unobserved task
        // exception — no dialog, no stack in front of anybody, and a popup frozen in whatever state it
        // was in. Every keystroke after it would do the same. What the user should see either way is a
        // list that does not appear.
        try
        {
            var paths = await source.GetPathsAsync(_closed.Token);

            // Not Close(): a later keystroke already owns the popup, and closing it here would put away
            // suggestions that are more current than these.
            if (revision != _revision) return;

            Show(FileMentionMatcher.Match(CorpusFor(paths), token.Query));
        }
        catch (Exception ex)
        {
            // Nothing to suggest is a popup that does not appear, which is the whole of what the user
            // should notice. A dialog over a keystroke would not be.
            Trace.TraceWarning($"Offering files for a mention failed: {ex.Message}");
            Close();
        }
    }

    /// <summary>Moves the pick, wrapping round both ends. False when there was nothing to move.</summary>
    public bool MoveSelection(int delta)
    {
        if (!IsOpen || Suggestions.Count == 0) return false;

        var next = (SelectedIndex + delta) % Suggestions.Count;
        SelectedIndex = next < 0 ? next + Suggestions.Count : next;
        return true;
    }

    /// <summary>
    /// The box with the mention under the caret replaced by the chosen path, or null when there is
    /// nothing to replace.
    /// </summary>
    /// <param name="path">The row that was clicked, or null for whichever row is picked.</param>
    /// <remarks>
    /// The token is worked out again from the text as it is now rather than remembered from the update
    /// that filled the list: what the popup was answering and what is in the box are two different
    /// things the moment a key arrives while the tree is being read.
    /// </remarks>
    public FileMentionCompletion? Complete(string? text, int caretIndex, string? path = null)
    {
        if (!IsOpen) return null;

        var chosen = path ?? SelectedPath;
        if (chosen == null || text == null) return null;

        var token = FileMentionToken.At(text, caretIndex);
        if (token is not { } at) return null;

        // A folder is a step, not an answer. Taking one types it and leaves the popup up on what is
        // inside it, so Enter walks down a tree the same way Tab does — and the mention is left
        // unfinished, without the trailing space, because `@src/` names a place the user has not
        // finished naming.
        if (IsDirectory(chosen))
        {
            return at.Extend(text, chosen);
        }

        Close();
        return at.Complete(text, chosen);
    }

    /// <summary>
    /// What Tab does: types the part every row agrees on, and leaves the list up.
    /// </summary>
    /// <remarks>
    /// <para>The shell's completion, which is the one every user of this application already has in
    /// their fingers: Tab narrows, and only picks when there is nothing left to narrow. Enter is what
    /// takes the row that is lit.</para>
    /// <para>Falls through to <see cref="Complete"/> when the rows share no more than what has been
    /// typed — otherwise Tab would be the one key in the popup that did nothing.</para>
    /// </remarks>
    public FileMentionCompletion? CompleteCommonPrefix(string? text, int caretIndex)
    {
        if (!IsOpen || text == null) return null;
        if (FileMentionToken.At(text, caretIndex) is not { } token) return null;

        var prefix = FileMentionToken.CommonPrefix([..Suggestions]);
        if (prefix.Length <= token.Query.Length) return Complete(text, caretIndex);

        return token.Extend(text, prefix);
    }

    /// <summary>A row that names a folder rather than a file.</summary>
    /// <remarks>The trailing separator is put there by whatever supplies the paths, and it is the only
    /// thing that tells the two apart — nothing here touches a disk.</remarks>
    private static bool IsDirectory(string path) => path.EndsWith('/');

    /// <summary>The last corpus built, and the list it was built from.</summary>
    private (IReadOnlyList<string> Paths, FileMentionCorpus Corpus)? _corpus;

    /// <summary>
    /// The corpus for this reading of the tree, built once and kept until the tree is read again.
    /// </summary>
    /// <remarks>
    /// <para>Keyed on the identity of the list, not its contents. The source hands back the very same
    /// instance for as long as its reading stands and a fresh one when it does not, so reference
    /// equality is exactly the question "is this still the reading I folded?" — and answering it costs
    /// one comparison rather than a walk of two hundred thousand strings.</para>
    /// <para>Here rather than in the source because it is the scorer's business: the source knows what
    /// files there are, and what a fast search needs precomputed is a fact about the search. It also
    /// keeps <see cref="IFileMentionSource"/> a list of paths, which is what makes it testable with a
    /// literal.</para>
    /// <para><b>Built here, on this thread, deliberately.</b> The reading of the tree goes to the pool
    /// because it is unbounded work behind a process; this is bounded work once per reading — 28 ms at
    /// the declared ceiling of two hundred thousand paths, and well under a millisecond at the size any
    /// real repository has.</para>
    /// <para>Moving it to <c>Task.Run</c> was tried and reverted. It yields unconditionally, and with
    /// no synchronisation context to post back to the continuation rewrites <see cref="Suggestions"/> —
    /// bound to a list — from a pool thread. That is the same trap the debounce fell into, and it costs
    /// the invariant this class is built on to buy a few milliseconds once every five seconds.</para>
    /// </remarks>
    private FileMentionCorpus CorpusFor(IReadOnlyList<string> paths)
    {
        if (_corpus is { } cached && ReferenceEquals(cached.Paths, paths)) return cached.Corpus;

        var corpus = new FileMentionCorpus(paths);
        _corpus = (paths, corpus);

        return corpus;
    }

    /// <summary>Puts the popup away, and disowns any reading still in flight.</summary>
    public void Close()
    {
        _revision++;
        IsOpen = false;
        SelectedIndex = -1;
        Suggestions.Clear();
    }

    private void Show(IReadOnlyList<string> matches)
    {
        Suggestions.Clear();
        foreach (var match in matches) Suggestions.Add(match);

        SelectedIndex = matches.Count > 0 ? 0 : -1;
        IsOpen = matches.Count > 0;
    }
}
