using mTiles.Services;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// What happens to settings written by an older version.
/// <para>Renaming a property means the old value is not read and the new one starts at its default —
/// harmless for a font size, and not harmless when the default writes to the user's repository.</para>
/// </summary>
public sealed class SettingsMigrationTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "mtiles-tests", Guid.NewGuid().ToString("N"));

    private string SettingsPath => Path.Combine(_directory, "settings.json");

    public SettingsMigrationTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { /* a temp directory */ }
    }

    private void GivenSettings(string json) => File.WriteAllText(SettingsPath, json);

    /// <summary>
    /// The one case where the application would edit a repository against a decision the user had
    /// already made. They turned the old switch off; the feature was renamed and its meaning widened
    /// from hiding to ignoring, but "no, thank you" carries across both readings.
    /// </summary>
    [Fact]
    public void An_explicit_no_under_the_old_name_is_still_a_no()
    {
        GivenSettings("""{ "GitHideMTerminalDir": false }""");

        var service = new SettingsService(SettingsPath);

        Assert.False(service.Settings.GitIgnoreMTerminalDir);
    }

    [Fact]
    public void An_explicit_yes_under_the_old_name_stays_yes()
    {
        GivenSettings("""{ "GitHideMTerminalDir": true }""");

        Assert.True(new SettingsService(SettingsPath).Settings.GitIgnoreMTerminalDir);
    }

    /// <summary>Never having said anything is not the same as having said no: those users get the
    /// default, which is what a new installation gets too.</summary>
    [Fact]
    public void Settings_that_never_mentioned_it_take_the_default()
    {
        GivenSettings("""{ "FontSize": 13 }""");

        Assert.True(new SettingsService(SettingsPath).Settings.GitIgnoreMTerminalDir);
    }

    /// <summary>
    /// With both keys present the old one wins, and that is the intended rule rather than an accident.
    /// <para>The old key exists only in a file written by a version that did not know the new one, so
    /// its presence is what marks the file as coming from before the rename — and it is removed as soon
    /// as it has been read, so it can never override a choice made afterwards. A file holding both is
    /// only reachable by hand-editing, and there the more cautious reading wins: the one that can leave
    /// somebody's repository alone.</para>
    /// </summary>
    [Fact]
    public void With_both_keys_present_the_old_answer_wins_and_is_then_gone()
    {
        GivenSettings("""{ "GitHideMTerminalDir": false, "GitIgnoreMTerminalDir": true }""");

        Assert.False(new SettingsService(SettingsPath).Settings.GitIgnoreMTerminalDir);
        Assert.DoesNotContain("GitHideMTerminalDir", File.ReadAllText(SettingsPath));
    }

    /// <summary>Read once. The old key is gone from the file afterwards, so the migration cannot run a
    /// second time and undo a choice the user makes later.</summary>
    [Fact]
    public void The_old_key_is_dropped_once_it_has_been_read()
    {
        GivenSettings("""{ "GitHideMTerminalDir": false }""");
        _ = new SettingsService(SettingsPath);

        Assert.DoesNotContain("GitHideMTerminalDir", File.ReadAllText(SettingsPath));

        // And a run after that leaves the answer where the first one put it.
        Assert.False(new SettingsService(SettingsPath).Settings.GitIgnoreMTerminalDir);
    }
}
