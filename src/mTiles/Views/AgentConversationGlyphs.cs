using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Material.Icons;
using mTiles.AgentSessions.Conversation;
using mTiles.AgentSessions.Events;

namespace mTiles.Views;

/// <summary>
/// Which picture and which colour stand for a state of an agent conversation — facts about the drawing,
/// so they live with the views, the way <see cref="TileIcons"/> does.
/// </summary>
public static class AgentConversationGlyphs
{
    /// <summary>The gutter glyph: an angle bracket for you, a dot for the agent — the Goal tile's own.</summary>
    public static readonly FuncValueConverter<bool, string> Speaker = new(isUser => isUser ? "❯" : "●");

    public static readonly FuncValueConverter<bool, MaterialIconKind> Chevron =
        new(expanded => expanded ? MaterialIconKind.ChevronDown : MaterialIconKind.ChevronRight);

    public static readonly FuncValueConverter<ToolKind, MaterialIconKind> Tool = new(kind => kind switch
    {
        ToolKind.Command => MaterialIconKind.Console,
        ToolKind.FileRead => MaterialIconKind.FileEyeOutline,
        ToolKind.FileChange => MaterialIconKind.FileEditOutline,
        ToolKind.Search => MaterialIconKind.Magnify,
        ToolKind.WebFetch => MaterialIconKind.Web,
        ToolKind.Mcp => MaterialIconKind.Connection,
        ToolKind.SubAgent => MaterialIconKind.RobotOutline,
        _ => MaterialIconKind.Wrench,
    });


    public static readonly FuncValueConverter<bool, MaterialIconKind> Decision =
        new(allowed => allowed ? MaterialIconKind.ShieldCheckOutline : MaterialIconKind.ShieldOffOutline);

    public static readonly FuncValueConverter<PlanStepStatus, MaterialIconKind> PlanStep = new(status => status switch
    {
        PlanStepStatus.Completed => MaterialIconKind.CheckboxMarkedOutline,
        PlanStepStatus.InProgress => MaterialIconKind.ProgressClock,
        _ => MaterialIconKind.CheckboxBlankOutline,
    });

    public static readonly FuncValueConverter<NoticeLevel, IBrush?> NoticeBrush = new(level => Brush(level switch
    {
        NoticeLevel.Error => "DangerText",
        NoticeLevel.Warning => "WarnText",
        _ => "TextMuted",
    }));

    /// <summary>An attached image decoded at thumbnail width, once per image.</summary>
    /// <remarks>Cached against the attachment itself, which never changes after it is made: the timeline
    /// re-realises a message's template whenever it scrolls back into a rebuilt list, and decoding a
    /// screenshot each time would be most of the cost of drawing the conversation.</remarks>
    public static readonly FuncValueConverter<ImageAttachment?, Bitmap?> Thumbnail = new(image =>
        image is null ? null : Thumbnails.GetValue(image, Decode));

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ImageAttachment, Bitmap?> Thumbnails = new();

    private static Bitmap? Decode(ImageAttachment image)
    {
        try
        {
            using var stream = new MemoryStream(Convert.FromBase64String(image.Base64Data));
            return Bitmap.DecodeToWidth(stream, 320);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException
                                       or NotSupportedException or IOException)
        {
            return null;
        }
    }

    /// <summary>A Goal tile's pasted image — kept as a file — decoded at thumbnail width.</summary>
    /// <remarks>Cached against the attachment, the way <see cref="Thumbnail"/> is, so the bitmap goes when the
    /// goal lets go of its image rather than living as long as the process. A file that could not be read is
    /// not remembered: a paste held open for a moment by an antivirus gets its thumbnail on the next redraw.</remarks>
    public static readonly FuncValueConverter<mTiles.Models.GoalImageAttachment?, Bitmap?> ThumbnailOfFile = new(image =>
        image is null ? null : FileThumbnailOf(image));

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<mTiles.Models.GoalImageAttachment, Bitmap> FileThumbnails = new();

    private static Bitmap? FileThumbnailOf(mTiles.Models.GoalImageAttachment image)
    {
        if (FileThumbnails.TryGetValue(image, out var cached)) return cached;
        if (DecodeFile(image.Path) is not { } decoded) return null;
        FileThumbnails.AddOrUpdate(image, decoded);
        return decoded;
    }

    private static Bitmap? DecodeFile(string path)
    {
        if (path.Length == 0) return null;
        try
        {
            using var stream = File.OpenRead(path);
            return Bitmap.DecodeToWidth(stream, 320);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException
                                       or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static IBrush? Brush(string key) =>
        Application.Current?.TryGetResource(key, Application.Current.ActualThemeVariant, out var value) == true
            ? value as IBrush
            : null;
}
