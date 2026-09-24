using System.Diagnostics;
using mTiles.Models;

namespace mTiles.Services.Agents.SessionLogs;

/// <summary>
/// The part of reading a session store that is the same for every CLI: find the candidates, pick the
/// one this tile may have, read it, and never throw.
/// </summary>
/// <remarks>
/// <para>The same division <c>SessionCapture</c> makes. What is shared is not a procedure but the
/// plumbing underneath — enumerating a directory, ordering by time, applying the two filters that keep a
/// tile off somebody else's conversation, and turning every failure into <c>null</c>. What differs is
/// where the files are and what is in them, and that is a table per agent, which is where a table
/// belongs.</para>
/// <para><b>Everything runs off the caller's thread</b> (<c>Task.Run</c>), because every subclass here
/// is doing file I/O and the callers are the UI thread. <b>And nothing here throws</b>: a store whose
/// shape has moved, a directory the user has deleted mid-read, a file the CLI is rewriting as we open it
/// — each of those has to end as "no reading", because what it costs is a gauge and a resume while a
/// thrown exception costs the tile.</para>
/// </remarks>
public abstract class AgentSessionLog : IAgentSessionLog
{
    /// <summary>One conversation the store holds, before anything inside it has been read.</summary>
    /// <param name="Id">What the CLI would resume it by.</param>
    /// <param name="StartedAt">When the conversation began. Kept apart from the write time because
    /// these CLIs append to a session for as long as it is open.</param>
    /// <param name="UpdatedAt">When it was last written — what <c>since</c> is compared against and how
    /// the newest is chosen. The write time rather than the start, because <c>/resume</c> inside the TUI
    /// goes on appending to a conversation begun before the tile was, and filtered by its start that
    /// move would never be followed.</param>
    /// <param name="File">Where it is kept, when the store found it by a file of its own — so reading it
    /// does not have to look for that file a second time.</param>
    protected readonly record struct SessionEntry(string Id, DateTimeOffset StartedAt, DateTimeOffset UpdatedAt,
        FileInfo? File = null);

    /// <summary>Every conversation this store holds for that working directory, in any order — or, for a
    /// store that can only tell by opening the file, every candidate, narrowed by
    /// <see cref="BelongsTo"/>.</summary>
    /// <remarks>Called on a thread-pool thread, and may throw whatever the file system throws — the
    /// catching is here, once, rather than in six subclasses.</remarks>
    protected abstract IEnumerable<SessionEntry> Enumerate(AiSignIn? signIn, string workspaceDir);

    /// <summary>Whether a candidate <see cref="Enumerate"/> gave is kept for that working directory.
    /// </summary>
    /// <remarks>True by default, for a store that files by working directory and has already answered.
    /// Overridden by one that files otherwise, where the answer costs a file opened — asked after the
    /// time filter and newest first, so only the candidates that could still be taken pay for it.
    /// </remarks>
    protected virtual bool BelongsTo(string workspaceDir, SessionEntry entry) => true;

    /// <summary>What that conversation says about itself, or null when it will not say.</summary>
    protected abstract AgentSessionReading? Read(AiSignIn? signIn, string workspaceDir, SessionEntry entry);

    /// <summary>The conversation this store holds under that id for that working directory, or null.
    /// </summary>
    /// <remarks>Asked by id and matched by id, rather than trusting the caller's idea of where the file
    /// is: a session the user moved to with /resume is in the same directory as one this tile started,
    /// and only the store knows which name it ended up under. Overridden by a store where walking every
    /// candidate costs more than looking the one up.</remarks>
    protected virtual SessionEntry? Find(AiSignIn? signIn, string workspaceDir, string sessionId,
        CancellationToken ct)
    {
        foreach (var entry in Enumerate(signIn, workspaceDir))
        {
            ct.ThrowIfCancellationRequested();
            if (string.Equals(entry.Id, sessionId, StringComparison.Ordinal))
                return BelongsTo(workspaceDir, entry) ? entry : null;
        }

        return null;
    }

    /// <inheritdoc />
    public abstract string? WatchDirectory(AiSignIn? signIn, string workspaceDir);

    /// <inheritdoc />
    /// <remarks>False by default: an agent whose store has not been measured for such a mark is one
    /// whose headless runs would be indistinguishable from the conversation a tile moved to.</remarks>
    public virtual bool TellsHeadlessRunsApart => false;

    /// <summary>Whether that conversation was held in the CLI's own interface rather than run headless.
    /// </summary>
    /// <remarks>Only an interactive conversation can be the one a terminal tile has moved to, and the
    /// headless ones share its directory: a Goal tile's run, an Agent tile's session and a script's
    /// <c>-p</c> all file themselves beside it. Overridden by every store that
    /// <see cref="TellsHeadlessRunsApart"/>; a watcher never asks <see cref="ReadLatestAsync"/> of one
    /// that does not, so the default is only what a direct caller gets.</remarks>
    protected virtual bool IsInteractive(AiSignIn? signIn, string workspaceDir, SessionEntry entry) => true;

    /// <inheritdoc />
    public Task<AgentSessionReading?> ReadLatestAsync(AiSignIn? signIn, string workspaceDir,
        DateTimeOffset since, Func<string, bool>? isFree = null, CancellationToken ct = default) =>
        Task.Run(() => Safely(() =>
        {
            // isFree is last because a caller may well *claim* the id in it: asking whether one is free
            // and taking it afterwards is two steps, and two tiles restored from one layout race through
            // them together — and a headless run must never be claimed on the way past.
            foreach (var entry in InteractiveSince(signIn, workspaceDir, since, ct))
            {
                if (isFree?.Invoke(entry.Id) == false) continue;
                return Read(signIn, workspaceDir, entry);
            }

            return null;
        }), ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> ListInteractiveAsync(AiSignIn? signIn, string workspaceDir,
        DateTimeOffset since, CancellationToken ct = default) =>
        Task.Run(() => Safely<IReadOnlyList<string>>(
            () => InteractiveSince(signIn, workspaceDir, since, ct).Select(entry => entry.Id).ToList(),
            []), ct);

    /// <summary>The interactive conversations written since a moment, newest first.</summary>
    /// <remarks>Newest first, then the time filter, then the two that cost a file opened — in that order
    /// and not the other way round, so nothing is opened on behalf of a candidate that was never this
    /// tile's to take. Lazy, so a caller that takes the first stops there.</remarks>
    private IEnumerable<SessionEntry> InteractiveSince(AiSignIn? signIn, string workspaceDir,
        DateTimeOffset since, CancellationToken ct)
    {
        foreach (var entry in Enumerate(signIn, workspaceDir)
                     .Where(entry => entry.UpdatedAt >= since)
                     .OrderByDescending(entry => entry.UpdatedAt))
        {
            ct.ThrowIfCancellationRequested();
            if (!BelongsTo(workspaceDir, entry)) continue;
            if (!IsInteractive(signIn, workspaceDir, entry)) continue;
            yield return entry;
        }
    }

    /// <inheritdoc />
    public Task<AgentSessionReading?> ReadAsync(AiSignIn? signIn, string workspaceDir, string sessionId,
        CancellationToken ct = default) =>
        Task.Run(() => Safely(() =>
        {
            if (sessionId.Length == 0) return null;
            return Find(signIn, workspaceDir, sessionId, ct) is { } entry
                ? Read(signIn, workspaceDir, entry)
                : null;
        }), ct);

    /// <inheritdoc />
    /// <remarks>Virtual here and not left to the interface's default, the rule <c>UsesModelContextWindow</c>
    /// sets: a default interface member is only reached through the interface.</remarks>
    public virtual bool ReadsTranscripts => false;

    /// <inheritdoc />
    public Task<IReadOnlyList<TranscriptTurn>> ReadTranscriptAsync(AiSignIn? signIn, string workspaceDir,
        string sessionId, CancellationToken ct = default) =>
        Task.Run(() => Safely<IReadOnlyList<TranscriptTurn>>(() =>
        {
            if (!ReadsTranscripts || sessionId.Length == 0) return [];
            return Find(signIn, workspaceDir, sessionId, ct) is { } entry
                ? TranscriptOf(signIn, entry)
                : [];
        }, []), ct);

    /// <summary>The conversation's messages, for a store that <see cref="ReadsTranscripts"/>.</summary>
    protected virtual IReadOnlyList<TranscriptTurn> TranscriptOf(AiSignIn? signIn, SessionEntry entry) => [];

    /// <summary>Every line of a transcript, read with sharing because the CLI may be writing it.</summary>
    protected static IEnumerable<string> ReadAllLines(FileInfo file)
    {
        using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line) yield return line;
    }

    /// <summary>The turns read line by line, with a turn that repeats the one before it dropped — codex
    /// writes the same message under two event shapes in some versions.</summary>
    protected static IReadOnlyList<TranscriptTurn> TurnsOf(IEnumerable<string> lines,
        Func<string, TranscriptTurn?> turnIn)
    {
        var turns = new List<TranscriptTurn>();
        foreach (var line in lines)
        {
            if (turnIn(line) is not { } turn || turn.Text.Length == 0) continue;
            if (turns.Count > 0 && turns[^1] == turn) continue;
            turns.Add(turn);
        }
        return turns;
    }

    /// <summary>Runs a read, answering null for anything that goes wrong with it.</summary>
    /// <remarks>Cancellation is let through, because a cancelled read is the caller's own doing and
    /// swallowing it would report "this agent says nothing" for a question nobody is asking any more.
    /// </remarks>
    private AgentSessionReading? Safely(Func<AgentSessionReading?> read) => Safely(read, null);

    private T Safely<T>(Func<T> read, T nothing)
    {
        try
        {
            return read();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Reading {0}'s own session store failed, so the tile has no reading from "
                + "it: {1}", GetType().Name, ex.Message);
            return nothing;
        }
    }

    /// <summary>The files in a directory, or nothing at all where there is no such directory.</summary>
    /// <remarks>A convenience every subclass needs and none should spell twice: a working directory this
    /// CLI has never been run in simply has no directory, which is an answer and not a failure.</remarks>
    protected static IEnumerable<FileInfo> FilesIn(string? directory, string pattern)
    {
        if (directory is not { Length: > 0 } || !Directory.Exists(directory)) return [];
        return new DirectoryInfo(directory).EnumerateFiles(pattern, SearchOption.TopDirectoryOnly);
    }

    /// <summary>When a file's content began, as opposed to when it was last appended to.</summary>
    /// <remarks>The rule <c>SessionCapture.Started</c> follows, and for the same reason: every store
    /// here appends for the life of the conversation. The write time is taken when it is the earlier of
    /// the two, which is what a file system that records no creation time reports.</remarks>
    protected static DateTimeOffset StartedAt(FileSystemInfo file) =>
        file.CreationTimeUtc < file.LastWriteTimeUtc ? file.CreationTimeUtc : file.LastWriteTimeUtc;

    /// <summary>The user's home directory, where five of these six CLIs keep their store.</summary>
    protected static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
}
