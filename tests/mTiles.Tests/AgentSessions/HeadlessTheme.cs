using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;

namespace mTiles.Tests.AgentSessions;

/// <summary>
/// The application's look for a view laid out in the headless session: Fluent's control templates, the
/// pickers from <c>mTiles.Controls</c> and the <c>AppTheme</c> tokens — on the application and taken off
/// again on disposal, because the test application is shared by the whole assembly.
/// </summary>
/// <remarks>On the application, never the window: a theme added to a window leaves an
/// <c>ItemsControl</c> untemplated. Without the picker styles the composer's pickers draw nothing, and a
/// test would pass over an empty row.</remarks>
internal sealed class HeadlessTheme : IDisposable
{
    private readonly Avalonia.Themes.Fluent.FluentTheme _theme = new();

    private readonly StyleInclude _pickers = new(new Uri("avares://mTiles.Controls/Themes/"))
    {
        Source = new Uri("avares://mTiles.Controls/Themes/Picker.axaml"),
    };

    private readonly ResourceInclude _tokens = new(new Uri("avares://mTiles/Styles/"))
    {
        Source = new Uri("avares://mTiles/Styles/AppTheme.axaml"),
    };

    public HeadlessTheme()
    {
        var app = Avalonia.Application.Current!;
        app.Styles.Add(_theme);
        app.Styles.Add(_pickers);
        app.Resources.MergedDictionaries.Add(_tokens);
    }

    public void Dispose()
    {
        var app = Avalonia.Application.Current!;
        app.Styles.Remove(_pickers);
        app.Styles.Remove(_theme);
        app.Resources.MergedDictionaries.Remove(_tokens);
    }

    /// <summary>Runs the dispatcher and lays the window out, a few times over, so bindings and templates
    /// realised by one pass are measured by the next.</summary>
    public static void Layout(Window window)
    {
        for (var pass = 0; pass < 3; pass++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }
    }

    /// <summary>Pumps the dispatcher while <paramref name="condition"/> holds, for at most ten seconds.
    /// </summary>
    public static void PumpWhile(Func<bool> condition)
    {
        var deadline = Environment.TickCount64 + 10_000;
        while (condition() && Environment.TickCount64 < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
    }
}
