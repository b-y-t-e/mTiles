using System.Diagnostics;

namespace mTiles.ViewModels;

/// <summary>
/// The context window of the model a conversation is running on <em>now</em> — the gauge's denominator
/// when the agent does not name one itself.
/// </summary>
/// <remarks>
/// <para><b>One of these for both agent tiles</b>, for the reason <see cref="ContextGaugeViewModel"/> is
/// one: the same question about the same conversation, and two copies had already drifted into the same
/// bug.</para>
/// <para><b>The window belongs to a model, and a new model drops it.</b> <c>/model</c> inside the TUI moves
/// a conversation from <c>claude-opus-5</c> (1 000 000) to <c>claude-opus-4-5</c> (200 000); a window
/// kept past that counts a nearly full conversation against five times its real room. So a model the
/// window was not settled for clears it at once — a count with no bar until the answer comes, rather than
/// a bar drawn against the wrong model — and asks again. An answer for a model that has since changed
/// again is discarded, because it describes a question nobody is asking any more.</para>
/// <para>What is asked, and in which order, is the caller's (<c>lookup</c>): the provider, then the
/// agent's own account. This class owns only which model the answer is for.</para>
/// </remarks>
public sealed class ContextWindowFollower
{
    private readonly Func<string, CancellationToken, Task<long?>> _lookup;
    private readonly Action<Action> _post;
    private readonly Action _changed;

    private string _model = "";
    private int _generation;
    private bool _asking;
    private long? _announced;
    private bool _newModelUnannounced;

    /// <param name="lookup">How large a context the model is served with, or null when nobody says.</param>
    /// <param name="post">Gets an answer back onto the thread the caller draws on.</param>
    /// <param name="changed">Called on that thread when a background answer has changed
    /// <see cref="Window"/>, so the caller can redraw against it.</param>
    public ContextWindowFollower(Func<string, CancellationToken, Task<long?>> lookup, Action<Action> post,
        Action changed)
    {
        _lookup = lookup;
        _post = post;
        _changed = changed;
    }

    /// <summary>The window of the model last settled or followed, or null when nobody said.</summary>
    public long? Window { get; private set; }

    /// <summary>Whether a lookup is in flight — read by tests, which cannot wait on an answer that
    /// changes nothing and is therefore never announced.</summary>
    internal bool IsAsking => _asking;

    /// <summary>Settles the window for the model a launch resolved.</summary>
    /// <remarks><para>Fire and forget, like <see cref="Follow"/>: the answer is only the gauge's
    /// denominator, and on a subscription it is an HTTP call that can take its whole timeout — a launch
    /// waiting on it would stand still for a bar. The window is dropped at once and arrives through
    /// <c>changed</c>.</para>
    /// <para>An empty model — a subscription, where the CLI picks its own — is settled too: nothing is
    /// known yet, and the first reading that names a model is then a change and is asked about.</para>
    /// </remarks>
    public void Settle(string model)
    {
        var generation = BeginModel(model);
        if (model.Length > 0) AskInBackground(model, generation);
    }

    /// <summary>Takes the model a reading says the conversation is running on.</summary>
    /// <remarks>Fire and forget: a reading arrives on the drawing thread and the answer is an HTTP call
    /// behind a half-hour cache. Nothing is asked while the model stays the same and has an answer —
    /// <b>a model still without one is asked again</b>, because "nobody said" is often "nobody could say
    /// yet": a subscription's token is not renewed by the gauge (<c>ClaudeCredentialStore.LiveAccessToken</c>),
    /// so the answer arrives once the CLI in the tile has renewed it. Each source keeps its own cache, so
    /// the repeat costs a lookup and not a request.</remarks>
    public void Follow(string? model)
    {
        if (model is not { Length: > 0 }) return;

        if (!NamesTheSameModel(model, _model)) AskInBackground(model, BeginModel(model));
        else if (Window is null && !_asking) AskInBackground(model, _generation);
    }

    private void AskInBackground(string model, int generation)
    {
        _asking = true;
        _ = Task.Run(async () =>
        {
            var window = await LookupAsync(model);
            _post(() => Answer(generation, window));
        });
    }

    private async Task<long?> LookupAsync(string model)
    {
        try
        {
            return await _lookup(model, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Asking the context window of {0} failed: {1}", model, ex.Message);
            return null;
        }
    }

    /// <summary>Takes an answer, unless the model has changed since it was asked for.</summary>
    /// <remarks><b>Announced once per model, and after that only when it differs from what was last
    /// announced.</b> The first answer for a model is always announced, since the caller may still be drawing
    /// against the previous model's window. The caller
    /// answers <c>changed</c> by reading again, and a reading of a model still without a window asks again
    /// (<see cref="Follow"/>) — so announcing a null that changed nothing closed a loop: a subscription
    /// nobody could answer for re-read its store and re-asked every few hundred milliseconds for the life
    /// of the tile. Unannounced, the next ask waits for a reading the store itself produced.</remarks>
    private void Answer(int generation, long? window)
    {
        if (generation != _generation) return;
        _asking = false;
        Window = window;
        if (!_newModelUnannounced && window == _announced) return;
        _newModelUnannounced = false;
        _announced = window;
        _changed();
    }

    private int BeginModel(string model)
    {
        _model = model;
        Window = null;
        _asking = false;
        _newModelUnannounced = true;
        return ++_generation;
    }

    /// <summary>Whether two spellings name one model.</summary>
    /// <remarks>A transcript can carry the id qualified by its provider (<c>openrouter/z-ai/glm-5</c>)
    /// where the launch resolved it bare, and reading that as a change would drop a window that is still
    /// right.</remarks>
    internal static bool NamesTheSameModel(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase)
        || (b.Length > 0 && a.EndsWith("/" + b, StringComparison.OrdinalIgnoreCase))
        || (a.Length > 0 && b.EndsWith("/" + a, StringComparison.OrdinalIgnoreCase));
}
