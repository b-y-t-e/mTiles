using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using mTiles.ViewModels.AgentConversation;

namespace mTiles.Views;

/// <summary>
/// The status at the end of a conversation tile's strip: a rule, then a word coloured by its
/// <see cref="Tone"/>, and compact (<see cref="RowFitter"/>) a dot of that colour.
/// </summary>
/// <remarks>One control for the Agent and the Goal tile, because what it exists for is that the two
/// answer "can I type here" in one language — as two copies of the markup, a tone added to one would
/// silently be missing from the other. The colours are the <c>StackPanel.strip-status</c> styles in
/// <c>Conversation.axaml</c>, which is why this answers to <see cref="StackPanel"/>'s style key.
/// </remarks>
public sealed class StripStatus : StackPanel
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<StripStatus, string?>(nameof(Text));

    public static readonly StyledProperty<AgentStatusTone> ToneProperty =
        AvaloniaProperty.Register<StripStatus, AgentStatusTone>(nameof(Tone));

    private readonly TextBlock _label = new()
    {
        Classes = { "strip" },
        TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
    };

    public StripStatus()
    {
        Orientation = Avalonia.Layout.Orientation.Horizontal;
        Background = Avalonia.Media.Brushes.Transparent;
        Classes.Add("strip-status");
        Children.Add(new Border { Classes = { "strip-divider" } });
        Children.Add(_label);
        Children.Add(new Ellipse { Classes = { "status-dot" } });
        ApplyTone(Tone);
    }

    /// <summary>The word, drawn in full, trimmed to a cap (<see cref="RetreatStep.Trim"/>), and hidden
    /// when compact.</summary>
    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>What colours the word and the dot.</summary>
    public AgentStatusTone Tone
    {
        get => GetValue(ToneProperty);
        set => SetValue(ToneProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(StackPanel);

    /// <summary>Gives the word whatever width the cap leaves after the rule and the dot.</summary>
    /// <remarks>A horizontal <see cref="StackPanel"/> measures its children with unbounded width, so
    /// its own <c>MaxWidth</c> would clip the word mid-letter rather than trim it. The word is measured
    /// again at the width that fits, which is the size the panel then arranges it at.</remarks>
    protected override Size MeasureOverride(Size availableSize)
    {
        var desired = base.MeasureOverride(availableSize);
        var overflow = desired.Width - availableSize.Width;
        if (overflow <= 0 || !_label.IsVisible) return desired;

        _label.Measure(new Size(Math.Max(0, _label.DesiredSize.Width - overflow), availableSize.Height));
        return desired.WithWidth(availableSize.Width);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty)
            _label.Text = Text;
        else if (change.Property == ToneProperty)
            ApplyTone(Tone);
    }

    private void ApplyTone(AgentStatusTone tone)
    {
        Classes.Set("status-ready", tone == AgentStatusTone.Ready);
        Classes.Set("status-working", tone == AgentStatusTone.Working);
        Classes.Set("status-waiting", tone == AgentStatusTone.Waiting);
        Classes.Set("status-failed", tone == AgentStatusTone.Failed);
    }
}
