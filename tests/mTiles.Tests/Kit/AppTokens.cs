using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;

namespace mTiles.Tests;

/// <summary>The application's <c>AppTheme</c> tokens on one window, for a view that looks colours and sizes
/// up by role and needs no control themes (for those, <c>AgentSessions/HeadlessTheme</c>).</summary>
internal static class AppTokens
{
    public static Window WithAppTokens(this Window window)
    {
        window.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://mTiles/Styles/"))
        {
            Source = new Uri("avares://mTiles/Styles/AppTheme.axaml"),
        });
        return window;
    }
}
