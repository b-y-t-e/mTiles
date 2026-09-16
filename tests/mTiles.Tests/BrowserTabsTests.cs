using System.Text.Json.Nodes;
using mTiles.Models;
using mTiles.Services.Browser;
using mTiles.Services.Tiles;
using mTiles.ViewModels;
using Xunit;

namespace mTiles.Tests;

/// <summary>A browser tile's tabs: what is always true of them, and what the layout keeps.</summary>
public class BrowserTabsTests
{
    [Fact]
    public void A_new_tile_has_one_tab_on_the_home_page()
    {
        using var settings = new TempSettings();
        settings.Service.Settings.Browser.HomePage = "example.org";
        using var tile = new BrowserTileViewModel(settings.Service, (string?)null);

        var tab = Assert.Single(tile.Tabs);
        Assert.Same(tab, tile.SelectedTab);
        Assert.True(tab.IsSelected);
        Assert.Equal("https://example.org/", tile.AddressText);
    }

    [Fact]
    public void A_new_tab_opens_beside_the_current_one_and_is_shown()
    {
        using var settings = new TempSettings();
        using var tile = new BrowserTileViewModel(settings.Service, ["a.com", "b.com"], 0);

        var opened = tile.OpenTab(new Uri("https://c.com/"));

        Assert.Equal(["https://a.com/", "https://c.com/", "https://b.com/"],
            tile.Tabs.Select(tab => tab.Address.ToString()));
        Assert.Same(opened, tile.SelectedTab);
        Assert.Equal("https://c.com/", tile.AddressText);
        Assert.Single(tile.Tabs, tab => tab.IsSelected);
    }

    [Fact]
    public void Closing_the_selected_tab_shows_its_neighbour()
    {
        using var settings = new TempSettings();
        using var tile = new BrowserTileViewModel(settings.Service, ["a.com", "b.com", "c.com"], 2);

        tile.CloseTabCommand.Execute(null);

        Assert.Equal(2, tile.Tabs.Count);
        Assert.Equal("https://b.com/", tile.SelectedTab.Address.ToString());
    }

    [Fact]
    public void Closing_the_last_tab_leaves_the_home_page()
    {
        using var settings = new TempSettings();
        settings.Service.Settings.Browser.HomePage = "home.example";
        using var tile = new BrowserTileViewModel(settings.Service, ["a.com"], 0);

        tile.CloseTabCommand.Execute(tile.SelectedTab);

        var tab = Assert.Single(tile.Tabs);
        Assert.Equal("https://home.example/", tab.Address.ToString());
        Assert.Same(tab, tile.SelectedTab);
    }

    [Fact]
    public void Cycling_wraps_round()
    {
        using var settings = new TempSettings();
        using var tile = new BrowserTileViewModel(settings.Service, ["a.com", "b.com"], 1);

        tile.CycleTabCommand.Execute(1);
        Assert.Equal("https://a.com/", tile.SelectedTab.Address.ToString());

        tile.CycleTabCommand.Execute(-1);
        Assert.Equal("https://b.com/", tile.SelectedTab.Address.ToString());
    }

    [Fact]
    public void An_engine_error_page_is_neither_shown_nor_kept()
    {
        using var settings = new TempSettings();
        using var tile = new BrowserTileViewModel(settings.Service, ["a.com"], 0);

        tile.OnNavigated(tile.SelectedTab, new Uri("chrome-error://chromewebdata/"), false, false);

        Assert.Equal("https://a.com/", tile.SelectedTab.Address.ToString());
        Assert.Equal("https://a.com/", tile.AddressText);
    }

    [Fact]
    public void The_tabs_survive_a_save_and_a_restore()
    {
        using var settings = new TempSettings();
        var kind = (ITileKind)new BrowserTileKind();
        var context = new TileContext(Path.GetTempPath(), settings.Service);
        using var tile = (BrowserTileViewModel)kind.Create(context,
            new JsonObject { ["tabs"] = new JsonArray("a.com", "b.com"), ["selectedTab"] = 1 });

        var saved = kind.Save(tile)!;
        using var restored = (BrowserTileViewModel)kind.Create(context, saved);

        Assert.Equal("https://b.com/", saved["address"]!.GetValue<string>());
        Assert.Equal(["https://a.com/", "https://b.com/"], restored.Tabs.Select(tab => tab.Address.ToString()));
        Assert.Equal(1, restored.Tabs.IndexOf(restored.SelectedTab));
    }

    [Fact]
    public void A_layout_from_before_tabs_opens_on_its_one_page()
    {
        using var settings = new TempSettings();
        var kind = (ITileKind)new BrowserTileKind();
        using var tile = (BrowserTileViewModel)kind.Create(
            new TileContext(Path.GetTempPath(), settings.Service), new JsonObject { ["address"] = "https://old.example/" });

        Assert.Equal("https://old.example/", Assert.Single(tile.Tabs).Address.ToString());
    }

    [Fact]
    public void The_relay_rule_admits_the_tailnet_only_and_names_its_port()
    {
        var script = RelayFirewall.RepairScript(@"C:\Program Files\It's mTiles\mTiles.exe", 18093);

        Assert.Contains("-LocalPort 18093", script);
        Assert.Contains("-RemoteAddress 100.64.0.0/10,fd7a:115c:a1e0::/48", script);
        Assert.Contains("It''s mTiles", script);
        Assert.Contains($"-DisplayName '{RelayFirewall.RuleName}'", script);
        // Only block rules are taken away; the phone bridge's allow rule stays.
        Assert.Contains("$_.Action -eq 'Block'", script);
    }

    [Fact]
    public void The_phone_repair_leaves_the_relay_rule_alone()
    {
        var script = mTiles.Services.Phone.WindowsFirewallGuide.BuildScript(@"C:\mTiles.exe");

        Assert.Contains($"$_.DisplayName -ne '{RelayFirewall.RuleName}'", script);
        Assert.Contains($"$_.DisplayName -ne '{RelayFirewall.RuleName}'",
            mTiles.Services.Phone.WindowsFirewallGuide.BuildCheckScript(@"C:\mTiles.exe"));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(RelayFirewall.NoRule, false)]
    [InlineData(RelayFirewall.Blocked, false)]
    [InlineData(RelayFirewall.WrongPort, false)]
    [InlineData(RelayFirewall.PolicyIgnoresLocalRules, false)]
    [InlineData(RelayFirewall.CheckFailed, false)]
    [InlineData(-1, false)]
    public void Every_firewall_answer_is_a_sentence(int code, bool ok)
    {
        var (passed, message) = RelayFirewall.Describe(code, 18093, afterRepair: false);

        Assert.Equal(ok, passed);
        Assert.StartsWith("Firewall:", message);
    }

    [Fact]
    public async Task No_proxy_is_said_rather_than_tested()
    {
        var line = await BrowserConnectivity.TestProxyAsync("");

        Assert.False(line.Ok);
        Assert.Contains("No proxy", line.Text);
    }
}
