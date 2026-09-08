using Avalonia.Controls;
using Avalonia.Headless;
using mTiles.Models;
using mTiles.ViewModels;
using mTiles.Views;
using Xunit;

namespace mTiles.Tests;

/// <summary>That pressing Add on a settings list actually puts a form on screen.</summary>
/// <remarks>
/// <para>The gap between <c>AiSettingsPageTests.Opening_a_form_shows_it</c> — which asserts the view
/// model's flags — and the screen. Every one of the four forms stopped opening and the whole suite
/// still passed: <c>SettingsEditDialog</c>'s hand-written <c>InitializeComponent</c> hid the generated
/// one, so its <c>x:Name</c> fields were never assigned and its constructor threw. It is built inside
/// an <c>async void</c> handler, so that arrived as a button that did nothing.</para>
/// <para>The page is attached but not made visible: laying it out needs the application's styles, and
/// the test app has none. Attachment is all <c>OverlayHost.For</c> needs, and the handler adds the
/// entry to the host before its first await — so what is asserted is the dialog reaching the host,
/// not the dispatcher having got round to drawing it.</para>
/// </remarks>
[Collection(ProviderSeamCollection.Name)]
public sealed class SettingsFormOpensTests
{
    [Theory]
    [InlineData("connection")]
    [InlineData("agent")]
    [InlineData("provider")]
    [InlineData("sign-in")]
    public void Adding_an_entry_opens_its_form(string what) => OnUiThread(() =>
    {
        using var settings = new TempSettings();
        using var appData = new TempAppData();
        var vm = new SettingsViewModel(settings.Service);

        var host = new OverlayHost();
        var view = new SettingsView { DataContext = vm, IsVisible = false };
        var window = new Window { Content = new Panel { Children = { view, host } }, Width = 900, Height = 700 };
        window.Show();

        switch (what)
        {
            case "connection": vm.AddManualConnectionCommand.Execute(null); break;
            case "agent": vm.AddAgentInstanceCommand.Execute(null); break;
            case "provider": vm.AddProviderInstanceCommand.Execute(null); break;
            case "sign-in": vm.AddSignInCommand.Execute(null); break;
        }

        Assert.True(vm.IsEditingAnything);
        Assert.Equal(1, host.Children.Count);

        window.Close();
    });

    private static void OnUiThread(Action body)
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(SettingsFormOpensTests).Assembly);
        session.Dispatch(() => { body(); return Task.FromResult(true); }, CancellationToken.None)
            .GetAwaiter().GetResult();
    }
}
