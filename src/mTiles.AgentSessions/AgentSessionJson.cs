using System.Text.Encodings.Web;
using System.Text.Json;

namespace mTiles.AgentSessions;

/// <summary>
/// The one set of JSON options every event, command and state is written with — on disk and, later, on
/// the wire to a browser.
/// </summary>
/// <remarks>camelCase because that is what a browser reads without a mapping layer; the discriminators and
/// the enum spellings come from attributes on the types themselves, so they are the same whoever
/// serializes them.</remarks>
public static class AgentSessionJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}
