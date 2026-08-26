using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using mTiles.Services;
using Notepad.Avalonia.Controls;

namespace mTiles.Views;

/// <summary>
/// <see cref="MarkdownViewer"/> wearing this application's colours and the font the user chose.
/// </summary>
/// <remarks>
/// <para>A subclass rather than a page of <c>DynamicResource</c> attributes, because those did not work
/// and failed silently. <c>MarkdownViewer</c> calls its own <c>ApplyTheme</c> <b>in its constructor</b>,
/// with <c>ColorTheme</c> still at its default of <c>Light</c>, and that assigns every brush as a
/// <em>local value</em>: white ground, black text. Setting <c>ColorTheme="None"</c> in markup comes too
/// late to stop it, a <c>Style</c> cannot override it (styles lose to local values), and any brush the
/// markup forgets to name keeps the light one. Measured, not guessed: a probe read
/// <c>Foreground=Black</c> off a viewer built from the transcript's own template.</para>
/// <para>So the values are pushed, after attachment, from the same tokens the rest of the tile uses — the
/// idiom <c>ThemeBridge</c> already follows for the whole application. Re-applied whenever the resources
/// change, which is what happens when the user picks another terminal theme or font size.</para>
/// </remarks>
public sealed class GoalMarkdownView : MarkdownViewer
{
    public GoalMarkdownView()
    {
        // Shape, not colour: none of this depends on a theme, so none of it has to wait for one.
        ColorTheme = EditorTheme.None;
        ViewerPadding = new Thickness(0);
        ParagraphSpacing = 8;
        LineSpacing = 2;

        // The tools write single newlines and mean them — a plan is a list of lines, not one paragraph.
        SoftLineBreaks = true;

        // Never straight to a browser. This text is written by a model, from a prompt carrying a working
        // tree that may itself contain anything, and `[click here](http://…)` shows the words and hides
        // the address. The tile already puts a barrier in front of a *command* that arrives in a goal
        // file for exactly this reason; a link that opens on one click, with the destination visible
        // nowhere, is the same trust placed in the same author with none of the same care.
        OpenLinksInBrowser = false;
        LinkClicked += OnLinkClicked;

        ResourcesChanged += (_, _) => ApplyTokens();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ApplyTokens();
    }

    /// <summary>
    /// Every brush the control owns, and the fonts, from this application's tokens.
    /// </summary>
    /// <remarks>
    /// <b>Every</b> one, including the two scroll-bar brushes this control only uses when it has a
    /// height of its own. Leaving one out does not leave it neutral: it leaves the light-theme value the
    /// constructor already wrote there.
    /// </remarks>
    internal void ApplyTokens()
    {
        if (Brush("TextPrimary") is { } text) Foreground = text;
        if (Brush("TextMuted") is { } muted) MutedBrush = muted;
        if (Brush("AccentDefault") is { } accent)
        {
            LinkBrush = accent;
            SelectionBrush = new SolidColorBrush(ColorOf(accent), 0.35);
        }

        if (Brush("BgElevated") is { } elevated) CodeBackground = elevated;
        if (Brush("BorderSubtle") is { } subtle)
        {
            RuleBrush = subtle;
            ScrollTrackBrush = subtle;
        }

        if (Brush("BorderStrong") is { } strong)
        {
            QuoteBarBrush = strong;
            ScrollThumbBrush = strong;
        }

        // The tile is a transcript in the terminal's face; a proportional heading in the middle of it is
        // the one element that announces it came from somewhere else.
        if (Token("TerminalFontFamily") is FontFamily font)
        {
            DefaultFont = font;
            CodeFont = font;
        }

        if (Token("UiFontSize") is double points and > 0)
            DefaultFontSize = points;

        // Nothing behind it: the row already has whatever background its role calls for, and a viewer
        // painting its own would put a rectangle over the user's band.
        BackgroundBrush = Brushes.Transparent;
    }

    /// <summary>
    /// Asks before opening, and shows the address it would open.
    /// </summary>
    /// <remarks>
    /// The address, not the words on it: showing the link text back would be repeating what the user
    /// already read and clicked, which is the half a misleading link controls. Nothing is opened when
    /// there is no window to ask in — the same answer the settings dialog gives, and for the same
    /// reason: an unanswered question is not a yes.
    /// </remarks>
    private async void OnLinkClicked(object? sender, LinkClickedEventArgs e)
    {
        e.Handled = true;

        if (LinkToOpen(e.Url) is not { } opening) return;
        if (TopLevel.GetTopLevel(this) is not Window window) return;

        try
        {
            var box = MsBox.Avalonia.MessageBoxManager.GetMessageBoxStandard(
                "Open link",
                $"Open this address in your browser?\n\n{CommandDisplay.ForDialog(opening)}",
                MsBox.Avalonia.Enums.ButtonEnum.YesNo,
                MsBox.Avalonia.Enums.Icon.Question);

            if (await box.ShowWindowDialogAsync(window) != MsBox.Avalonia.Enums.ButtonResult.Yes) return;

            Process.Start(new ProcessStartInfo(opening) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            // Both halves are in here on purpose. This is an `async void` handler, so anything escaping
            // it reaches the dispatcher's unhandled hook and takes the application with it — and the
            // dialog is as capable of throwing as the launch: a window closing underneath it is enough.
            // Failing to open a link is not worth the process.
            Trace.TraceWarning($"Opening a link failed: {ex.Message}");
        }
    }

    /// <summary>
    /// The address a click would open, or null where it must not open at all.
    /// </summary>
    /// <remarks>
    /// <para>Pure, and separate from the dialog, because it is the barrier — the same reason
    /// <c>CommandDisplay</c> is its own class. What it decides can then be stated without a window.</para>
    /// <para><b>The address, normalised.</b> <c>Uri</c> rewrites on the way through — IDN to punycode,
    /// <c>..</c> out of the path, percent-escapes — so the raw markdown and the thing actually opened
    /// are two different strings, and the difference is the attack: a cyrillic "аpple.com" reads as
    /// apple.com and opens xn--pple-43d.com. A barrier that shows one address and follows another is
    /// not a barrier, so this returns what will be opened and the dialog shows exactly that.</para>
    /// <para><b>Two schemes.</b> Anything else — <c>file:</c>, and every scheme some other application
    /// on this machine has registered for itself — is not something to hand a shell on the strength of
    /// one click on text a model wrote.</para>
    /// <para><b>And a length.</b> The same refusal a verify command gets: a thing too long to be read
    /// cannot be consented to, and truncating it into the dialog moves the payload past the ellipsis
    /// rather than removing it.</para>
    /// </remarks>
    internal static string? LinkToOpen(string? url)
    {
        var raw = (url ?? "").Trim();
        if (raw.Length == 0) return null;

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var target)
            || (target.Scheme != Uri.UriSchemeHttp && target.Scheme != Uri.UriSchemeHttps))
            return null;

        // Punycode, deliberately, and this is the half the first version got wrong. `AbsoluteUri` does
        // *not* convert an international host: it hands back the cyrillic "аpple.com" it was given, so
        // showing it and opening it were consistent and both were the lookalike — while the browser
        // resolves the punycode underneath. Rebuilding through IdnHost makes the address shown the one
        // that will actually be looked up, which is the only version of this that is worth showing.
        var opening = target.IdnHost == target.Host
            ? target.AbsoluteUri
            : new UriBuilder(target) { Host = target.IdnHost }.Uri.AbsoluteUri;

        return CommandDisplay.CanBeConsentedTo(opening) ? opening : null;
    }

    private IBrush? Brush(string key) => Token(key) as IBrush;

    /// <summary>One token, or null where the application has not defined it — which is how a missing
    /// resource has to fail here: leaving the value alone rather than painting something invented.
    /// </summary>
    private object? Token(string key) =>
        this.TryFindResource(key, out var value) ? value : null;

    /// <summary>The selection wash is the accent at a third, so text stays readable through it. A solid
    /// accent behind selected text hides the words it is selecting.</summary>
    private static Color ColorOf(IBrush brush) =>
        brush is ISolidColorBrush solid ? solid.Color : Colors.SteelBlue;
}
