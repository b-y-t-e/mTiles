using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.VisualTree;
using mTiles.Models;
using mTiles.Services;
using mTiles.ViewModels;
using mTiles.Views;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// A finding is drawn no wider than the list it is in.
/// </summary>
/// <remarks>
/// <para>The failure this pins is invisible in the markup and in a code review: padding on the
/// dialog's <c>ScrollViewer</c> is taken off the width the content is <em>measured</em> against and
/// left on the width it is <em>arranged</em> against, so every row came out wider than the list, was
/// clipped by its own rounded border, and every line lost its last few characters — "nie mają n",
/// "implementacj". Nothing errors, nothing warns, and the XAML reads correctly.</para>
/// <para>It is a layout test, so it needs a rendered tree: the test application carries no theme, and
/// without one an <c>ItemsControl</c> has no template, generates no containers, and every width in
/// sight is zero — which an assertion about widths passes without noticing. The first version of this
/// test did exactly that and passed against the broken markup.</para>
/// </remarks>
public class GoalFindingsDialogTests
{
    private static void OnUiThread(Action body)
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(GoalFindingsDialogTests).Assembly);
        session.Dispatch(() => { body(); return Task.FromResult(true); }, CancellationToken.None)
            .GetAwaiter().GetResult();
    }

    /// <summary>
    /// Puts a theme on the application for the duration of one test, and takes it off again.
    /// </summary>
    /// <remarks>
    /// <para>On <see cref="Application"/> and not on the window: control themes are resolved from the
    /// application's styles, and a <c>FluentTheme</c> added to a window leaves an <c>ItemsControl</c>
    /// untemplated — measured, arranged, and empty.</para>
    /// <para>Taken off again because the test application is shared by every test in this assembly, and
    /// leaving a theme on it changed what two dozen other UI tests were looking at — they assert on the
    /// brushes and templates of an untemplated tree, and a Fluent theme underneath them is a different
    /// tree. Safe to mutate only because this assembly runs its tests one at a time
    /// (<c>CollectionBehavior(DisableTestParallelization = true)</c>); without that this would be a
    /// race, not a fixture.</para>
    /// </remarks>
    private static IDisposable AppTheme()
    {
        var app = Application.Current ?? throw new InvalidOperationException("no application");
        var theme = new FluentTheme();
        var tokens = new ResourceInclude(new Uri("avares://mTiles/Styles/"))
        {
            Source = new Uri("avares://mTiles/Styles/AppTheme.axaml"),
        };

        app.Styles.Add(theme);
        app.Resources.MergedDictionaries.Add(tokens);

        return new Undo(() =>
        {
            app.Styles.Remove(theme);
            app.Resources.MergedDictionaries.Remove(tokens);
        });
    }

    private sealed class Undo(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }

    /// <summary>A finding whose every part is longer than the dialog is wide.</summary>
    private static GoalFinding LongFinding() => new()
    {
        Severity = GoalSeverity.Warning,
        File = "src/mTiles/ViewModels/MainWindowViewModel.cs",
        Line = 225,
        Category = "lifetime",
        Title = "Unrelated changes to the Goal tile bolted on to this one",
        Detail = string.Join(" ", Enumerable.Repeat(
            "The flyout of findings under the badges and the extraction of the template are not the "
            + "goal of this change, which is the workspaces panel.", 4)),
    };

    private static (GoalTileView View, GoalTileViewModel Vm) Build(string dir)
    {
        var vm = new GoalTileViewModel(dir, new SettingsService(Path.Combine(dir, "settings.json")));
        vm.OpenFindingsCommand.Execute(new GoalBadge
        {
            Severity = GoalSeverity.Warning,
            Count = 1,
            Findings = [LongFinding()],
        });

        var view = new GoalTileView { DataContext = vm };
        var window = new Window { Content = view, Width = 620, Height = 460 };
        window.Show();

        // Twice: the first pass gives the card its width, the second lays the findings out against it.
        window.UpdateLayout();
        window.UpdateLayout();

        return (view, vm);
    }

    [Fact]
    public void A_finding_is_never_wider_than_the_list_it_is_in()
    {
        var dir = Directory.CreateTempSubdirectory("mtiles-findings").FullName;
        try
        {
            OnUiThread(() =>
            {
                using var theme = AppTheme();
                var (view, vm) = Build(dir);

                var card = view.GetVisualDescendants().OfType<Border>()
                    .Single(b => b.Classes.Contains("findings-card"));
                var list = card.GetVisualDescendants().OfType<ItemsControl>().Single();
                var row = card.GetVisualDescendants().OfType<Border>()
                    .Single(b => b.Classes.Contains("finding"));

                // The tree is really there. Without this the rest passes on an empty dialog.
                Assert.True(card.Bounds.Width > 0, "the dialog was never laid out");
                Assert.True(row.Bounds.Height > 0, "the finding was never rendered");

                Assert.True(row.Bounds.Width <= list.Bounds.Width,
                    $"the finding is {row.Bounds.Width:0} wide in a list {list.Bounds.Width:0} wide, "
                    + "so its last characters are clipped by its own border");

                vm.Dispose();
            });
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* a test's temp dir */ }
        }
    }
}
