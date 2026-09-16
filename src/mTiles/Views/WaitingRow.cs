using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;

namespace mTiles.Views;

/// <summary>
/// The row a conversation shows while it waits for the agent: the thinking dots where the next message will
/// be, and beside them what is being waited on and for how long. One control for every conversation (the
/// Goal tile's run, the Agent tile's turn), drawn by the shared styles in <c>Styles/Conversation.axaml</c>.
/// </summary>
/// <remarks>
/// <para>A row in the conversation, in the shape of the message it stands in for: the gutter marker where
/// the marker goes, the dots where the text will be. In the flow it collides with nothing, lands where the
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

    private readonly TextBlock _stage = Note();
    private readonly TextBlock _separator = Note("·");
    private readonly TextBlock _elapsed = Note();

    public WaitingRow()
    {
        Classes.Add("row");

        var dots = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        foreach (var dot in new[] { "d1", "d2", "d3" })
            dots.Children.Add(new Ellipse { Classes = { "think", dot } });
        _stage.Margin = new Thickness(7, 0, 0, 0);
        dots.Children.Add(_stage);
        dots.Children.Add(_separator);
        dots.Children.Add(_elapsed);

        var gutter = new TextBlock { Classes = { "gutter", "gutter-system" } };
        DockPanel.SetDock(gutter, Dock.Left);
        Child = new DockPanel { Children = { gutter, dots } };
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
        _elapsed.Margin = new Thickness(hasStage ? 0 : 7, 0, 0, 0);
    }

    private static TextBlock Note(string? text = null) => new() { Classes = { "waiting-note" }, Text = text };
}
