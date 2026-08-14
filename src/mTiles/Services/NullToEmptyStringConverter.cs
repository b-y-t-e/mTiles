using System.Text.Json;
using System.Text.Json.Serialization;

namespace mTiles.Services;

/// <summary>
/// Reads <c>null</c> in the settings file as an empty string.
/// </summary>
/// <remarks>
/// <para>Every section and collection in the settings refuses a null in its own setter, because a
/// property initialiser does not survive deserialisation: <c>"Speech": null</c> is not an error the
/// load's own catch would ever see, it is a <see cref="NullReferenceException"/> while the main window is
/// being built — the application does not start and says nothing about why. The strings had the same hole
/// and only some of them were plugged, one property at a time, as each was found the hard way. That is a
/// list that can only ever be almost complete: the settings tree has dozens of strings across seven
/// types, and the one nobody guarded is the one a hand-edited file will name.</para>
/// <para>So the rule is stated once, for the type, rather than repeated at each property. The existing
/// per-property guards stay — they also catch a null assigned in code, which no converter ever sees — but
/// they are no longer the only thing standing between a null in the file and a window that never
/// appears.</para>
/// <para><b>Registered on the settings' own options</b> (<see cref="JsonDefaults.SettingsOptions"/>) and
/// not on the shared ones: elsewhere — workspace layouts, goal state — a null string may well be meant to
/// stay a null, and widening this to every file the application writes would be a decision about each of
/// them made by not thinking about any of them.</para>
/// <para>A property carrying its own <c>[JsonConverter]</c> wins over this one, which is why
/// <see cref="ProtectedStringConverter"/> handles its own nulls.</para>
/// <para><b>It overrules <c>string?</c> as well, and cannot do otherwise.</b> A
/// <c>JsonConverter&lt;string&gt;</c> is chosen by type and is never told which property it is filling,
/// so a property annotated as nullable — a promise that null is an answer — still comes back empty when
/// the file says null. The annotation survives only for what code assigns directly. There are two such
/// properties in the settings, <c>AppSettings.LastWorkspaceId</c> and
/// <c>UserShellProfile.RequiredAiToolBinaryName</c>; both are read through <c>IsNullOrEmpty</c>, so empty
/// and null are the same answer to every caller. <c>SettingsNullGuardTests</c> pins that list, because
/// the next nullable string to be added here is one somebody has to have thought about — and the same
/// tests skip nullable properties when checking the setters, which is the opposite rule stated for a
/// different question.</para>
/// </remarks>
internal sealed class NullToEmptyStringConverter : JsonConverter<string>
{
    /// <summary>Without this the framework short-circuits a null token and never calls
    /// <see cref="Read"/> — the whole case this exists for.</summary>
    public override bool HandleNull => true;

    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.Null ? "" : reader.GetString() ?? "";

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        => writer.WriteStringValue(value);

    // Dictionary keys go through a separate pair, and a custom string converter that does not implement
    // them is not merely ignored there: serialising Dictionary<string, string> throws
    // NotSupportedException. `CustomAiToolPaths` and `GoalDefaultModels` are exactly that, so without
    // these two the settings would stop saving altogether — a far worse failure than the one above.
    public override string ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options) => reader.GetString() ?? "";

    public override void WriteAsPropertyName(Utf8JsonWriter writer, string value,
        JsonSerializerOptions options) => writer.WritePropertyName(value);
}
