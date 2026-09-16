using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using mTiles.Services.Browser;

namespace mTiles.ViewModels;

/// <summary>Settings → General → Browser: the home page, the proxy, and this machine's relay.</summary>
public partial class SettingsViewModel
{
    private BrowserRelay? _browserRelay;

    [ObservableProperty] private string _browserHomePage = "";
    [ObservableProperty] private string _browserProxy = "";
    [ObservableProperty] private bool _browserRelayEnabled;
    [ObservableProperty] private double _browserRelayPort;
    [ObservableProperty] private bool _browserSecureDns;

    /// <summary>Why the proxy field is refused, or empty.</summary>
    [ObservableProperty] private string _browserProxyProblem = "";

    public bool BrowserProxyIsSupported => OperatingSystem.IsWindows();

    public string BrowserRelayStatus => _browserRelay?.Status ?? "Unavailable.";

    /// <summary>What another machine types into its proxy field.</summary>
    public string BrowserRelayAddresses => string.Join("\n", _browserRelay?.Addresses ?? []);

    public bool HasBrowserRelayAddresses => _browserRelay?.Addresses.Count > 0;

    /// <summary>Reads the section into the form. Writes the fields, not the properties, for the reason
    /// <c>InitializeSpeech</c> gives: the setters write back, and this only ever reads.</summary>
    private void InitializeBrowser(BrowserRelay? relay)
    {
        if (relay is not null && _browserRelay is null)
        {
            _browserRelay = relay;
            relay.Changed += () => Dispatcher.UIThread.Post(() =>
            {
                OnPropertyChanged(nameof(BrowserRelayStatus));
                OnPropertyChanged(nameof(BrowserRelayAddresses));
                OnPropertyChanged(nameof(HasBrowserRelayAddresses));
            });
        }

        var browser = _settingsService.Settings.Browser;
#pragma warning disable MVVMTK0034
        _browserHomePage = browser.HomePage;
        _browserProxy = browser.ProxyServer;
        _browserRelayEnabled = browser.RelayEnabled;
        _browserRelayPort = browser.RelayPort;
        _browserSecureDns = browser.SecureDns;
        Services.Browser.BrowserProxy.Normalise(browser.ProxyServer, out var problem);
        _browserProxyProblem = problem ?? "";
#pragma warning restore MVVMTK0034
    }

    partial void OnBrowserHomePageChanged(string value)
    {
        _settingsService.Settings.Browser.HomePage = value;
        _settingsService.NotifyChanged();
    }

    partial void OnBrowserProxyChanged(string value)
    {
        _settingsService.Settings.Browser.ProxyServer = value;
        Services.Browser.BrowserProxy.Normalise(value, out var problem);
        BrowserProxyProblem = problem ?? "";
        _settingsService.NotifyChanged();
    }

    partial void OnBrowserRelayEnabledChanged(bool value)
    {
        _settingsService.Settings.Browser.RelayEnabled = value;
        _settingsService.NotifyChanged();
    }

    partial void OnBrowserSecureDnsChanged(bool value)
    {
        _settingsService.Settings.Browser.SecureDns = value;
        _settingsService.NotifyChanged();
    }

    // The spinner clears to 0 while it is being retyped; a port of 0 is not one to listen on.
    partial void OnBrowserRelayPortChanged(double value)
    {
        if (value is < 1 or > 65535) return;
        _settingsService.Settings.Browser.RelayPort = (int)value;
        _settingsService.NotifyChanged();
    }

    [RelayCommand]
    private void RetryBrowserRelay() => _browserRelay?.Restart();

    /// <summary>What the last proxy test said, and whether it passed (null before one has run).</summary>
    [ObservableProperty] private string _browserProxyTestResult = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BrowserProxyTestPassed), nameof(BrowserProxyTestFailed))]
    private bool? _browserProxyTestOk;

    public bool BrowserProxyTestPassed => BrowserProxyTestOk == true;
    public bool BrowserProxyTestFailed => BrowserProxyTestOk == false;
    [ObservableProperty] private bool _isTestingBrowserProxy;

    [RelayCommand]
    private async Task TestBrowserProxyAsync()
    {
        if (IsTestingBrowserProxy) return;
        IsTestingBrowserProxy = true;
        BrowserProxyTestOk = null;
        BrowserProxyTestResult = "Testing…";
        try
        {
            var line = await BrowserConnectivity.TestProxyAsync(BrowserProxy);
            BrowserProxyTestOk = line.Ok;
            BrowserProxyTestResult = line.Text;
        }
        finally
        {
            IsTestingBrowserProxy = false;
        }
    }

    /// <summary>The last relay check, one line per condition.</summary>
    public System.Collections.ObjectModel.ObservableCollection<ConnectivityLine> BrowserRelayCheck { get; } = [];

    [ObservableProperty] private bool _isCheckingBrowserRelay;
    [ObservableProperty] private string _browserFirewallResult = "";

    public bool CanChangeFirewall => RelayFirewall.IsSupported;

    [RelayCommand]
    private async Task CheckBrowserRelayAsync()
    {
        if (_browserRelay is null || IsCheckingBrowserRelay) return;
        IsCheckingBrowserRelay = true;
        BrowserRelayCheck.Clear();
        BrowserRelayCheck.Add(new ConnectivityLine(true, "Checking…"));
        try
        {
            var lines = await BrowserConnectivity.CheckRelayAsync(_browserRelay,
                _settingsService.Settings.Browser.RelayPort, _settingsService.Settings.Browser.RelayEnabled);
            BrowserRelayCheck.Clear();
            foreach (var line in lines)
                BrowserRelayCheck.Add(line);
        }
        finally
        {
            IsCheckingBrowserRelay = false;
        }
    }

    [RelayCommand]
    private async Task AllowBrowserRelayInFirewallAsync()
    {
        BrowserFirewallResult = "Waiting for the administrator prompt…";
        var (_, message) = await RelayFirewall.AllowAsync(_settingsService.Settings.Browser.RelayPort);
        BrowserFirewallResult = message;
    }
}
