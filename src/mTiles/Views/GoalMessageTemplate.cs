using Avalonia.Controls;
using Avalonia.Controls.Templates;
using mTiles.Models;

namespace mTiles.Views;

/// <summary>
/// Picks which control draws a message, and builds only that one.
/// </summary>
/// <remarks>
/// <para>The transcript used to hold both — a markdown view and a text block — in a <c>Panel</c>, with
/// <c>IsVisible</c> deciding. That draws the right one, and constructs both: a hidden control is still
/// a constructed control, and <c>MarkdownViewer</c>'s constructor is not free — it applies a theme,
/// allocates a set of brushes and subscribes to resource changes. The transcript's <c>ItemsControl</c>
/// does not virtualise, so a conversation of two hundred messages carried two hundred of them, every
/// one of which was there to be invisible.</para>
/// <para>A template rather than virtualisation, because it fixes the thing that is actually wrong. The
/// row cost one control before this feature and costs one control now; virtualising as well is a
/// separate question, and one with its own way of going wrong in a list that scrolls to the end on
/// every message.</para>
/// </remarks>
public sealed class GoalMessageTemplate : IDataTemplate
{
    /// <summary>Drawn for the tool's own words.</summary>
    public IDataTemplate? Markdown { get; set; }

    /// <summary>Drawn for everything else: what the user typed, a note from the tile, and anything this
    /// application composed — whose columns markdown would re-flow.</summary>
    public IDataTemplate? Plain { get; set; }

    public bool Match(object? data) => data is GoalMessage;

    public Control? Build(object? param) =>
        param is not GoalMessage message
            ? null
            : (message.IsMarkdown ? Markdown : Plain)?.Build(param);
}
