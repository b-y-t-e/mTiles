using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Avalonia.Layout;

namespace mTiles.Models;

/// <summary>
/// One node of a saved tile tree: either a leaf holding a tile, or a split holding two nodes.
/// </summary>
/// <remarks>
/// <para><b>The layout must come back looking exactly as it went in.</b> That is the acceptance
/// criterion for the move from a closed <see cref="TileContentType"/> to a registered kind, and the
/// compatibility properties below are most of what serves it. Each is the old field, and each is read
/// <em>and</em> written: the setter copies the value into its new home, and the getter says the same
/// thing back out of that home so the file carries both formats at once.</para>
/// <para><b>The dual write is what makes the change reversible.</b> Reading the old fields covers the
/// update; writing them covers the rollback, which this application treats as a real event — Velopack
/// can put an older build back, and <c>settings.json</c> is already written to be survivable that way.
/// An older build knows nothing of <see cref="Kind"/> or of <see cref="Settings"/>' per-kind keys, so a
/// file holding only those opens as a workspace of empty tiles, and the first splitter drag saves that
/// emptiness over the layout — the one thing here a user cannot reconstruct from anything else.
/// <c>{id}.pre-kind.json</c> is a copy nothing tells them about; this needs telling nobody. It is a
/// bridge, not a format: once no supported build reads the old fields, the getters go and these become
/// the write-nothing compatibility properties <c>WorkspaceState.RootPane</c> is.</para>
/// <para>What did not change at all: <see cref="SplitOrientation"/>, <see cref="SplitRatio"/>,
/// <see cref="First"/>, <see cref="Second"/>, <see cref="TileId"/>, <see cref="TileName"/> and
/// <see cref="IsActive"/> — the whole geometry and identity of the tree. And no file outside
/// <c>workspaces/{id}.json</c> is touched: notes, todo lists and goal files stay exactly where they
/// are, and only the path recorded for them moves within the JSON.</para>
/// </remarks>
public sealed class TileNode
{
    public bool IsLeaf { get; set; }

    private string _kind = TileKindIds.None;
    private bool _kindWasNamed;
    private bool _anOldFieldArrived;

    /// <summary>Which kind of tile this leaf holds, or empty for a tile with no content yet.</summary>
    public string Kind
    {
        get => _kind;
        set { _kind = value ?? TileKindIds.None; _kindWasNamed = true; }
    }

    public string? TileId { get; set; }
    public string? TileName { get; set; }
    public bool IsActive { get; set; }

    private readonly JsonObject _settings = [];

    /// <summary>
    /// Everything else the tile's kind needs in order to rebuild it — its shell, its file, its profile.
    /// </summary>
    /// <remarks>
    /// <para>A <see cref="JsonObject"/> rather than a <c>Dictionary&lt;string, object?&gt;</c>: the same
    /// bytes on disk, without the <c>val is JsonElement</c> dance reading one back used to need.</para>
    /// <para>The getter answers null when there is nothing to say, so a tile with no state of its own
    /// adds no line to the file. The setter <b>merges and refuses a null</b>, which is what makes the
    /// migration independent of the order the old fields appear in: a legacy <c>ShellName</c> read
    /// before this property would otherwise be wiped by the <c>"Settings": null</c> that sits after it
    /// in every layout an older build wrote.</para>
    /// </remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonObject? Settings
    {
        get => _settings.Count > 0 ? _settings : null;
        set
        {
            if (value is null) return;
            foreach (var (key, node) in value)
                _settings[key] = node?.DeepClone();
        }
    }

    public Orientation SplitOrientation { get; set; } = Orientation.Vertical;
    public double SplitRatio { get; set; } = 0.5;
    public TileNode? First { get; set; }
    public TileNode? Second { get; set; }

    /// <summary>Whether this node arrived in the format used before tile kinds existed.</summary>
    /// <remarks>
    /// <para>Not persisted, and the whole reason it exists: rewriting a layout in the new format is the
    /// only moment at which every one of those files is replaced at once, and a tile layout is the one
    /// thing in this application a user cannot reconstruct from anything else. It is what tells the
    /// loader to take a copy first.</para>
    /// <para>It asks both halves of the question, because the dual write means an old field is no
    /// longer evidence of an old file: a layout is legacy when it carried one of them and never said
    /// <see cref="Kind"/>. Reading the file's own words rather than the order they arrive in — a
    /// hand-edited layout may put them either way round, and a backup taken on every launch would
    /// overwrite nothing but still be a lie about what the file is.</para>
    /// </remarks>
    [JsonIgnore]
    public bool IsLegacyFormat => _anOldFieldArrived && !_kindWasNamed;

    /// <summary>
    /// The kind, as every version before this one wrote it.
    /// </summary>
    /// <remarks>
    /// A lower-casing and nothing else. <c>JsonDefaults.Options</c> registers
    /// <c>JsonStringEnumConverter</c>, so this was already a <em>string</em> on disk — the change is one
    /// of type in C# over identical bytes in the file. A kind with no name in the closed enum writes
    /// nothing here, which is the honest answer: an older build could not have built it either.
    /// </remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TileContentType? ContentType
    {
        get => TileKindIds.ToLegacy(_kind);
        set
        {
            if (value is not { } type) return;
            _anOldFieldArrived = true;
            // The file may carry both. Kind is the one this build wrote, so it is the one that wins,
            // whichever of the two the reader reached first.
            if (!_kindWasNamed) _kind = TileKindIds.FromLegacy(type);
        }
    }

    /// <summary>The shell an older build would run this leaf in.</summary>
    /// <remarks>Echoed for an agent tile as well, and that is the other half of its rollback: without a
    /// shell name beside the <c>terminal</c> content type, the degraded tile would open on whatever
    /// this machine's default happens to be rather than on the one it was running.</remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ShellName
    {
        get => Echo(TileKindIds.Terminal, "shellName") ?? Echo(TileKindIds.Agent, "shellName");
        set => Adopt("shellName", value);
    }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UserProfileId
    {
        get => Echo(TileKindIds.Terminal, "userProfileId");
        set => Adopt("userProfileId", value);
    }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NoteFilePath
    {
        get => Echo(TileKindIds.Note, "filePath");
        set => Adopt("filePath", value);
    }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TodoFilePath
    {
        get => Echo(TileKindIds.Todo, "filePath");
        set => Adopt("filePath", value);
    }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GoalFilePath
    {
        get => Echo(TileKindIds.Goal, "filePath");
        set => Adopt("filePath", value);
    }

    /// <summary>Moves one old field into the state its kind now reads.</summary>
    /// <remarks>A blank is not adopted: an old file carries every one of these on every leaf, so
    /// copying them across unconditionally would give a note a <c>shellName</c> of nothing and a
    /// terminal a <c>filePath</c> of nothing.</remarks>
    private void Adopt(string key, string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        _anOldFieldArrived = true;
        _settings[key] = value;
    }

    /// <summary>Says a piece of this kind's state back out under the name an older build reads it by.</summary>
    /// <remarks>Gated on the kind, because three of the old fields are the same key: a note's file path
    /// and a goal's are both <c>filePath</c>, and writing all three would tell an older build that one
    /// tile is three kinds at once.</remarks>
    private string? Echo(string kind, string key) =>
        _kind == kind && _settings[key] is JsonValue value && value.TryGetValue<string>(out var text)
            ? text
            : null;
}
