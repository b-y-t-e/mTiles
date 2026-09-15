using System.Text.Json;

namespace mTiles.AgentSessions.Protocols;

/// <summary>
/// Tolerant reads of somebody else's JSON: a missing field, a field of the wrong kind and an absent
/// object all come back as null rather than as an exception.
/// </summary>
/// <remarks>Every agent protocol here is a CLI's contract that moves between releases. A field that
/// changed type must cost one blank in the view, never the session.</remarks>
public static class JsonFields
{
    public static JsonElement? Prop(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
                                                  && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? value
            : null;

    public static JsonElement? Prop(this JsonElement? element, string name) =>
        element is { } e ? e.Prop(name) : null;

    public static string? Str(this JsonElement element, string name) =>
        element.Prop(name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    public static string? Str(this JsonElement? element, string name) =>
        element is { } e ? e.Str(name) : null;

    public static long? Long(this JsonElement element, string name) =>
        element.Prop(name) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt64(out var n) ? n : null;

    public static long? Long(this JsonElement? element, string name) =>
        element is { } e ? e.Long(name) : null;

    public static decimal? Decimal(this JsonElement? element, string name) =>
        element?.Prop(name) is { ValueKind: JsonValueKind.Number } value && value.TryGetDecimal(out var n) ? n : null;

    public static decimal? Decimal(this JsonElement element, string name) => ((JsonElement?)element).Decimal(name);

    public static bool? Bool(this JsonElement element, string name) => ((JsonElement?)element).Bool(name);

    public static bool? Bool(this JsonElement? element, string name) =>
        element?.Prop(name) is { ValueKind: JsonValueKind.True or JsonValueKind.False } value ? value.GetBoolean() : null;

    public static IEnumerable<JsonElement> Items(this JsonElement? element, string name) =>
        element?.Prop(name) is { ValueKind: JsonValueKind.Array } array ? array.EnumerateArray() : [];

    public static IEnumerable<JsonElement> Items(this JsonElement element, string name) =>
        ((JsonElement?)element).Items(name);

    /// <summary>The element as indented JSON, for showing a tool's raw input to a person.</summary>
    public static string? Pretty(this JsonElement? element) =>
        element is { } e ? JsonSerializer.Serialize(e, PrettyOptions) : null;

    private static readonly JsonSerializerOptions PrettyOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
