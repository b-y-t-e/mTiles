using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Material.Icons;

namespace mTiles.Views;

/// <summary>What a question and a failure look like: one control, drawn in <see cref="OverlayHost"/>.</summary>
/// <remarks>
/// <para>This replaces <c>MessageBox.Avalonia</c>, which opened a window of its own for every one of
/// them. On a tiling window manager each of those is placed into the layout rather than centred over
/// the application — and these are the dialogs that appear most: every "are you sure", every push that
/// failed. The four wizards a user meets once; these they meet daily.</para>
/// <para>What is gained beyond the placement is that they look like the rest of the application — the
/// same card, the same accent button, the same fonts — and that the message can be selected and
/// copied, which is the first thing anybody wants to do with a git error.</para>
/// <para><b>The answer when there is nowhere to ask is the caller's</b> (<c>whenUnavailable</c>), and
/// deliberately not one value: discarding a transcript or deleting a downloaded model must default to
/// no, while confirming an ordinary action defaults to yes, which is what each call site had already
/// decided for itself. A parameter keeps that decision where it is made rather than burying it here.</para>
/// </remarks>
public partial class MessageDialog : UserControl, OverlayHost.IFocusOnOpen
{
    /// <summary>What the dialog is about, which decides its glyph and that glyph's colour.</summary>
    public enum Tone
    {
        Question,
        Info,
        Warning,
        Error,
    }

    private readonly Button _focusOnOpen;

    public MessageDialog()
    {
        InitializeComponent();
        _focusOnOpen = ConfirmButton;
    }

    private MessageDialog(string title, string message, Tone tone, string? confirmText, string cancelText)
        : this()
    {
        TitleText.Text = title;
        MessageText.Text = message;

        (MaterialIconKind kind, string brush) = tone switch
        {
            Tone.Error => (MaterialIconKind.AlertCircleOutline, "DangerText"),
            Tone.Warning => (MaterialIconKind.AlertOutline, "WarnText"),
            Tone.Info => (MaterialIconKind.InformationOutline, "AccentHover"),
            _ => (MaterialIconKind.HelpCircleOutline, "TextMuted"),
        };
        ToneIcon.Kind = kind;
        ToneIcon.Bind(ForegroundProperty, this.GetResourceObservable(brush).ToBinding());

        if (confirmText is null)
        {
            // A statement rather than a question: one button, and it takes the keyboard.
            CancelButton.IsVisible = false;
            ConfirmButton.Content = cancelText;
            ConfirmButton.Click += (_, _) => OverlayHost.CloseWith(this, false);
        }
        else
        {
            ConfirmButton.Content = confirmText;
            CancelButton.Content = cancelText;
            ConfirmButton.Click += (_, _) => OverlayHost.CloseWith(this, true);
            CancelButton.Click += (_, _) => OverlayHost.CloseWith(this, false);

            // The safe answer takes the keyboard, so Enter on a keystroke nobody aimed declines:
            // every one of these confirms something that pressing it again will not undo.
            _focusOnOpen = CancelButton;
        }

        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            OverlayHost.CloseWith(this, false);
        };
    }

    public void FocusOnOpen() => _focusOnOpen.Focus();

    /// <summary>Asks a yes/no question.</summary>
    /// <param name="whenUnavailable">The answer when there is no window to ask in — see the class
    /// remarks for why this is the caller's decision and not one value.</param>
    public static Task<bool> ConfirmAsync(Visual owner, string title, string message,
        bool whenUnavailable, Tone tone = Tone.Question,
        string confirmText = "Yes", string cancelText = "No")
    {
        if (OverlayHost.For(owner) is not { } host)
            return Task.FromResult(whenUnavailable);

        return host.ShowAsync<bool>(
            new MessageDialog(title, message, tone, confirmText, cancelText), width: 460);
    }

    /// <summary>States something and waits for it to be dismissed.</summary>
    public static async Task ShowAsync(Visual owner, string title, string message, Tone tone)
    {
        if (OverlayHost.For(owner) is not { } host)
            return;

        await host.ShowAsync<bool>(
            new MessageDialog(title, message, tone, confirmText: null, cancelText: "OK"), width: 460);
    }
}
