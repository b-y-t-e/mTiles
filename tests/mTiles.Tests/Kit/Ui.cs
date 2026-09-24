using Avalonia.Headless;
using Avalonia.Threading;

namespace mTiles.Tests;

/// <summary>
/// The headless UI thread, for the tests that need one.
/// </summary>
/// <remarks>
/// <para>A view model that dispatches to the UI thread (every message it adds, every state it saves)
/// deadlocks when driven from the test thread, because nobody is pumping that dispatcher; and a control
/// can only be built and measured on it. So such a test runs its body here. The session is the
/// assembly's one headless session, and each call gets a freshly isolated application.</para>
/// <para>Before this there were forty copies of the same six lines, one per test class.</para>
/// </remarks>
internal static class Ui
{
    private static HeadlessUnitTestSession Session =>
        HeadlessUnitTestSession.GetOrStartForAssembly(typeof(Ui).Assembly);

    /// <summary>Runs <paramref name="body"/> on the UI thread and waits for it.</summary>
    public static void Run(Action body) =>
        Session.Dispatch(() => { body(); return Task.FromResult(true); }, CancellationToken.None)
            .GetAwaiter().GetResult();

    /// <summary>Runs <paramref name="body"/> on the UI thread, awaiting it there, and waits for it.</summary>
    public static void Run(Func<Task> body) =>
        Session.Dispatch(async () => { await body(); return true; }, CancellationToken.None)
            .GetAwaiter().GetResult();

    /// <summary>Runs <paramref name="body"/> on the UI thread and hands back what it answered.</summary>
    public static T Run<T>(Func<T> body) =>
        Session.Dispatch(() => Task.FromResult(body()), CancellationToken.None)
            .GetAwaiter().GetResult();

    /// <summary>Runs every job queued on the dispatcher: layout, bindings, posted work.</summary>
    public static void Pump() => Dispatcher.UIThread.RunJobs();
}
