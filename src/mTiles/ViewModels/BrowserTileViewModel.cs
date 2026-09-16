using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using mTiles.Models;
using mTiles.Services;
using mTiles.Services.Browser;

namespace mTiles.ViewModels;

/// <summary>Web pages in a tile, one tab at a time.</summary>
/// <remarks>
/// <para>The pages themselves live in the view — native windows the platform's engine draws — so what is
/// kept here is what outlives them: the tabs and their addresses, which is also everything the layout
/// saves. The view asks a tab where to go (<see cref="BrowserTabViewModel.NavigationRequested"/>) and
/// tells it where it went (<see cref="OnNavigated"/>), and nothing here knows the engine exists.</para>
/// <para>A page is content that is simply more of the same at a larger size, so it is
/// <see cref="IMaximizableTile"/>. The page's own full-screen button fills the tile, which is the
/// workspace once the tile has been given it.</para>
/// <para><b>There is always a tab.</b> Closing the last one opens the home page in its place: a tile with
/// no page is an empty card with an address bar that has nowhere to go.</para>
/// </remarks>
public sealed partial class BrowserTileViewModel : ObservableObject, IMaximizableTile, IDescribedTile
{
    private readonly SettingsService _settings;
    private readonly Action? _requestSave;
    private bool _disposed;

    public BrowserTileViewModel(SettingsService settings, string? address, Action? requestSave = null)
        : this(settings, address is null ? [] : [address], 0, requestSave)
    {
    }

    public BrowserTileViewModel(SettingsService settings, IReadOnlyList<string> addresses, int selected,
        Action? requestSave = null)
    {
        _settings = settings;
        _requestSave = requestSave;

        foreach (var address in addresses)
            if (BrowserAddress.Resolve(address) is { } uri && IsShowable(uri))
                Tabs.Add(new BrowserTabViewModel(uri));
        if (Tabs.Count == 0)
            Tabs.Add(new BrowserTabViewModel(Home));

        _selectedTab = Tabs[Math.Clamp(selected, 0, Tabs.Count - 1)];
        _selectedTab.IsSelected = true;
        _addressText = _selectedTab.Address.ToString();
        _selectedTab.PropertyChanged += OnSelectedTabChanged;
        _settings.SettingsChanged += OnSettingsChanged;
    }

    public string KindId => TileKindIds.Browser;

    public ObservableCollection<BrowserTabViewModel> Tabs { get; } = [];

    /// <summary>The tab on screen.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeaderNote))]
    private BrowserTabViewModel _selectedTab;

    /// <summary>The page on screen — what the layout's old single-address field is written from.</summary>
    public Uri Address => SelectedTab.Address;

    /// <summary>What the address box shows, and what Enter in it opens.</summary>
    [ObservableProperty]
    private string _addressText;

    /// <summary>Whether the pages are going out through a proxy — set by the view, which is what knows
    /// which arguments the browser process was started with.</summary>
    [ObservableProperty]
    private bool _usesProxy;

    public string HeaderNote => SelectedTab.Title;

    /// <summary>Why the pages cannot be shown, or empty while they can.</summary>
    [ObservableProperty]
    private string _problem = "";

    /// <summary>A proxy typed into Settings that the running browser has not picked up yet.</summary>
    public bool ProxyChangePending => BrowserEngine.IsBehind(_settings.Settings.Browser);

    public BrowserSettings Settings => _settings.Settings.Browser;

    private Uri Home => BrowserAddress.Home(_settings.Settings.Browser.HomePage);

    /// <summary>The control drawing the pages, kept so a tile that moves takes its pages along.</summary>
    /// <remarks>An object for the reason <c>WorkspacesTileViewModel.CachedView</c> is one. A card is
    /// rebuilt whenever its tile moves; a page built again would start over.</remarks>
    public object? CachedView { get; set; }

    /// <summary>The view reporting where a tab's page went.</summary>
    /// <remarks>An address the engine made up for itself — <c>chrome-error://</c> for a page that did not
    /// load — is not one to show or to save: saved, it would reopen as a search for its own name.</remarks>
    public void OnNavigated(BrowserTabViewModel tab, Uri address, bool canGoBack, bool canGoForward)
    {
        tab.CanGoBack = canGoBack;
        tab.CanGoForward = canGoForward;
        tab.IsLoading = false;
        if (!IsShowable(address))
            return;

        tab.Address = address;
        if (tab == SelectedTab)
            AddressText = address.ToString();
        _requestSave?.Invoke();
    }

    private static bool IsShowable(Uri address) =>
        address.Scheme is "http" or "https" or "about" or "file";

    partial void OnSelectedTabChanged(BrowserTabViewModel? oldValue, BrowserTabViewModel newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsSelected = false;
            oldValue.PropertyChanged -= OnSelectedTabChanged;
        }

        newValue.IsSelected = true;
        newValue.PropertyChanged += OnSelectedTabChanged;
        AddressText = newValue.Address.ToString();
        _requestSave?.Invoke();
    }

    private void OnSelectedTabChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BrowserTabViewModel.Title))
            OnPropertyChanged(nameof(HeaderNote));
    }

    [RelayCommand]
    private void Go()
    {
        if (BrowserAddress.Resolve(AddressText) is { } target)
        {
            AddressText = target.ToString();
            SelectedTab.RequestNavigation(target);
        }
    }

    [RelayCommand]
    private void GoHome()
    {
        AddressText = Home.ToString();
        SelectedTab.RequestNavigation(Home);
    }

    [RelayCommand]
    private void Back() => SelectedTab.RequestHistory(-1);

    [RelayCommand]
    private void Forward() => SelectedTab.RequestHistory(1);

    [RelayCommand]
    private void Reload() => SelectedTab.RequestHistory(0);

    [RelayCommand]
    private void NewTab() => OpenTab(Home);

    /// <summary>Opens <paramref name="address"/> in a new tab beside the current one, and shows it.</summary>
    public BrowserTabViewModel OpenTab(Uri address)
    {
        var tab = new BrowserTabViewModel(address);
        Tabs.Insert(Tabs.IndexOf(SelectedTab) + 1, tab);
        SelectedTab = tab;
        return tab;
    }

    [RelayCommand]
    private void SelectTab(BrowserTabViewModel? tab)
    {
        if (tab is not null && Tabs.Contains(tab))
            SelectedTab = tab;
    }

    [RelayCommand]
    private void CloseTab(BrowserTabViewModel? tab)
    {
        tab ??= SelectedTab;
        var index = Tabs.IndexOf(tab);
        if (index < 0) return;

        if (Tabs.Count == 1)
            Tabs.Add(new BrowserTabViewModel(Home));

        if (tab == SelectedTab)
            SelectedTab = Tabs[index + 1 < Tabs.Count ? index + 1 : index - 1];

        Tabs.RemoveAt(index);
        tab.Detach();
        _requestSave?.Invoke();
    }

    /// <summary>Moves to the next tab, wrapping round, or the previous one.</summary>
    [RelayCommand]
    private void CycleTab(int direction)
    {
        if (Tabs.Count < 2) return;
        var index = (Tabs.IndexOf(SelectedTab) + Math.Sign(direction) + Tabs.Count) % Tabs.Count;
        SelectedTab = Tabs[index];
    }

    private void OnSettingsChanged() => OnPropertyChanged(nameof(ProxyChangePending));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _settings.SettingsChanged -= OnSettingsChanged;
        SelectedTab.PropertyChanged -= OnSelectedTabChanged;
        Disposed?.Invoke();
        foreach (var tab in Tabs)
            tab.Detach();
    }

    /// <summary>The tile has closed: the view lets go of the pages now rather than when it is collected.</summary>
    public event Action? Disposed;
}
