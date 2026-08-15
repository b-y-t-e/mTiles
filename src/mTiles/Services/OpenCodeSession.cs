using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using mTiles.Models;

namespace mTiles.Services;

/// <summary>
/// What it takes to give an OpenCode tile the same conversation back after a restart: a session id the
/// tile chooses, and the document that brings that id into being.
/// <para>OpenCode is the awkward case among the seeded profiles. Claude Code and pi let the caller name
/// a session outright (<c>--session-id</c>), so a tile hands over its own <c>TileId</c> and is done.
/// <c>opencode --session &lt;id&gt;</c> only ever <em>continues</em> one — an id we invent is refused
/// (<c>Session not found</c>, exit 1 after ~1.4 s) — and the TUI creates no session at all until the
/// first message is sent, so there is nothing to observe at startup and pick up afterwards either.</para>
/// <para>The way in is <c>opencode import</c>, which takes a JSON document and keeps its <c>id</c>
/// verbatim. That turns "learn the id opencode chose" into "tell opencode the id", which is the shape
/// the launch chain already knows how to run.</para>
/// <para>Measured against <b>opencode 1.18.14</b> on Windows, and every point below is load-bearing:</para>
/// <list type="bullet">
/// <item>Only the <c>ses</c> prefix is enforced on the id, so <c>ses_&lt;tileId&gt;</c> is legal and no
/// tile-to-session bookkeeping is needed anywhere.</item>
/// <item>The document's <c>projectID</c> and <c>directory</c> are <b>ignored</b> — the session lands in
/// the project of the <em>current working directory</em> of the import. So the import has to run in the
/// tile's workspace, which is exactly where the chain runs its commands. The fields are still written
/// truthfully because they are part of the format, not because anything reads them.</item>
/// <item>Importing an id that already exists is <b>non-destructive</b>: the title and the existing
/// messages are kept and only <c>time.updated</c> moves. That is what makes this a create-if-missing
/// rather than a way to wipe the conversation the tile is trying to resume.</item>
/// <item>It costs ~1.1 s and no model call, and the session is a row in a shared sqlite database, so the
/// resume that follows in the same command sees it at once.</item>
/// <item><b>Every field below is required.</b> A document carrying only <c>id</c> and <c>time</c> is
/// rejected with "Missing key" — which does not say <em>which</em> key, so the shape is written out in
/// full rather than trimmed to what looks meaningful.</item>
/// </list>
/// <para>The document is opencode's own <em>export</em> format and not a documented API, so it can move
/// under us. It fails softly if it does: the import prints an error, the resume after it finds no
/// session and exits, and the chain carries on to a plain interactive shell — a tile with no history
/// rather than no tile.</para>
/// </summary>
internal static class OpenCodeSession
{
    /// <summary>The prefix opencode insists on. The rest of the id is the tile's own.</summary>
    private const string IdPrefix = "ses_";

    /// <summary>The session id a tile resumes, which is what <c>${tileId}</c> in the seeded profile is
    /// prefixed with. Here so the two halves — the id in the script and the id in the document — cannot
    /// be written differently in two places.</summary>
    public static string IdFor(string tileId) => IdPrefix + tileId;

    /// <summary>
    /// Where the tile's import document lives. Derived from the tile id rather than handed back from
    /// somewhere, so <see cref="TileScript"/> can expand the path as a plain function of the id and
    /// nothing has to thread a file name through the launch path.
    /// </summary>
    /// <param name="sessionsDirectory">Where the documents live. Defaults to the real one; a test passes
    /// a temporary directory, because the alternative is a test that writes into the settings of whoever
    /// is running it. <see cref="TileScript"/> always takes the default — the token has to stay a pure
    /// function of the tile id.</param>
    public static string DocumentPath(string tileId, string? sessionsDirectory = null)
    {
        // The file-name half of TileScript's rule, asked of the same one implementation: this builds a
        // path out of the value, and an id from a hand-edited layout file is not to be trusted with one.
        // (That makes the two types mutually dependent — TileScript expands the token by asking for this
        // path, and this asks TileScript what an id may look like. Deliberate: the rule has one owner,
        // and the alternative is a second copy of it that will disagree about `..\` exactly once.)
        if (!TileScript.IsUsableId(tileId))
            throw new ArgumentException($"A tile id must be a GUID in the plain hyphenated form; "
                + $"'{tileId}' is not, and it would be used as a file name.", nameof(tileId));

        return Path.Combine(sessionsDirectory ?? SessionsDirectory, IdFor(tileId) + ".json");
    }

    /// <summary>
    /// Under <c>sessions/</c> rather than at the top: Codex is the same problem with a different CLI and
    /// will want its own corner of this.
    /// </summary>
    /// <remarks>
    /// <b>Nothing prunes it, on purpose.</b> Each file is a few hundred bytes and there is one per tile
    /// that has ever launched an OpenCode profile, so a heavy user reaches kilobytes; the logs get a
    /// retention sweep because they are written continuously and by size, and this is neither. Deleting
    /// an old one would also not be free: while the file exists a session the user threw away can be
    /// recreated on the next launch, which is the behaviour the whole arrangement is for.
    /// </remarks>
    private static string SessionsDirectory =>
        Path.Combine(AppPaths.GetAppDataDirectory(), "sessions", "opencode");

    /// <summary>
    /// Writes the tile's import document when its profile actually asks for one, and never fails a
    /// launch over it.
    /// <para>Written on every launch rather than only when it is missing: it is a few hundred bytes, the
    /// re-import is non-destructive, and rewriting it is what keeps a session that the user deleted
    /// behind the tile's back recreatable — the resume fails, the fallback imports this again, and the
    /// tile keeps its identity even though the conversation behind it is gone.</para>
    /// </summary>
    /// <param name="sessionsDirectory"><inheritdoc cref="DocumentPath" path="/param[@name='sessionsDirectory']"/></param>
    public static void PrepareIfReferenced(LaunchScripts scripts, string tileId, string workingDirectory,
        string? sessionsDirectory = null)
    {
        if (!References(scripts.Startup) && !References(scripts.Fallback))
            return;

        try
        {
            var path = DocumentPath(tileId, sessionsDirectory);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            WriteAtomically(path, Document(tileId, workingDirectory, DateTimeOffset.UtcNow));
        }
        catch (Exception ex)
        {
            // Nothing here is worth losing the tile over. Without the document the import fails, the
            // resume after it fails, and the chain ends at an interactive shell — which is a working
            // tile without its history, and this line is the only place that says why.
            Trace.TraceWarning("The OpenCode session document for tile {0} could not be written, so the "
                + "tile will start without its previous conversation: {1}", tileId, ex);
        }
    }

    /// <summary>
    /// Writes through a temporary file beside the real one and moves it into place, as
    /// <see cref="GitIgnoreFile"/> does and for the same reason.
    /// <para>The straightforward write truncates first, so a write that fails halfway leaves a document
    /// that parses as nothing — and the caller swallows failures so the tile still launches. The launch
    /// then imports the wreckage, the import is refused, and the tile loses its history for that session.
    /// A move keeps the previous document instead, which is <em>enough</em>: importing it again is
    /// create-if-missing and non-destructive, so the conversation comes back.</para>
    /// </summary>
    private static void WriteAtomically(string path, string content)
    {
        var temporary = path + ".mtiles-tmp";
        try
        {
            File.WriteAllText(temporary, content);
            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temporary); } catch { /* the previous document is untouched, which is the point */ }
            throw;
        }
    }

    private static bool References(string? script) =>
        script?.Contains(TileScript.OpenCodeSessionFileToken) == true;

    /// <summary>
    /// The document itself, as a function of its inputs so its shape can be read in a test rather than
    /// off a disk. See the type's remarks for what opencode does and does not read out of it.
    /// </summary>
    internal static string Document(string tileId, string workingDirectory, DateTimeOffset now)
    {
        var stamp = now.ToUnixTimeMilliseconds();
        var document = new ImportDocument(
            new SessionInfo(
                Id: IdFor(tileId),
                Slug: "mtiles-" + Short(tileId),
                // Ignored on import — the project comes from the cwd. Written because the format has the
                // field, not because anything reads it.
                ProjectId: "",
                Directory: workingDirectory,
                Title: "mTiles tile " + Short(tileId),
                // Not opencode's version and not this app's: it is the format's producer stamp, nothing
                // validates it (measured — "0.0.0" imports fine), and putting a real opencode version we
                // did not read from opencode would be a guess that reads as a fact.
                Version: "0.0.0",
                Time: new SessionTime(stamp, stamp)),
            Messages: []);

        return JsonSerializer.Serialize(document, DocumentOptions);
    }

    /// <summary>Enough of the id to tell two tiles apart at a glance in <c>opencode session list</c>;
    /// the whole GUID would be the only thing on the line.</summary>
    private static string Short(string tileId) => tileId.Length <= 8 ? tileId : tileId[..8];

    /// <summary>Its own options, deliberately not <see cref="JsonDefaults"/>: this file is read by
    /// another program, so its property names are a contract and the naming policy of ours has no
    /// business touching them. Hence the explicit names on every member below, <c>projectID</c>
    /// included — that spelling is opencode's, not a slip.</summary>
    private static readonly JsonSerializerOptions DocumentOptions = new() { WriteIndented = false };

    private sealed record ImportDocument(
        [property: JsonPropertyName("info")] SessionInfo Info,
        [property: JsonPropertyName("messages")] IReadOnlyList<object> Messages);

    private sealed record SessionInfo(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("slug")] string Slug,
        [property: JsonPropertyName("projectID")] string ProjectId,
        [property: JsonPropertyName("directory")] string Directory,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("version")] string Version,
        [property: JsonPropertyName("time")] SessionTime Time);

    private sealed record SessionTime(
        [property: JsonPropertyName("created")] long Created,
        [property: JsonPropertyName("updated")] long Updated);
}
