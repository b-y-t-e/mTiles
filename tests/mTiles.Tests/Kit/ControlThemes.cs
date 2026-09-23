namespace mTiles.Tests;

/// <summary>Fluent's control themes on the test application, added once and left in place.</summary>
/// <remarks>The test application carries no styles, so without them an <c>ItemsControl</c> has no
/// template and no containers, and a test measures the harness rather than the view. For a view that
/// also needs the application's tokens and the pickers, use <c>AgentSessions/HeadlessTheme</c>, which adds
/// and removes all three per test — at a cost this cheaper arrangement avoids.</remarks>
internal static class ControlThemes
{
    public static void EnsureFluent()
    {
        var app = Avalonia.Application.Current;
        if (app == null || app.Styles.Any(s => s is Avalonia.Themes.Fluent.FluentTheme)) return;
        app.Styles.Insert(0, new Avalonia.Themes.Fluent.FluentTheme());
    }
}
