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
    /// <remarks>The word is part of the reading. A bare <c>13:16</c> at the top of a tile full of clock
    /// times — five-hour windows, resets, countdowns — is one more time of day among them, and the one
    /// question it answers (how old is everything below this) is the one nobody could tell it was
    /// answering. The full instant is in the tooltip, since the line itself only has room for the
    /// minute.</remarks>
    [ObservableProperty] private string _lastRefreshLabel = "";

    /// <summary>The whole instant, spelled out, for the line that only has room for the minute.</summary>
    [ObservableProperty] private string _lastRefreshTooltip = "";

    /// <summary>How many things the busiest account puts on its line, and whether any draws a bar.</summary>
    /// <remarks><b>Facts about the answers, not about the drawing.</b> The view needs them to decide
    /// whether a card's windows still fit side by side (see <c>UsageLayout</c>) — an account with two
    /// windows fits in a column where one with four does not — and the alternative was a code-behind
    /// walking <see cref="Accounts"/> and subscribing to it, which is the tile's own bookkeeping done
    /// twice.</remarks>
    [ObservableProperty] private int _windowsPerAccount;

    /// <inheritdoc cref="WindowsPerAccount" />
    [ObservableProperty] private bool _hasBarWindows;

    /// <summary>How long the longest window label on the tile is, in characters.</summary>
    /// <remarks><b>A fact about the answers, like the two above it.</b> Not every service names its
    /// windows in two characters: agy reports two families of models with two windows each, so the
    /// family is part of every one of its labels (<c>Claude and GPT 7d</c>). A shared line measured
    /// against <c>7d</c> therefore had room for four windows it could only draw by clipping the figure,
    /// which is the one part of the row the design promises to keep — so the length goes to
    /// <c>UsageLayout</c> rather than the threshold assuming it.</remarks>
    [ObservableProperty] private int _longestWindowLabel;

    /// <summary>Nothing has been asked yet — which is not the same as nothing having been found.</summary>
    /// <remarks>The complement of <see cref="IsEmpty"/> among the states with no card to draw: before
    /// the first round answers there is no fact about this machine to state, so what stands in the
    /// middle of the tile is the round itself. Without it the tile is blank for as long as the round
    /// takes, and Claude Code alone allows fifteen seconds of it.</remarks>
    [ObservableProperty] private bool _isLoading;

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
        IsLoading = Accounts.Count == 0 && !IsEmpty;
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
        // One card per login, not per way of naming one. A machine that exports CLAUDE_CONFIG_DIR - which
        // is what a sign-in sets for the tiles it launches - has its default account inside that
        // sign-in's own directory, so both were read from one file and drew the same figures twice.
        // AccountKey is what says they are the same; a report with none is never merged with anything,
        // because two accounts wrongly folded together is a subscription missing from the screen.
        var seen = new HashSet<string>(StringComparer.Ordinal);

        Accounts.Clear();
        foreach (var report in _usage.Reports.Where(report => report.Answered))
            if (report.AccountKey is not { Length: > 0 } key || seen.Add(key))
                Accounts.Add(new UsageAccountViewModel(report, now));

        WindowsPerAccount = Accounts.Count == 0 ? 0 : Accounts.Max(account => account.LineItems.Count);
        HasBarWindows = Accounts.Any(account => account.HasBars);
        LongestWindowLabel = Accounts
            .SelectMany(account => account.LineItems)
            .Select(item => item.Label.Length)
            .DefaultIfEmpty(0)
            .Max();

        IsEmpty = Accounts.Count == 0 && _usage.LastRefresh is not null;
        IsLoading = Accounts.Count == 0 && !IsEmpty;
        IsBusy = _usage.IsRefreshing;

        if (_usage.LastRefresh is { } last)
        {
            var local = last.ToLocalTime();
            LastRefreshLabel = "updated " + local.ToString("HH:mm");
            LastRefreshTooltip = "Last updated " + local.ToString("dddd, d MMMM yyyy, HH:mm:ss");
        }
        else
        {
            LastRefreshLabel = "";
            LastRefreshTooltip = "";
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        _usage.Changed -= OnUsageChanged;
        _attachment.Dispose();
    }
}
