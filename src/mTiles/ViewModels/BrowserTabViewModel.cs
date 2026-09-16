using CommunityToolkit.Mvvm.ComponentModel;

namespace mTiles.ViewModels;

/// <summary>One page of a browser tile.</summary>
/// <remarks>What outlives the page's native window, the same split <see cref="BrowserTileViewModel"/>
/// describes: the view draws the page and reports back, this remembers where it is.</remarks>
public sealed partial class BrowserTabViewModel : ObservableObject
{
    public BrowserTabViewModel(Uri address)
    {
        _address = address;
    }

    /// <summary>The page this tab is on.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label))]
    private Uri _address;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label))]
    private string _title = "";

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _canGoBack;

    [ObservableProperty]
    private bool _canGoForward;

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>What the tab strip shows: the title, or the host until the page has one.</summary>
    public string Label => Title.Length > 0 ? Title
        : Address.Host.Length > 0 ? Address.Host
        : "New tab";

    /// <summary>Asks the view to open an address in this tab.</summary>
    public event Action<Uri>? NavigationRequested;

    /// <summary>Asks the view to go back, forward or reload this tab: -1, +1 or 0.</summary>
    public event Action<int>? HistoryRequested;

    internal void RequestNavigation(Uri target)
    {
        Address = target;
        IsLoading = true;
        NavigationRequested?.Invoke(target);
    }

    internal void RequestHistory(int direction) => HistoryRequested?.Invoke(direction);

    internal void Detach()
    {
        NavigationRequested = null;
        HistoryRequested = null;
    }
}
