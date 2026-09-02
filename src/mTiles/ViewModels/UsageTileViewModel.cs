using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using mTiles.Models;
using mTiles.Services;

namespace mTiles.ViewModels;

/// <summary>
/// A read-only dashboard: what is left of every account this machine can actually ask.
/// </summary>
/// <remarks>
/// <para><b>It starts nothing and kills nothing</b>, which is why it implements
/// <see cref="IBusyTile"/> and none of the other tile interfaces — no process to report
/// (<c>IProcessTile</c>), no file to follow, no text to type into. The working light is the one thing a
/// workspace row needs from it: a refresh reaches three services over the network and can take a few
/// seconds, and a dashboard that looks identical while it is asking is a dashboard nobody trusts.</para>
/// <para><b>The asking is not this tile's.</b> <see cref="AiUsageService"/> holds the answers for the
/// whole application, so two usage tiles in two workspaces are one set of calls; this attaches to it
/// while it is alive, which is also what lets the service's timer stop when the last one closes.</para>
/// </remarks>
public sealed partial class UsageTileViewModel : ObservableObject, IBusyTile
{
    private readonly AiUsageService _usage;
    private readonly Action<int>? _openSettings;
    private readonly IDisposable _attachment;
    private bool _disposed;

    public UsageTileViewModel(AiUsageService usage, Action<int>? openSettings = null)
    {
        _usage = usage;
        _openSettings = openSettings;
        _attachment = usage.Attach();

        usage.Changed += OnUsageChanged;
        Rebuild();

        // Asked for as the tile is built, and only asked: a set of answers younger than the refresh
        // window is the same set this tile would fetch, and a second workspace opening a dashboard must
        // not cost a second round of calls against somebody's rate limit.
        _ = RefreshAsync(force: false);
    }

    /// <inheritdoc />
    public string KindId => TileKindIds.Usage;

    /// <summary>The cards, in the order the service listed the accounts.</summary>
    public ObservableCollection<UsageAccountViewModel> Accounts { get; } = [];

    /// <inheritdoc />
    [ObservableProperty] private bool _isBusy;

    /// <summary>Whether the tile has anything to say yet — nothing asked is not nothing found.</summary>
    /// <remarks><b>False until the first round has answered.</b> It started true and was recomputed in
    /// the constructor, so "No account here reports limits." was the first thing every usage tile said,
    /// for as long as the round took — and Claude Code alone allows fifteen seconds. A sentence stating
    /// a fact about the machine, shown before anything was asked, is simply wrong for that moment.
    /// <c>LastRefresh</c> already tells the two states apart.</remarks>
    [ObservableProperty] private bool _isEmpty;

    /// <summary>When the figures were last replaced, or an empty string before the first answer.</summary>
    [ObservableProperty] private string _lastRefreshLabel = "";

    /// <summary>What an empty tile says, which is a fact and not an error.</summary>
    /// <remarks>Every one of the six CLIs and six services is asked; most of them publish nothing, so a
    /// machine with a key-less local setup legitimately has no card to draw. The button beside it goes
    /// where an account is configured.</remarks>
    public static string EmptyMessage => "No account here reports limits.";

    [RelayCommand]
    private Task Refresh() => RefreshAsync(force: true);

    [RelayCommand]
    private void OpenAiSettings() => _openSettings?.Invoke(SettingsTabs.Ai);

    private async Task RefreshAsync(bool force)
    {
        IsBusy = true;
        try { await _usage.RefreshAsync(force); }
        finally { IsBusy = _usage.IsRefreshing; }
    }

    /// <summary>The service answers off the UI thread, and the cards are the UI thread's.</summary>
    private void OnUsageChanged() => Dispatcher.UIThread.Post(Rebuild);

    private void Rebuild()
    {
        if (_disposed) return;

        var now = DateTimeOffset.Now;

        // An account that could not be asked gets no card at all. It is the opposite of what the report
        // type was built for - a Problem exists so a failure never reads as a zero - and it is the
        // user's call: most of these failures are an account they do not use through this machine, and a
        // dashboard whose permanent top line is a sentence about one of them is a dashboard they stop
        // reading. The reason is still in the log; what is gone is the card.
        Accounts.Clear();
        foreach (var report in _usage.Reports.Where(report => report.Answered))
            Accounts.Add(new UsageAccountViewModel(report, now));

        IsEmpty = Accounts.Count == 0 && _usage.LastRefresh is not null;
        IsBusy = _usage.IsRefreshing;
        LastRefreshLabel = _usage.LastRefresh is { } last ? last.ToLocalTime().ToString("HH:mm") : "";
    }

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        _usage.Changed -= OnUsageChanged;
        _attachment.Dispose();
    }
}
