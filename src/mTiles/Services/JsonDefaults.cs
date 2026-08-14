using System.Text.Json;
using System.Text.Json.Serialization;

namespace mTiles.Services;

internal static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// The same, plus the rule that a <c>null</c> string in <c>settings.json</c> is an empty one.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Options"/> on purpose — see <see cref="NullToEmptyStringConverter"/> for
    /// why the rule belongs to the settings file and not to every file this application writes.
    /// </remarks>
    public static readonly JsonSerializerOptions SettingsOptions = new(Options)
    {
        Converters = { new NullToEmptyStringConverter() }
    };
}
