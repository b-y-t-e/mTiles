using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform;
using Avalonia.Threading;
using mTiles.Services;
using mTiles.Services.Browser;
using mTiles.ViewModels;

namespace mTiles.Views;

/// <summary>Draws a <see cref="BrowserTileViewModel"/>: tabs and an address bar over the platform's own
/// web views, one per tab.</summary>
/// <remarks>
/// <para><b>One view per tile, kept on the tile.</b> A card is rebuilt whenever its tile moves, and a
/// <see cref="NativeWebView"/> built again is a page loaded again, and whatever was playing starts over.
/// <c>App.BuildTileCatalog</c> hands the tile the view it already has, and the web views survive being
/// moved because Avalonia destroys a native control only when it is still detached a moment later.</para>
/// <para><b>A page is a native window.</b> Nothing Avalonia draws can be drawn over it, so the pages are
/// hidden while a dialog is open (<see cref="ModalScope.Changed"/>) — otherwise Settings would open
/// underneath them. A tab in the background is hidden the same way and keeps running, as it would in
/// any browser.</para>
/// <para><b>Input inside a page never reaches this window</b>, which is why each page is given a small
/// script (<see cref="PageScript"/>) that passes the tile's own gestures back to it.</para>
/// </remarks>
public partial class BrowserTileView : UserControl
{
    /// <summary>What a page is given after every load. Idempotent: a second run finds its own mark.</summary>
    /// <remarks>
    /// <c>invokeCSharpAction</c> is defined by the web view before any page script runs, on every
    /// engine it supports. The title and address are reported on change rather than on load, because a
    /// single-page site is one load and a thousand in-page navigations.
    /// </remarks>
    internal const string PageScript = """
        (function () {
          if (window.__mtiles || typeof invokeCSharpAction !== 'function') return;
          window.__mtiles = true;
          var send = function (m) { try { invokeCSharpAction(m); } catch (e) {} };
          window.addEventListener('keydown', function (e) {
            if (!e.ctrlKey || e.altKey || e.metaKey) return;
            var k = (e.key || '').toLowerCase(), m = null;
            if (e.shiftKey && k === 'f') m = 'maximize';
            else if (!e.shiftKey && k === 't') m = 'newtab';
            else if (!e.shiftKey && k === 'w') m = 'closetab';
            else if (k === 'tab') m = e.shiftKey ? 'prevtab' : 'nexttab';
            if (m) { e.preventDefault(); e.stopPropagation(); send('mtiles:' + m); }
          }, true);
          var last = 0;
          var middle = function (e) {
            if (e.button !== 1) return;
            e.preventDefault(); e.stopPropagation();
            if (e.type !== 'mousedown') return;
            var now = Date.now();
            if (now - last < 500) { last = 0; send('mtiles:close'); } else { last = now; }
          };
          ['mousedown', 'mouseup', 'auxclick'].forEach(function (t) { window.addEventListener(t, middle, true); });
          var title = null, href = null;
          var report = function () {
            if (document.title !== title) { title = document.title; send('mtiles:title:' + title); }
            if (location.href !== href) { href = location.href; send('mtiles:url:' + href); }
          };
          report();
          setInterval(report, 1000);
        })();
        """;

    private const string MessagePrefix = "mtiles:";

    private readonly Dictionary<BrowserTabViewModel, NativeWebView> _pages = [];
    private BrowserTileViewModel? _vm;
    private string? _arguments;
    private bool _acquired;

    public BrowserTileView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        AddressBox.KeyDown += OnAddressKeyDown;
        KeyDown += OnViewKeyDown;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm is not null || DataContext is not BrowserTileViewModel vm)
            return;

        _vm = vm;
        _arguments = BrowserEngine.Acquire(vm.Settings);
        _acquired = true;
        // The shield stands for the proxy, not for secure DNS: the two say different things and only the
        // proxy changes where the connection leaves from.
        vm.UsesProxy = _arguments is not null && BrowserProxy.ProxyArgument(vm.Settings.ProxyServer) is not null;
        vm.Disposed += OnTileDisposed;
        vm.PropertyChanged += OnTilePropertyChanged;
        vm.Tabs.CollectionChanged += OnTabsChanged;

        foreach (var tab in vm.Tabs)
            AddPage(tab);

        ModalScope.Changed += OnModalChanged;
        ApplyVisibility();
    }

    private void AddPage(BrowserTabViewModel tab)
    {
        if (_pages.ContainsKey(tab)) return;

        var page = new NativeWebView();
        page.EnvironmentRequested += OnEnvironmentRequested;
        page.AdapterCreated += (_, _) => ReportEngine(page);
        page.NavigationStarted += (_, _) => tab.IsLoading = true;
        page.NavigationCompleted += (_, e) => OnNavigationCompleted(tab, page, e);
        page.NewWindowRequested += (_, e) => OnNewWindowRequested(e);
        page.WebMessageReceived += (_, e) => OnWebMessageReceived(tab, page, e);
        tab.NavigationRequested += target => Navigate(page, target);
        tab.HistoryRequested += direction => History(page, direction);

        _pages[tab] = page;
        PageHost.Children.Add(page);
        page.Navigate(tab.Address);
        ApplyVisibility();
    }

    private void RemovePage(BrowserTabViewModel tab)
    {
        if (!_pages.Remove(tab, out var page)) return;
        // Out of the tree now, which is what ends the page — and its sound — at once.
        PageHost.Children.Remove(page);
    }

    private void OnTabsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_vm is null) return;

        foreach (var gone in _pages.Keys.Where(tab => !_vm.Tabs.Contains(tab)).ToList())
            RemovePage(gone);
        foreach (var tab in _vm.Tabs)
            AddPage(tab);
    }

    private void OnTilePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BrowserTileViewModel.SelectedTab))
            ApplyVisibility();
    }

    private void ReportEngine(NativeWebView page)
    {
        if (_vm is not null && page.AdapterInfo is DetailedWebViewAdapterInfo { IsSupported: false } info)
            _vm.Problem = $"No web engine is available here: {info.UnavailableReason}";
    }

    private void OnEnvironmentRequested(object? sender, WebViewEnvironmentRequestedEventArgs e)
    {
        var profile = BrowserEngine.ProfileDirectory;
        try { Directory.CreateDirectory(profile); }
        catch (Exception ex) { Trace.TraceWarning("Browser: creating {0} failed: {1}", profile, ex.Message); }

        switch (e)
        {
            case WindowsWebView2EnvironmentRequestedEventArgs webView2:
                webView2.UserDataFolder = profile;
                webView2.AdditionalBrowserArguments = _arguments;
                break;
            case GtkWebViewEnvironmentRequestedEventArgs gtk:
                gtk.BaseDataDirectory = Path.Combine(profile, "data");
                gtk.BaseCacheDirectory = Path.Combine(profile, "cache");
                break;
        }
    }

    private static void Navigate(NativeWebView page, Uri target)
    {
        if (page.Source == target)
            page.Refresh();
        else
            page.Navigate(target);
    }

    private static void History(NativeWebView page, int direction)
    {
        _ = direction switch
        {
            < 0 => page.GoBack(),
            > 0 => page.GoForward(),
            _ => page.Refresh(),
        };
    }

    private async void OnNavigationCompleted(BrowserTabViewModel tab, NativeWebView page,
        WebViewNavigationCompletedEventArgs e)
    {
        if (_vm is null) return;

        if (e.Request is { } address)
            _vm.OnNavigated(tab, address, page.CanGoBack, page.CanGoForward);
        else
            tab.IsLoading = false;

        try
        {
            await page.InvokeScript(PageScript);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Browser: the page script could not run: {0}", ex.Message);
        }
    }

    /// <summary>A link that asks for a new window opens in a new tab: a popup window outside the layout is
    /// the one thing this tile must never produce.</summary>
    private void OnNewWindowRequested(WebViewNewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (e.Request is { } target && _vm is not null)
            _vm.OpenTab(target);
    }

    private void OnWebMessageReceived(BrowserTabViewModel tab, NativeWebView page, WebMessageReceivedEventArgs e)
    {
        if (_vm is null || e.Body is not { } body || !body.StartsWith(MessagePrefix, StringComparison.Ordinal))
            return;

        var message = body[MessagePrefix.Length..];
        switch (message)
        {
            case "close":
                BrowserTiles.CloseAll();
                return;
            case "maximize":
                FindLeaf()?.ToggleMaximizeCommand.Execute(null);
                return;
            case "newtab":
                _vm.NewTabCommand.Execute(null);
                FocusAddress();
                return;
            case "closetab":
                _vm.CloseTabCommand.Execute(tab);
                return;
            case "nexttab":
                _vm.CycleTabCommand.Execute(1);
                return;
            case "prevtab":
                _vm.CycleTabCommand.Execute(-1);
                return;
        }

        if (message.StartsWith("title:", StringComparison.Ordinal))
            tab.Title = message["title:".Length..];
        else if (message.StartsWith("url:", StringComparison.Ordinal)
                 && Uri.TryCreate(message["url:".Length..], UriKind.Absolute, out var address)
                 && address != tab.Address)
            _vm.OnNavigated(tab, address, page.CanGoBack, page.CanGoForward);
    }

    private LeafTileNodeViewModel? FindLeaf()
    {
        for (var at = Parent; at is not null; at = at.Parent)
            if (at.DataContext is LeafTileNodeViewModel leaf && ReferenceEquals(leaf.Content, _vm))
                return leaf;
        return null;
    }

    private void OnTabPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_vm is null || sender is not Control { DataContext: BrowserTabViewModel tab }) return;

        // The middle button closes a tab, as in every browser; the left one shows it.
        if (e.InitialPressMouseButton == MouseButton.Middle)
            _vm.CloseTabCommand.Execute(tab);
        else if (e.InitialPressMouseButton == MouseButton.Left)
            _vm.SelectTabCommand.Execute(tab);
        e.Handled = true;
    }

    /// <summary>The same shortcuts as inside a page, for when the address box or a tab has the keyboard.</summary>
    private void OnViewKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is null) return;

        if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.T)
        {
            _vm.NewTabCommand.Execute(null);
            FocusAddress();
        }
        else if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.W)
            _vm.CloseTabCommand.Execute(null);
        else if (e.Key == Key.Tab && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            _vm.CycleTabCommand.Execute(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
        else
            return;

        e.Handled = true;
    }

    private void FocusAddress()
    {
        AddressBox.Focus();
        AddressBox.SelectAll();
    }

    private void OnAddressKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None || _vm is null) return;

        if (e.Key == Key.Enter)
        {
            _vm.GoCommand.Execute(null);
            if (_pages.TryGetValue(_vm.SelectedTab, out var page))
                page.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _vm.AddressText = _vm.SelectedTab.Address.ToString();
            e.Handled = true;
        }
    }

    private void OnModalChanged() => Dispatcher.UIThread.Post(ApplyVisibility);

    private void ApplyVisibility()
    {
        if (_vm is null) return;
        var hidden = ModalScope.IsAnyOpen;
        foreach (var (tab, page) in _pages)
            page.IsVisible = !hidden && tab == _vm.SelectedTab;
        HiddenNote.IsVisible = hidden;
    }

    private void OnTileDisposed()
    {
        ModalScope.Changed -= OnModalChanged;
        if (_vm is not null)
        {
            _vm.Disposed -= OnTileDisposed;
            _vm.PropertyChanged -= OnTilePropertyChanged;
            _vm.Tabs.CollectionChanged -= OnTabsChanged;
            _vm.CachedView = null;
        }

        foreach (var tab in _pages.Keys.ToList())
            RemovePage(tab);

        if (_acquired)
        {
            _acquired = false;
            BrowserEngine.Release();
        }

        ControlHelper.DetachFromParent(this);
    }
}
