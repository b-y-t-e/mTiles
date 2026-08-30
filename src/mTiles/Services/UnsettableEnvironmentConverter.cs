using System.Text.Json;
using System.Text.Json.Serialization;

namespace mTiles.Services;

/// <summary>
/// Reads and writes an environment block in which <c>null</c> means <em>remove this variable</em>.
/// </summary>
/// <remarks>
/// <para>The settings file turns every null string into an empty one
/// (<see cref="NullToEmptyStringConverter"/>), which is right nearly everywhere and wrong here: an
/// environment value of <c>""</c> sets a variable to nothing, while <c>null</c> unsets it. On a machine
/// that exports a global <c>ANTHROPIC_API_KEY</c> those are different outcomes — one leaves the CLI
/// authenticating on the inherited account, the other is the whole reason
/// <c>PtyOptions.Environment</c> learned to remove.</para>
/// <para>Applied per property rather than by making the general rule cleverer: a converter is chosen by
/// type and never told which property it fills, so "nullable means nullable" is not something
/// <see cref="NullToEmptyStringConverter"/> could decide for itself. A property-level
/// <c>[JsonConverter]</c> wins over the registered one, which is the same escape hatch
/// <c>ProtectedStringConverter</c> uses.</para>
/// </remarks>
internal sealed class UnsettableEnvironmentConverter : JsonConverter<Dictionary<string, string?>>
{
    /// <summary>A whole block written as <c>null</c> is an empty one — the same rule every collection in
    /// the settings follows, and the reason the property's own setter refuses a null too.</summary>
    public override bool HandleNull => true;

    public override Dictionary<string, string?> Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (reader.TokenType == JsonTokenType.Null) return result;

        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("An environment block has to be an object.");

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) return result;
            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new JsonException("An environment block has to be an object.");

            var name = reader.GetString() ?? "";
            reader.Read();
            result[name] = reader.TokenType == JsonTokenType.Null ? null : reader.GetString();
        }

        throw new JsonException("An environment block ended before it was closed.");
    }

    public override void Write(Utf8JsonWriter writer, Dictionary<string, string?> value,
        JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var (name, setting) in value)
        {
            writer.WritePropertyName(name);
            if (setting is null) writer.WriteNullValue();
            else writer.WriteStringValue(setting);
        }
        writer.WriteEndObject();
    }
}
