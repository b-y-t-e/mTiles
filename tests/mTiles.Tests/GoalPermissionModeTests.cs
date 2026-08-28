using System.Text.Json;
using mTiles.Models;
using mTiles.Services;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The permission mode as a value that is written to a file, shown in a combo box and handed to
/// somebody else's CLI — three contracts, each with its own way of going quietly wrong.
/// </summary>
public class GoalPermissionModeTests
{
    /// <summary>
    /// A mode this build has never heard of must not cost the user their settings file.
    /// </summary>
    /// <remarks>
    /// The route in is a downgrade: pick <c>bypassPermissions</c>, let Velopack roll back a version,
    /// and the name in the file is unknown to the running build. Without the tolerant converter that is
    /// a <c>JsonException</c>, which <c>SettingsService.Load</c> rightly reads as a damaged file and
    /// sets aside — taking the shell profiles, the AI tool paths and the DPAPI-encrypted database
    /// passwords with it, over one word describing one tile's appetite for unattended edits.
    /// </remarks>
    [Theory]
    [InlineData("\"somethingFromTheFuture\"")]
    [InlineData("99")]
    [InlineData("null")]
    [InlineData("{}")]
    public void An_unknown_permission_mode_reads_as_the_default_rather_than_throwing(string written)
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(
            $$"""{ "GoalPermissionMode": {{written}}, "GitPath": "kept" }""", JsonDefaults.SettingsOptions)!;

        Assert.Equal(AiPermissionMode.Auto, settings.GoalPermissionMode);

        // And the rest of the file survives, which is the whole point of not throwing.
        Assert.Equal("kept", settings.GitPath);
    }

    [Fact]
    public void A_mode_this_build_does_know_is_read_as_itself()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(
            """{ "GoalPermissionMode": "BypassPermissions" }""", JsonDefaults.SettingsOptions)!;

        Assert.Equal(AiPermissionMode.BypassPermissions, settings.GoalPermissionMode);
    }

    /// <summary>
    /// Every label maps back to the mode it came from.
    /// </summary>
    /// <remarks>
    /// The combo box binds by <em>string</em>, and <c>FromLabel</c> falls back to <c>Auto</c> without
    /// complaining. A typo in one label would therefore degrade that choice silently: pick "bypass",
    /// get "auto", with nothing anywhere saying so — and the one mode where the difference matters most
    /// is the one whose label is least like its name.
    /// </remarks>
    [Fact]
    public void Every_label_round_trips_to_its_own_mode()
    {
        foreach (var mode in AiPermissionModes.All)
            Assert.Equal(mode, AiPermissionModes.FromLabel(AiPermissionModes.Label(mode)));
    }

    /// <summary>The flags are somebody else's CLI contract, spelled in one place so a test can state
    /// them. <c>ToolDefault</c> passes no flag at all, which is not the same as passing an empty one.
    /// </summary>
    [Fact]
    public void The_flags_are_the_ones_the_tool_accepts()
    {
        Assert.Equal("auto", AiPermissionModes.Flag(AiPermissionMode.Auto));
        Assert.Equal("acceptEdits", AiPermissionModes.Flag(AiPermissionMode.AcceptEdits));
        Assert.Equal("bypassPermissions", AiPermissionModes.Flag(AiPermissionMode.BypassPermissions));
        Assert.Null(AiPermissionModes.Flag(AiPermissionMode.ToolDefault));
    }

    [Theory]
    // What an older Claude Code prints for a mode it has never heard of. Every run of the tile fails
    // this way, on the default setting, and the old message named no cause at all.
    [InlineData("error: option '--permission-mode <mode>' argument 'auto' is invalid. Allowed choices are default, acceptEdits.")]
    [InlineData("Unknown value for --permission-mode")]
    [InlineData("usage: claude [options]\n  --permission-mode <mode>")]
    public void A_rejected_mode_is_recognised(string output) =>
        Assert.True(AiPermissionModes.LooksLikeRejectedMode(output, "--permission-mode", "--effort"));

    /// <summary>
    /// A tool that refuses one flag prints the usage for all of them, and only one of them was refused.
    /// </summary>
    /// <remarks>
    /// The whole reason the match is per line. Both matchers used to scan the whole of stdout for
    /// "this flag appears" and "a word like unknown appears", so a full usage dump — which lists every
    /// option the tool has — satisfied both, and whichever was asked first took the blame. The advice
    /// then named a setting that had nothing to do with the failure while the one that did went
    /// unmentioned.
    /// </remarks>
    [Fact]
    public void A_usage_dump_blames_the_flag_the_error_line_names_and_no_other()
    {
        const string refusedEffort = """
            error: unknown option '--effort'
            usage: claude [options]
              --permission-mode <mode>   Permission mode to use for the session
              --effort <level>           Effort level for the current session
            """;

        Assert.True(AiEfforts.LooksLikeRejectedEffort(refusedEffort, "--effort", "--permission-mode"));
        Assert.False(AiPermissionModes.LooksLikeRejectedMode(refusedEffort, "--permission-mode", "--effort"));

        const string refusedMode = """
            error: unknown option '--permission-mode'
            usage: claude [options]
              --permission-mode <mode>   Permission mode to use for the session
              --effort <level>           Effort level for the current session
            """;

        Assert.True(AiPermissionModes.LooksLikeRejectedMode(refusedMode, "--permission-mode", "--effort"));
        Assert.False(AiEfforts.LooksLikeRejectedEffort(refusedMode, "--effort", "--permission-mode"));
    }

    [Theory]
    // The failure was about the work. Naming the permission mode here would send somebody to a setting
    // that cannot help, which is worse than the generic sentence.
    [InlineData("Error: ENOENT: no such file or directory, open 'src/Cart.cs'")]
    [InlineData("Claude requested permissions to use Edit, but you haven't granted it yet.")]
    [InlineData("")]
    [InlineData(null)]
    public void An_ordinary_failure_is_not_blamed_on_the_permission_mode(string? output) =>
        Assert.False(AiPermissionModes.LooksLikeRejectedMode(output, "--permission-mode", "--effort"));
}
