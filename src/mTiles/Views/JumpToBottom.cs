using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Material.Icons;
using Material.Icons.Avalonia;

namespace mTiles.Views;

/// <summary>
/// The way back to the end of a conversation, shown only while the reader is somewhere else.
/// </summary>
/// <remarks>
/// <para>Floats over the foot of the transcript rather than taking a row of its own: it exists only
/// while the reader has scrolled away, and a row that came and went with the scroll would move the very
/// text being read. Drawn by the <c>Button.jump-to-bottom</c> style in <c>Styles/Conversation.axaml</c>.
/// </para>
/// <para>"The end" is <see cref="TranscriptFollow"/>'s: the button shows exactly when the transcript has
/// stopped following, so the two cannot disagree about where the bottom is. Pressing it — or Ctrl+End
/// anywhere in the tile — is <see cref="TranscriptAnchor.GoToEnd"/>, which is what a send already does,
/// so the reader is put back on the end <em>and kept there</em> as the answer streams in.</para>
/// </remarks>
public sealed class JumpToBottom : Button
{
    private ScrollViewer? _scroll;
    private TranscriptAnchor? _anchor;

    protected override Type StyleKeyOverride => typeof(Button);

    public JumpToBottom()
    {
        Classes.Add("jump-to-bottom");
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Bottom;
        IsVisible = false;
        Focusable = false;
        Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = "Jump to bottom (Ctrl+End)", VerticalAlignment = VerticalAlignment.Center },
                new MaterialIcon { Kind = MaterialIconKind.ArrowDown, Width = 12, Height = 12 },
            },
        };
    }

    /// <summary>Follows <paramref name="scroll"/>, and answers Ctrl+End anywhere in <paramref name="tile"/>.
    /// </summary>
    public void Attach(ScrollViewer scroll, TranscriptAnchor anchor, InputElement tile)
    {
        _scroll = scroll;
        _anchor = anchor;
        scroll.ScrollChanged += (_, _) => Update();

        // Tunnelled, and never marked handled: in the composer Ctrl+End also puts the caret at the end
        // of the text, and both halves of that are what somebody pressing it wants.
        tile.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.End && e.KeyModifiers == KeyModifiers.Control && IsVisible) Jump();
        }, RoutingStrategies.Tunnel);
    }

    protected override void OnClick()
    {
        base.OnClick();
        Jump();
    }

    private void Jump()
    {
        _anchor?.GoToEnd();
        IsVisible = false;
    }

    private void Update()
    {
        if (_scroll is not { } s) return;
        IsVisible = TranscriptAnchor.CanBeMeasured(s.Viewport.Height, s.Extent.Height)
            && !TranscriptFollow.ShouldFollow(s.Extent.Height, s.Viewport.Height, s.Offset.Y);
    }
}
