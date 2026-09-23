using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using System.Diagnostics;
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

    /// <summary>The answer to a question with two ways forward (<see cref="ChooseAsync"/>).</summary>
    /// <remarks><see cref="Cancel"/> is zero on purpose: Escape, the X and a dialog taken down with its window
    /// all answer null, and null read as this enum is its default.</remarks>
    public enum Choice
    {
        Cancel = 0,
        Confirm,
        Alternate,
    }

    /// <summary>How long after it opens a dialog refuses a bare answer key, unless told otherwise.</summary>
    /// <remarks>
    /// <para>These dialogs appear <em>under</em> somebody's typing — a discard asked for from the git
    /// tile, with a terminal and a composer a keystroke away — so a letter already on its way to the
    /// keyboard lands in a dialog the user has not read yet. Long enough that such a key is gone, short
    /// enough that a deliberate press never waits for it: Firefox guards its download buttons the same
    /// way and for the same reason.</para>
    /// <para>Only the <b>bare</b> letter is held back. Enter, Escape and Alt+D are gestures aimed at a
    /// dialog — nobody makes them by accident — and they work from the first frame.</para>
    /// </remarks>
    public static TimeSpan DefaultSettlingTime { get; } = TimeSpan.FromMilliseconds(300);

    /// <summary>This dialog's own settling window, taken from <see cref="DefaultSettlingTime"/>.</summary>
    /// <remarks>Per instance rather than a static a test moves: test classes run in parallel, so a
    /// shared window is one class widening it while another's deliberate keypress is refused — a flake
    /// with no trace of its cause in the test that fails.</remarks>
    internal TimeSpan SettlingTime { get; set; } = DefaultSettlingTime;

    private readonly Button _focusOnOpen;
    private readonly Stopwatch _sinceOpened = new();
    private char? _confirmKey;
    private char? _cancelKey;
    private char? _alternateKey;

    public MessageDialog()
    {
        InitializeComponent();
        _focusOnOpen = ConfirmButton;

        // Bubble: a control that means something else by a letter has already handled it. Nothing in
        // this dialog is typed into today, and that is a fact about its markup rather than a promise.
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Bubble);


    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _sinceOpened.Restart();
    }

    /// <summary>The bare letter that answers: <c>y</c>, <c>n</c>, <c>d</c> for Discard.</summary>
    /// <remarks>The letter is the one underlined on the button, asked of the same rule that put the
    /// mark there — so what the dialog shows and what it accepts cannot drift apart.</remarks>
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled) return;
        if (_sinceOpened.Elapsed < SettlingTime) return;

        Button? pressed = null;
        if (AccessKeyLabel.Answers(e.Key, e.KeyModifiers, _confirmKey))
            pressed = ConfirmButton;
        else if (CancelButton.IsVisible && AccessKeyLabel.Answers(e.Key, e.KeyModifiers, _cancelKey))
            pressed = CancelButton;
        else if (AlternateButton.IsVisible && AccessKeyLabel.Answers(e.Key, e.KeyModifiers, _alternateKey))
            pressed = AlternateButton;

        if (pressed is null) return;

        e.Handled = true;
        pressed.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private MessageDialog(string title, string message, Tone tone, string? confirmText, string cancelText,
        bool defaultsToYes)
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
            ConfirmButton.Content = Label(cancelText, taken: null);
            _confirmKey = AccessKeyLabel.KeyOf(cancelText);
            ConfirmButton.Click += (_, _) => OverlayHost.CloseWith(this, false);
        }
        else
        {
            _confirmKey = AccessKeyLabel.KeyOf(confirmText);
            _cancelKey = AccessKeyLabel.KeyOf(cancelText, taken: _confirmKey);
            ConfirmButton.Content = Label(confirmText, taken: null);
            CancelButton.Content = Label(cancelText, taken: _confirmKey);
            ConfirmButton.Click += ConfirmWithTrue;
            CancelButton.Click += CancelWithFalse;

            // The safe answer takes the keyboard, so Enter on a keystroke nobody aimed declines:
            // nearly every one of these confirms something that pressing it again will not undo.
            // `defaultsToYes` is for the ones that do not — see the parameter's own remarks.
            _focusOnOpen = defaultsToYes ? ConfirmButton : CancelButton;
        }
    }

    /// <summary>A question with two ways forward and a way out: the three buttons answer with
    /// <see cref="Choice"/> rather than a bool.</summary>
    /// <remarks>The keyboard goes to Cancel, for the reason it does in the two-answer dialog: both ways
    /// forward change something, and a stray Enter must not pick either.</remarks>
    private MessageDialog(string title, string message, Tone tone, string confirmText, string alternateText,
        string cancelText)
        : this(title, message, tone, confirmText, cancelText, defaultsToYes: false)
    {
        _alternateKey = AccessKeyLabel.KeyOf(alternateText, _confirmKey, _cancelKey);
        AlternateButton.Content = new AccessText
        {
            Text = AccessKeyLabel.Mark(alternateText, _confirmKey, _cancelKey),
            ShowAccessKey = true,
        };
        AlternateButton.IsVisible = true;

        // The two-answer constructor wired these to a bool; this dialog answers with a Choice instead.
        ConfirmButton.Click -= ConfirmWithTrue;
        CancelButton.Click -= CancelWithFalse;
        ConfirmButton.Click += (_, _) => OverlayHost.CloseWith(this, Choice.Confirm);
        CancelButton.Click += (_, _) => OverlayHost.CloseWith(this, Choice.Cancel);
        AlternateButton.Click += (_, _) => OverlayHost.CloseWith(this, Choice.Alternate);
    }

    private void ConfirmWithTrue(object? sender, RoutedEventArgs e) => OverlayHost.CloseWith(this, true);

    private void CancelWithFalse(object? sender, RoutedEventArgs e) => OverlayHost.CloseWith(this, false);

    /// <summary>A button's label with its access key underlined.</summary>
    /// <remarks>
    /// <para><b>An <see cref="AccessText"/> rather than a string</b>, which is the part that was
    /// measured rather than assumed: a <see cref="ContentPresenter"/> turns a string into an
    /// <see cref="AccessText"/> only when its template asks for it, and the Button theme this
    /// application uses (Avalonia 12's Fluent) does not — so <c>"_Yes"</c> handed over as a string
    /// reached the screen as a plain TextBlock reading <c>_Yes</c>, underscore and all, with no
    /// access key registered anywhere. Built here, the control parses the mark itself.</para>
    /// <para><b>The underline is on from the start</b>, not only while Alt is held, which is what
    /// Avalonia's own <c>AccessKeyHandler</c> does with it. Alt-to-reveal is right where Alt is the
    /// whole gesture; here the bare letter answers too, so a mark nobody sees is a shortcut nobody
    /// knows about.</para>
    /// </remarks>
    private static AccessText Label(string text, char? taken) => new()
    {
        Text = AccessKeyLabel.Mark(text, taken),
        ShowAccessKey = true,
    };

    public void FocusOnOpen() => _focusOnOpen.Focus();

    /// <summary>Asks a yes/no question.</summary>
    /// <param name="whenUnavailable">The answer when there is no window to ask in — see the class
    /// remarks for why this is the caller's decision and not one value.</param>
    /// <param name="defaultsToYes">Which button takes the keyboard, and therefore what Enter answers.
    /// <b>Off by default and opt-in per call</b>: the reason the safe answer normally has it is that
    /// nearly every question here confirms something pressing it again will not undo, and a stray Enter
    /// must not be the thing that does it. Turn it on only where <em>no</em> is the cautious answer to
    /// nothing — where saying yes loses nothing the user could want back — so that the question is a
    /// pause rather than an obstacle. Compacting a conversation's context is the one such call today.
    /// </param>
    public static Task<bool> ConfirmAsync(Visual owner, string title, string message,
        bool whenUnavailable, Tone tone = Tone.Question,
        string confirmText = "Yes", string cancelText = "No", bool defaultsToYes = false)
    {
        if (OverlayHost.For(owner) is not { } host)
            return Task.FromResult(whenUnavailable);

        return host.ShowAsync<bool>(
            new MessageDialog(title, message, tone, confirmText, cancelText, defaultsToYes), width: 460);
    }

    /// <summary>Asks a question with two ways forward — <paramref name="confirmText"/> (the accent) and
    /// <paramref name="alternateText"/> — and a way out.</summary>
    /// <param name="whenUnavailable">The answer when there is no window to ask in.</param>
    public static async Task<Choice> ChooseAsync(Visual owner, string title, string message,
        string confirmText, string alternateText, string cancelText = "Cancel",
        Choice whenUnavailable = Choice.Cancel, Tone tone = Tone.Question)
    {
        if (OverlayHost.For(owner) is not { } host)
            return whenUnavailable;

        return await host.ShowAsync<Choice>(
            new MessageDialog(title, message, tone, confirmText, alternateText, cancelText), width: 520);
    }

    /// <summary>The one question a switch of agent or login asks, in both agent tiles: carry the context over,
    /// switch without it, or stay. Nowhere to ask answers <see cref="mTiles.ViewModels.HandoverAnswer.Cancel"/>.</summary>
    public static async Task<mTiles.ViewModels.HandoverAnswer> ChooseHandoverAsync(Visual owner, string message) =>
        await ChooseAsync(owner, "Switch agent", message,
                confirmText: "Carry context over", alternateText: "Without context") switch
            {
                Choice.Confirm => mTiles.ViewModels.HandoverAnswer.WithContext,
                Choice.Alternate => mTiles.ViewModels.HandoverAnswer.WithoutContext,
                _ => mTiles.ViewModels.HandoverAnswer.Cancel,
            };

    /// <summary>States something and waits for it to be dismissed.</summary>
    public static async Task ShowAsync(Visual owner, string title, string message, Tone tone)
    {
        if (OverlayHost.For(owner) is not { } host)
            return;

        await host.ShowAsync<bool>(
            new MessageDialog(title, message, tone, confirmText: null, cancelText: "OK", defaultsToYes: false),
            width: 460);
    }
}
