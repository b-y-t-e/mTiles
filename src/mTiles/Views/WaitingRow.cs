using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace mTiles.Views;

/// <summary>
/// The row a conversation shows while it waits for the agent: a spinner in the gutter where the next message's
/// glyph will be, and beside them what is being waited on and for how long. One control for every conversation (the
/// Goal tile's run, the Agent tile's turn), drawn by the shared styles in <c>Styles/Conversation.axaml</c>.
/// </summary>
/// <remarks>
/// <para>A row in the conversation, in the shape of the message it stands in for. <b>The gutter is what
/// moves</b>: the slot where the speaker's glyph stands turns through the braille spinner the agents'
/// own TUIs use in their titles, in the assistant glyph's colour — the speaker's mark in the act of
/// forming, rather than three dots beside it. It is the only motion on the row; the stage and the clock
/// beside it are still text, so the eye has one thing to find and nothing to chase. It replaced three
/// pulsing dots, which said "busy" in a chat-app dialect and pushed the stage a column to the right of
/// every message's text. In the flow it collides with nothing, lands where the
/// next message will, and the follow-to-the-bottom rule carries it for free.</para>
/// <para>The stage and the clock are the only honest things left to say about an agent nothing has been
/// heard from — nothing here knows how far along it is, so there is no progress bar. Either may be empty
/// and then takes no room, the separator with it.</para>
/// </remarks>
public sealed class WaitingRow : Border
{
    public static readonly StyledProperty<string?> StageProperty =
        AvaloniaProperty.Register<WaitingRow, string?>(nameof(Stage));

    public static readonly StyledProperty<string?> ElapsedProperty =
        AvaloniaProperty.Register<WaitingRow, string?>(nameof(Elapsed));

    /// <summary>One turn of the spinner. Ten cells, each a single braille character, so the glyph never
    /// changes width and nothing beside it moves.</summary>
    private static readonly string[] Frames = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];

    private readonly TextBlock _spinner = new() { Classes = { "gutter", "gutter-working" }, Text = Frames[0] };
    private readonly DispatcherTimer _turn = new() { Interval = FrameInterval };
    private int _frame;
    private readonly TextBlock _stage = Note();
    private readonly TextBlock _separator = Note("·");
    private readonly TextBlock _elapsed = Note();

    public WaitingRow()
    {
        Classes.Add("row");

        var note = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        note.Children.Add(_stage);
        note.Children.Add(_separator);
        note.Children.Add(_elapsed);

        DockPanel.SetDock(_spinner, Dock.Left);
        Child = new DockPanel { Children = { _spinner, note } };

        // Turning only while it can be seen: a hidden row in every idle tile - or in a workspace switched
        // away from, whose view stays attached and merely hidden - must not wake the UI thread eleven
        // times a second. Avalonia raises nothing public when an ancestor hides, so a hidden row drops
        // to one look a second and turns again the moment it can be seen.
        _turn.Tick += (_, _) => Turn();
        Refresh();
    }

    /// <summary>What is being waited on — a phase, a tool — or empty.</summary>
    public string? Stage
    {
        get => GetValue(StageProperty);
        set => SetValue(StageProperty, value);
    }

    /// <summary>How long it has been, as <see cref="mTiles.ViewModels.ElapsedClock"/> writes it, or empty.</summary>
    public string? Elapsed
    {
        get => GetValue(ElapsedProperty);
        set => SetValue(ElapsedProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == StageProperty || change.Property == ElapsedProperty) Refresh();
        if (change.Property == IsVisibleProperty) Spin();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Spin();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _turn.Stop();
    }

    private void Spin()
    {
        if (IsVisible && this.IsAttachedToVisualTree()) _turn.Start();
        else _turn.Stop();
    }

    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(90);
    private static readonly TimeSpan HiddenCheckInterval = TimeSpan.FromSeconds(1);

    private void Turn()
    {
        var seen = IsEffectivelyVisible;
        _turn.Interval = seen ? FrameInterval : HiddenCheckInterval;
        if (seen) _spinner.Text = Frames[_frame = (_frame + 1) % Frames.Length];
    }

    private void Refresh()
    {
        _stage.Text = Stage;
        _elapsed.Text = Elapsed;
        var hasStage = !string.IsNullOrEmpty(Stage);
        var hasElapsed = !string.IsNullOrEmpty(Elapsed);
        _stage.IsVisible = hasStage;
        _elapsed.IsVisible = hasElapsed;
        _separator.IsVisible = hasStage && hasElapsed;
    }

    private static TextBlock Note(string? text = null) => new() { Classes = { "waiting-note" }, Text = text };
}
