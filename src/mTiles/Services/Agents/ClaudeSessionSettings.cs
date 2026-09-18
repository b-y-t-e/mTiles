namespace mTiles.Services.Agents;

/// <summary>
/// The <c>--settings</c> file every Claude Code session this application holds is started with: the
/// <c>Concise</c> output style, and nothing else.
/// </summary>
/// <remarks>A file rather than inline JSON: the argument is typed into a shell, and Windows PowerShell 5.1
/// strips the quotes inside an argument it hands to a native program, so <c>{"outputStyle":"Concise"}</c>
/// would arrive as something the CLI cannot parse. A <c>--settings</c> file is layered over the user's own
/// settings, so everything else in theirs still applies. Same category as <see cref="OpenCodeProviderConfig"/>:
/// derived, rewritten only when it differs, never pruned.</remarks>
public static class ClaudeSessionSettings
{
    /// <summary>What the file holds.</summary>
    public const string Content = "{ \"outputStyle\": \"Concise\" }\n";

    /// <summary>Where the file lives.</summary>
    public static string PathFor() =>
        Path.Combine(AppPaths.GetAppDataDirectory(), "claude", "session-settings.json");

    /// <summary>Writes it when it is missing or stale and answers where it is, or null when it could not
    /// be written.</summary>
    /// <remarks>Fails soft: a tile on the CLI's own default style is better than a tile that did not
    /// start.</remarks>
    public static string? Write()
    {
        var path = PathFor();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (!File.Exists(path) || File.ReadAllText(path) != Content) File.WriteAllText(path, Content);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.WriteLine($"Could not write Claude Code session settings: {ex.Message}");
            return null;
        }
    }
}
