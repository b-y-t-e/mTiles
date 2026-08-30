using System;
using System.IO;
using mTiles.Services;
using mTiles.ViewModels;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// What the key field promises is what the file actually gets.
/// </summary>
/// <remarks>
/// The placeholder said "stored encrypted on this machine" everywhere, while
/// <see cref="ProtectedStringConverter"/> encrypts only on Windows. A user weighing whether to paste an
/// OpenRouter key into a settings file reads that sentence, so it has to follow the platform rather than
/// the hope.
/// </remarks>
public class SecretStorageTests
{
    [Fact]
    public void Encryption_is_claimed_only_where_there_is_some()
    {
        Assert.Equal(OperatingSystem.IsWindows(), SecretStorage.IsEncrypted);
        Assert.Equal(SecretStorage.IsEncrypted, SecretStorage.KeyFieldHint.Contains("encrypted"));
    }

    [Fact]
    public void A_platform_without_encryption_says_so_on_the_page()
    {
        Assert.Equal(!SecretStorage.IsEncrypted, SecretStorage.HasWarning);
        Assert.Equal(SecretStorage.HasWarning, SecretStorage.Warning is not null);
        Assert.Equal(SecretStorage.Warning ?? "", SettingsViewModel.SecretStorageWarning);
        Assert.Equal(SecretStorage.KeyFieldHint, SettingsViewModel.SecretFieldHint);
        Assert.Equal(SecretStorage.HasWarning, SettingsViewModel.ShowsPlainSecretWarning);
    }

    /// <summary>The export note cannot claim an encryption this platform does not perform either.</summary>
    [Fact]
    public void The_export_note_follows_the_same_platform()
    {
        Assert.Equal(SecretStorage.IsEncrypted,
            SettingsViewModel.ExportSecretsNote.Contains("encrypted"));
        Assert.Contains("not exported", SettingsViewModel.ExportSecretsNote);
    }

    [Fact]
    public void A_private_file_holds_what_was_written_and_is_owner_only()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mtiles-secret-{Guid.NewGuid():N}.json");
        try
        {
            PrivateFile.WriteAllText(path, "{\"key\":\"secret\"}");
            Assert.Equal("{\"key\":\"secret\"}", File.ReadAllText(path));

            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    File.GetUnixFileMode(path));
            }
        }
        finally
        {
            try { File.Delete(path); } catch { /* the temp directory can keep it */ }
        }
    }
}
