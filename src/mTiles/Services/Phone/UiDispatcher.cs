namespace mTiles.Services.Phone;

/// <summary>
/// Getting onto the UI thread, as an interface so the phone bridge can be tested without one.
/// </summary>
/// <remarks>
/// <see cref="DictationService"/> takes an <c>Action&lt;Action&gt;</c> for the same purpose and that is
/// enough for it, because delivering a transcript is fire-and-forget. The bridge needs an answer back: a
/// phone that presses the button has to be told whether recording actually started, and the only place
/// that can be decided is the UI thread. Posting and then guessing would have the phone show "listening"
/// over a dictation that never began.
/// </remarks>
internal interface IUiDispatcher
{
    void Post(Action action);

    Task<T> InvokeAsync<T>(Func<T> work);
}

internal sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    public void Post(Action action) => Avalonia.Threading.Dispatcher.UIThread.Post(action);

    public Task<T> InvokeAsync<T>(Func<T> work) =>
        Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(work).GetTask();
}
