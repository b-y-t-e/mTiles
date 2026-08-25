using System.Text.Json;
using mTiles.Models;
using mTiles.Services;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The document that gives an OpenCode tile its session back, and the token a profile names it with.
/// <para>Everything asserted here about opencode's side was <b>measured against opencode 1.18.14</b> on
/// Windows, not read out of documentation — the import format is opencode's own export format and has no
/// specification. That is what these tests are for: they pin our half of a contract whose other half can
/// move under us, so when it does, the failure is here rather than in a tile that quietly lost its
/// history.</para>
/// </summary>
public sealed class OpenCodeSessionTests
{
    private const string TileId = "1b9d6bcd-bbfd-4b2d-9b5d-ab8dfbbd4bed";

    private static JsonElement Info(string tileId = TileId, string workingDirectory = @"D:\work\repo")
    {
        var json = OpenCodeSession.Document(tileId, workingDirectory, DateTimeOffset.UnixEpoch.AddDays(1));
        return JsonDocument.Parse(json).RootElement.GetProperty("info");
    }

    // ---- the session id --------------------------------------------------------

    /// <summary>The one thing opencode enforces about an id it is handed: the <c>ses</c> prefix. The rest
    /// is the tile's own, which is what removes any need to remember which session belongs to which tile.
    /// </summary>
    [Fact]
    public void The_session_id_is_the_tile_id_under_opencodes_required_prefix()
        => Assert.Equal("ses_" + TileId, OpenCodeSession.IdFor(TileId));

    // ---- the document ----------------------------------------------------------

    /// <summary>
    /// Every field, because opencode wants every field. A document carrying only <c>id</c> and
    /// <c>time</c> is rejected with <c>Missing key</c> — which does not say <em>which</em> key, so
    /// trimming this to what looks meaningful is a change nobody can debug from the error.
    /// <para>The whole set at once rather than a case per name: an equality on the set also fails when
    /// a field is <em>renamed</em>, which a per-name presence check cannot see — it would report the
    /// old name missing and say nothing about the new one that opencode will reject.</para>
    /// </summary>
    [Fact]
    public void The_document_carries_every_field_the_import_insists_on()
    {
        // `projectID` is opencode's spelling, not a slip here.
        string[] required = ["id", "slug", "projectID", "directory", "title", "version", "time"];

        Assert.Equal(required.Order(), Info().EnumerateObject().Select(p => p.Name).Order());
    }

    /// <summary>The id the tile will resume is the one it asked for. Pinned on the composed document
    /// here; <see cref="A_profile_that_asks_for_a_document_gets_one_written"/> pins it again on the
    /// file opencode is actually handed.</summary>
    [Fact]
    public void The_document_declares_the_id_the_tile_will_resume()
        => Assert.Equal(OpenCodeSession.IdFor(TileId), Info().GetProperty("id").GetString());

    [Fact]
    public void A_new_session_starts_with_no_messages()
    {
        var root = JsonDocument.Parse(
            OpenCodeSession.Document(TileId, @"D:\work\repo", DateTimeOffset.UnixEpoch)).RootElement;

        Assert.Equal(JsonValueKind.Array, root.GetProperty("messages").ValueKind);
        Assert.Equal(0, root.GetProperty("messages").GetArrayLength());
    }

    /// <summary>Milliseconds since the epoch, which is what opencode writes and reads. Seconds would be
    /// accepted and then sort every mTiles session to the beginning of 1970 in its session list.</summary>
    [Fact]
    public void The_timestamps_are_epoch_milliseconds()
    {
        var time = Info().GetProperty("time");

        Assert.Equal(86_400_000, time.GetProperty("created").GetInt64());
        Assert.Equal(86_400_000, time.GetProperty("updated").GetInt64());
    }

    /// <summary>
    /// Written truthfully, and read by nobody: measured, the import <em>ignores</em> both
    /// <c>directory</c> and <c>projectID</c> and puts the session in the project of the current working
    /// directory instead. That is why the import has to run in the tile's workspace — which is where the
    /// chain runs its commands — and why nothing here can be fixed by changing this field.
    /// </summary>
    [Fact]
    public void The_working_directory_is_recorded_even_though_the_import_ignores_it()
        => Assert.Equal(@"D:\work\repo", Info(workingDirectory: @"D:\work\repo").GetProperty("directory").GetString());

    /// <summary>A tile is recognisable in <c>opencode session list</c> without the whole GUID being the
    /// only thing on the line.</summary>
    [Fact]
    public void The_title_says_which_tile_it_belongs_to()
        => Assert.Contains(TileId[..8], Info().GetProperty("title").GetString());

    // ---- where it lives --------------------------------------------------------

    [Fact]
    public void The_document_is_named_after_the_session_it_creates()
        => Assert.Equal(OpenCodeSession.IdFor(TileId) + ".json",
            Path.GetFileName(OpenCodeSession.DocumentPath(TileId)));

    [Fact]
    public void The_document_lives_under_the_applications_own_directory()
        => Assert.StartsWith(AppPaths.GetAppDataDirectory(), OpenCodeSession.DocumentPath(TileId));

    /// <summary>
    /// The same rule as for a shell command, for the same reason at one remove: the id comes off a
    /// layout file on disk and here it becomes a file name, so anything with a separator in it writes
    /// somewhere else entirely.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData(@"..\..\settings")]
    [InlineData("x; rm -rf ~")]
    [InlineData("1b9d6bcdbbfd4b2d9b5dab8dfbbd4bed")]
    public void A_tile_id_that_is_not_a_guid_never_becomes_a_file_name(string tileId)
        => Assert.Throws<ArgumentException>(() => OpenCodeSession.DocumentPath(tileId));

    // ---- the token a profile names it with -------------------------------------

    [Fact]
    public void The_token_resolves_to_the_document_the_application_writes()
    {
        var command = TileScript.Resolve("opencode import \"${opencodeSessionFile}\"", TileId);

        Assert.Equal($"opencode import \"{OpenCodeSession.DocumentPath(TileId)}\"", command);
    }

    /// <summary>The token is built out of the tile id just as <c>${tileId}</c> is, so a tile without one
    /// cannot expand it either — and expanding it to nothing would leave <c>opencode import ""</c>,
    /// which is a command that runs and does something else.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-guid")]
    public void A_script_naming_the_document_refuses_to_run_without_a_usable_tile_id(string tileId)
        => Assert.Throws<ArgumentException>(
            () => TileScript.Resolve("opencode import \"${opencodeSessionFile}\"", tileId));

    /// <summary>Both tokens in one command, which is what the seeded profile's fallback actually is:
    /// create the session, then resume it by name.</summary>
    [Fact]
    public void The_create_then_resume_command_resolves_both_halves()
    {
        var commands = DirectLaunchSession.BuildCommands(
            LaunchScripts.FromProfile(
                "opencode --session ses_${tileId}",
                "opencode import \"${opencodeSessionFile}\" ; opencode --session ses_${tileId}"),
            TileId);

        Assert.Equal(
            [
                $"opencode --session ses_{TileId}",
                $"opencode import \"{OpenCodeSession.DocumentPath(TileId)}\" ; opencode --session ses_{TileId}",
            ],
            commands);
    }

    /// <summary>
    /// A profile that names the document gets one written, with the directory created for it — the
    /// command that reads it is the tile's own, and by the time it runs there is nobody left to write it.
    /// </summary>
    [Fact]
    public void A_profile_that_asks_for_a_document_gets_one_written()
    {
        using var temp = new TempDirectory();
        var scripts = LaunchScripts.FromProfile(
            "opencode --session ses_${tileId}",
            "opencode import \"${opencodeSessionFile}\" ; opencode --session ses_${tileId}");

        OpenCodeSession.PrepareIfReferenced(scripts, TileId, @"D:\work\repo", temp.Path);

        var written = OpenCodeSession.DocumentPath(TileId, temp.Path);
        Assert.True(File.Exists(written), "the document the fallback will import");
        // The real thing on disk, not just a file: this is what opencode is handed.
        var info = JsonDocument.Parse(File.ReadAllText(written)).RootElement.GetProperty("info");
        Assert.Equal(OpenCodeSession.IdFor(TileId), info.GetProperty("id").GetString());
        Assert.Equal(@"D:\work\repo", info.GetProperty("directory").GetString());
    }

    /// <summary>Rewritten on every launch rather than only when missing, which is what keeps a session
    /// the user deleted behind the tile's back recreatable.</summary>
    [Fact]
    public void The_document_is_rewritten_on_every_launch()
    {
        using var temp = new TempDirectory();
        var scripts = LaunchScripts.FromProfile(null, "opencode import \"${opencodeSessionFile}\"");
        var path = OpenCodeSession.DocumentPath(TileId, temp.Path);

        OpenCodeSession.PrepareIfReferenced(scripts, TileId, @"D:\first", temp.Path);
        OpenCodeSession.PrepareIfReferenced(scripts, TileId, @"D:\second", temp.Path);

        Assert.Contains(@"D:\\second", File.ReadAllText(path));
    }

    /// <summary>Only the startup script naming it is enough — the token is legal in either.</summary>
    [Fact]
    public void The_document_is_written_when_only_the_startup_script_names_it()
    {
        using var temp = new TempDirectory();

        OpenCodeSession.PrepareIfReferenced(
            LaunchScripts.FromProfile("opencode import \"${opencodeSessionFile}\"", "opencode"),
            TileId, "", temp.Path);

        Assert.True(File.Exists(OpenCodeSession.DocumentPath(TileId, temp.Path)));
    }

    /// <summary>A profile that never mentions the document is not given one — this runs on every launch
    /// of every tile, and a note tile has no business creating files for opencode.</summary>
    [Fact]
    public void A_profile_that_does_not_ask_for_a_document_gets_none()
    {
        using var temp = new TempDirectory();

        OpenCodeSession.PrepareIfReferenced(
            LaunchScripts.FromProfile("claude", "claude -r"), TileId, "", temp.Path);

        Assert.False(File.Exists(OpenCodeSession.DocumentPath(TileId, temp.Path)));
    }

    // ---- the seeded profile, against the code it depends on --------------------

    /// <summary>
    /// The profile the application actually ships, resolved as a tile would resolve it, checked against
    /// the code that has to agree with it.
    /// </summary>
    /// <remarks>
    /// Everything above tests the pieces; this is the only thing that tests that they were assembled
    /// into a working profile. Without it the production scripts are two string literals nothing reads:
    /// change opencode's prefix in <see cref="OpenCodeSession.IdFor"/>, or rename the token, or fix a
    /// typo in the seeded command, and the build stays green while every OpenCode tile quietly starts a
    /// fresh conversation — the exact failure this feature exists to prevent, and an invisible one,
    /// because a tile that resumes nothing looks like a tile that was never used.
    /// </remarks>
    [Fact]
    public void The_seeded_profile_resolves_to_commands_the_rest_of_this_code_agrees_with()
    {
        using var settings = new TempSettings();
        var profile = Assert.Single(settings.Service.Settings.ShellProfiles, p => p.Name == "OpenCode");

        var commands = DirectLaunchSession.BuildCommands(
            LaunchScripts.FromProfile(profile.StartupScript, profile.FallbackScript), TileId);

        // Startup resumes the session this tile's id names — the id as OpenCodeSession spells it, not as
        // a literal in the settings happens to spell it.
        Assert.Equal($"opencode --session {OpenCodeSession.IdFor(TileId)}", commands[0]);

        // The fallback creates it first, from the document this code writes, at the path it writes it to.
        Assert.Equal(
            $"opencode import \"{OpenCodeSession.DocumentPath(TileId)}\" "
            + $"; opencode --session {OpenCodeSession.IdFor(TileId)}",
            commands[1]);
    }

    /// <summary>And the launcher writes that document for that profile: the token in the shipped script
    /// is what <see cref="OpenCodeSession.PrepareIfReferenced"/> looks for, spelled the same way.</summary>
    [Fact]
    public void The_seeded_profile_is_one_the_launcher_writes_a_document_for()
    {
        using var settings = new TempSettings();
        using var temp = new TempDirectory();
        var profile = Assert.Single(settings.Service.Settings.ShellProfiles, p => p.Name == "OpenCode");

        OpenCodeSession.PrepareIfReferenced(
            LaunchScripts.FromProfile(profile.StartupScript, profile.FallbackScript),
            TileId, @"D:\work\repo", temp.Path);

        Assert.True(File.Exists(OpenCodeSession.DocumentPath(TileId, temp.Path)));
    }

    /// <summary>A directory of its own per test, because these write real files — and never the one the
    /// application uses, which holds the sessions of whoever is running the tests.</summary>
    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "mtiles-tests", Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
            catch { /* a temp directory nobody will look at again */ }
        }
    }
}
