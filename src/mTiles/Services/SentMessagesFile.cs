using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace mTiles.Services;

/// <summary>
/// What was sent from a composer, oldest first, kept in a file of its own so it outlives the application —
/// the Goal tile's counterpart of the Agent tile's stored conversation, which its composer history is read out of.
/// </summary>
/// <remarks>
/// <para>A file beside the goal's own state rather than a field in it: the state is the engine's snapshot and is
/// written by the engine, while this list is the composer's and grows only when the user sends something.</para>
/// <para>Fails soft both ways — an unreadable file is an empty history, a write that fails is a history that lasts
/// until the tile closes. Neither is worth a goal that stops.</para>
/// </remarks>
public sealed class SentMessagesFile
{
    private readonly string _path;
    private readonly List<string> _entries;

    public SentMessagesFile(string path)
    {
        _path = path;
        _entries = Read(path);
    }

    /// <summary>Where the history of a goal whose state is at <paramref name="goalStatePath"/> is kept.</summary>
    public static string BesideGoal(string goalStatePath) =>
        Path.ChangeExtension(goalStatePath, ".sent.json");

    public IReadOnlyList<string> Entries => _entries;

    /// <summary>Records one sent message, trimmed; a blank one is not a message.</summary>
    public void Add(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        _entries.Add(text.Trim());
        Write();
    }

    private static List<string> Read(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path)) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Trace.TraceWarning($"Could not read the composer history {path}: {ex.Message}");
            return [];
        }
    }

    private void Write()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            PrivateFile.WriteAllText(_path, JsonSerializer.Serialize(_entries));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"Could not write the composer history {_path}: {ex.Message}");
        }
    }
}
