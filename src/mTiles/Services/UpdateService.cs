using System.Diagnostics;
using Avalonia.Threading;
using Velopack;
using Velopack.Sources;

namespace mTiles.Services;

public sealed class UpdateService : IDisposable
{
    private const string GithubRepo = "https://github.com/b-y-t-e/mTiles";
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(10);

    private readonly DispatcherTimer _timer;

    /// <summary>
    /// Built on first use rather than in a field initialiser, and never allowed to escape.
    /// <para><c>UpdateManager</c> throws unless <c>VelopackApp.Build()</c> has run, which is true of the
    /// application and not of anything else that constructs this — a test, a tool, a designer. Failing
    /// there took the whole main view model down with it, which is a great deal of collateral for a
    /// feature whose entire job is optional and best-effort.</para>
    /// </summary>
    private UpdateManager? Manager
    {
        get
        {
            if (_mgr is not null || _managerUnavailable)
                return _mgr;

            try
            {
                _mgr = new UpdateManager(new GithubSource(GithubRepo, null, false));
            }
            catch (Exception ex)
            {
                _managerUnavailable = true;     // asked once; there is no second answer
                Debug.WriteLine($"Updates are unavailable in this installation: {ex.Message}");
            }
            return _mgr;
        }
    }

    private UpdateManager? _mgr;
    private bool _managerUnavailable;
    private volatile UpdateInfo? _pendingUpdate;
    private int _checking;

    public event Action? UpdateAvailable;
    public bool HasUpdate => _pendingUpdate != null;
    public string? NewVersion => _pendingUpdate?.TargetFullRelease.Version.ToString();

    public UpdateService()
    {
        _timer = new DispatcherTimer { Interval = CheckInterval };
        _timer.Tick += (_, _) => _ = Task.Run(() => CheckSilently());
    }

    public void StartPeriodicCheck()
    {
        _timer.Start();
        _ = Task.Run(() => CheckSilently());
    }

    private void CheckSilently()
    {
        if (_pendingUpdate != null) return;
        if (Interlocked.CompareExchange(ref _checking, 1, 0) != 0) return;
        try
        {
            if (Manager is not { } manager) return;

            var info = manager.CheckForUpdates();
            if (info != null)
            {
                manager.DownloadUpdates(info);
                _pendingUpdate = info;
                Dispatcher.UIThread.Post(() => UpdateAvailable?.Invoke());
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Update check failed: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _checking, 0);
        }
    }

    public void ApplyUpdate()
    {
        if (_pendingUpdate == null || Manager is not { } manager) return;
        manager.ApplyUpdatesAndRestart(_pendingUpdate);
    }

    public void Dispose()
    {
        _timer.Stop();
    }
}
