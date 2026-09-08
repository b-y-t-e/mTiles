using Avalonia.Controls;
using mTiles.Views;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The application has one window, and everything else is drawn inside it.
/// </summary>
/// <remarks>
/// <para>Two rules meet here and a second window breaks both. <see cref="InterfaceScaleWiringTests"/>
/// asserts that the whole tree hangs under one <c>LayoutTransformControl</c> in
/// <see cref="MainWindow"/> — a window opened beside it is outside that transform, so it is drawn at
/// the compositor's scale while everything the user can see is drawn at theirs. And a window of its
/// own is placed by the window manager rather than centred over the application, which on a tiling
/// compositor means a confirmation taking half the screen next to the tile it is asking about; that
/// is what <c>MessageBox.Avalonia</c> did and why <see cref="MessageDialog"/> replaced it.</para>
/// <para>Neither failure has a symptom on the machine it is written on: a modal dialog on a stacking
/// desktop at scale 1.0 looks exactly right. So this is the guard, in the same spirit as
/// <see cref="FontScaleTests"/> — a new view is written by copying an old one, and every dialog
/// framework's own examples open a window.</para>
/// </remarks>
public class SingleWindowTests
{
    /// <summary>Nothing in the application is a <see cref="Window"/> but <see cref="MainWindow"/>.</summary>
    /// <remarks>
    /// Reflection rather than a source scan, because this is the half that has to hold whatever the
    /// window was spelled in: a C# class, an AXAML file rooted at <c>&lt;Window&gt;</c>, or one of
    /// Avalonia's own window types subclassed for a wizard. All three arrive here as a type.
    /// </remarks>
    [Fact]
    public void Only_the_main_window_is_a_window()
    {
        var windows = typeof(MainWindow).Assembly
            .GetTypes()
            .Where(t => typeof(Window).IsAssignableFrom(t))
            .Select(t => t.FullName ?? t.Name)
            .Where(n => n != typeof(MainWindow).FullName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.True(windows.Count == 0,
            "A dialog is a UserControl drawn in OverlayHost, so that it is inside the interface scale " +
            "and centred over the application rather than placed by the window manager. These are " +
            "windows of their own:" + Environment.NewLine + string.Join(Environment.NewLine, windows));
    }

    /// <summary>And nothing opens one without declaring a type for it.</summary>
    /// <remarks>
    /// <para>The gap the test above cannot see: <c>new Window { Content = … }.ShowDialog(owner)</c>
    /// declares no type of ours, and neither does a package that opens one for you — which is exactly
    /// the shape <c>MessageBox.Avalonia</c> had at every call site before it was taken out.</para>
    /// <para>Documentation is skipped rather than the words being made narrower: <c>MessageDialog</c>
    /// names the package it replaced in its own <c>&lt;remarks&gt;</c>, and a rule that could not be
    /// explained in the file it governs would be a worse rule.</para>
    /// </remarks>
    [Fact]
    public void Nothing_opens_a_window_of_its_own()
    {
        var offenders = new List<string>();

        foreach (var file in SourceFiles())
        {
            var line = 0;

            foreach (var text in File.ReadLines(file))
            {
                line++;

                var trimmed = text.TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal)) continue;

                if (Opener.Match(text) is { Success: true } m)
                    offenders.Add($"{Path.GetFileName(file)}:{line} {m.Value.Trim()}");
            }
        }

        Assert.True(offenders.Count == 0,
            "A dialog is shown through OverlayHost.ShowAsync, which draws it inside the one window:" +
            Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>Constructing a bare window, showing one modally, or reaching for the old package.</summary>
    /// <remarks>
    /// <c>new\s+Window\s*[({]</c> rather than the bare words, so <c>new WindowsFirewallGuide()</c> —
    /// a real line in <c>PhoneFirewall</c> — is not read as a window.
    /// </remarks>
    private static readonly Regex Opener =
        new(@"new\s+Window\s*[({]|\.ShowDialog\b|\bMessageBox\b|\bMessageBoxManager\b");

    /// <inheritdoc cref="XmlDocPlacementTests"/>
    private static IEnumerable<string> SourceFiles() =>
        Directory.EnumerateFiles(Source(), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    /// <inheritdoc cref="XmlDocPlacementTests"/>
    private static string Source([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(thisFile)!, "..", "..", "src", "mTiles"));
}
