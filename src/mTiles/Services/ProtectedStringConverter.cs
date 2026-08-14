using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace mTiles.Services;

public sealed class ProtectedStringConverter : JsonConverter<string>
{
    private const string Prefix = "enc:";
    private static readonly byte[] Entropy = "MTerminal.v1"u8.ToArray();
    private static bool _warnedNonWindows;

    /// <summary>
    /// So a <c>"Password": null</c> arrives here rather than being short-circuited into a null field.
    /// </summary>
    /// <remarks>
    /// A property's own converter wins over the one on the options, so
    /// <see cref="NullToEmptyStringConverter"/> — which covers every other string in the settings — never
    /// sees the encrypted ones. Without this they would be the last strings in the tree that a
    /// hand-edited file could still turn into a null.
    /// </remarks>
    public override bool HandleNull => true;

    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString() ?? "";
        if (!value.StartsWith(Prefix)) return value;
        return Unprotect(value[Prefix.Length..]);
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        if (string.IsNullOrEmpty(value))
        {
            writer.WriteStringValue("");
            return;
        }

        if (!OperatingSystem.IsWindows())
        {
            if (!_warnedNonWindows)
            {
                Trace.TraceWarning("DPAPI not available on this platform — passwords stored in plain text.");
                _warnedNonWindows = true;
            }
            writer.WriteStringValue(value);
            return;
        }

        writer.WriteStringValue(Prefix + Protect(value));
    }

    private static string Protect(string plainText)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(plainText);
            var encrypted = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(encrypted);
        }
        catch
        {
            Trace.TraceWarning("DPAPI Protect failed — password stored in plain text.");
            return plainText;
        }
    }

    private static string Unprotect(string encoded)
    {
        if (!OperatingSystem.IsWindows()) return encoded;
        try
        {
            var encrypted = Convert.FromBase64String(encoded);
            var bytes = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            // Backward compat: try without entropy (old format)
            try
            {
                var encrypted = Convert.FromBase64String(encoded);
                var bytes = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch { return encoded; }
        }
    }
}
