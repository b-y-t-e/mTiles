using mTiles.Models;
using mTiles.Services.Shells;
using mTiles.Views;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The registry, and the one rule that has to survive a settings file written by an older build:
/// what a stored shell name resolves to.
/// </summary>
public class ShellCatalogTests
{
    /// <summary>Ids are what settings and layouts store, so two shells sharing one is a tile that comes
    /// back as the wrong shell — and a rename is a silent reset to the default.</summary>
    [Fact]
    public void Every_shell_has_its_own_id()
    {
        var ids = ShellTerminalCatalog.All.Select(s => s.Id).ToList();

        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(["powershell", "gitbash", "bash", "zsh", "fish"], ids);
    }

    /// <summary>A stored name is an id on a file this build wrote and a display name on an older one,
    /// and both have to find the same shell — the alternative is every existing tile and profile
    /// quietly falling back to the default.</summary>
    [Theory]
    [InlineData("powershell", "powershell")]
    [InlineData("PowerShell", "powershell")]        // as an older build wrote it
    [InlineData("Git Bash", "gitbash")]
    [InlineData("gitbash", "gitbash")]
    [InlineData("bash", "bash")]
    [InlineData("ZSH", "zsh")]
    public void A_stored_shell_name_finds_its_shell(string stored, string expectedId)
        => Assert.Equal(expectedId, ShellTerminalCatalog.Find(stored)?.Id);

    /// <summary><c>cmd</c> is gone, and this is what that means in practice: the name a settings file
    /// may still hold answers to nothing, so the caller falls back to the default shell.</summary>
    [Theory]
    [InlineData("CMD")]
    [InlineData("cmd")]
    [InlineData("Custom...")]
    [InlineData("")]
    [InlineData(null)]
    public void A_name_nothing_answers_to_is_not_a_shell(string? stored)
        => Assert.Null(ShellTerminalCatalog.Find(stored));

    private static readonly ShellInstallation Pwsh = new(new PowerShellTerminal(), "pwsh");
    private static readonly ShellInstallation Bash = new(new BashTerminal(), "/bin/bash");

    [Fact]
    public void The_default_is_the_shell_the_settings_name()
    {
        var settings = new AppSettings { DefaultShellName = "bash" };

        Assert.Equal("bash", ShellTerminalCatalog.ResolveDefault(settings, [Pwsh, Bash]).Id);
    }

    /// <summary>Named but not installed — a machine that lost PowerShell, or a settings file carried to
    /// another OS. The first detected shell is a shell; the named one is nothing.</summary>
    [Fact]
    public void A_default_that_is_not_installed_falls_back_to_one_that_is()
    {
        var settings = new AppSettings { DefaultShellName = "fish" };

        Assert.Equal("bash", ShellTerminalCatalog.ResolveDefault(settings, [Bash]).Id);
    }

    /// <summary>Nothing detected at all is not a state a machine reaches in practice, and not one to
    /// leave a tile dead in either: the answer is still a shell, with a name <c>PATH</c> may resolve.</summary>
    [Fact]
    public void With_nothing_detected_there_is_still_a_shell()
    {
        var shell = ShellTerminalCatalog.ResolveDefault(new AppSettings(), []);

        Assert.NotNull(shell.Shell);
        Assert.NotEqual("", shell.ExecutablePath);
    }

    /// <summary>A saved tile names its shell; an uninstalled one falls to the default rather than
    /// leaving the tile without one.</summary>
    [Fact]
    public void A_saved_tiles_shell_is_resolved_and_falls_back_when_it_is_gone()
    {
        var settings = new AppSettings { DefaultShellName = "bash" };

        Assert.Equal("powershell", ShellTerminalCatalog.Resolve("PowerShell", [Pwsh, Bash], settings).Id);
        Assert.Equal("bash", ShellTerminalCatalog.Resolve("CMD", [Pwsh, Bash], settings).Id);
    }

    /// <summary>Every shell's icon name is one the icon set actually knows. It falls back rather than
    /// throwing, so a typo here is a wrong glyph in the chooser and nothing that fails.</summary>
    [Fact]
    public void Every_shell_names_an_icon_the_application_can_draw()
    {
        foreach (var shell in ShellTerminalCatalog.All)
            Assert.NotEqual(TileIcons.Placeholder, TileIcons.Kind(shell.IconId));
    }
}
