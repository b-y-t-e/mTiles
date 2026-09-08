using CommunityToolkit.Mvvm.ComponentModel;
using mTiles.Models;

namespace mTiles.ViewModels;

public partial class ManualConnectionViewModel : ObservableObject
{
    public string Id { get; }
    public DbProviderType Provider { get; }
    public string Alias { get; }
    public string Server { get; }
    public string Instance { get; }
    public string Database { get; }
    public int Port { get; }
    public bool UseIntegratedSecurity { get; }

    [ObservableProperty] private bool _isTesting;
    [ObservableProperty] private string? _testResult;

    public string DisplayServer
    {
        get
        {
            var s = Server;
            if (!string.IsNullOrEmpty(Instance)) s += $"\\{Instance}";
            if (Port > 0) s += $":{Port}";
            return s;
        }
    }

    public string ProviderShort => Provider == DbProviderType.PostgreSQL ? "PG" : "SQL";
    public bool HasAlias => !string.IsNullOrWhiteSpace(Alias);
    public string Label => HasAlias ? Alias : Database;

    /// <summary>Everything this row can be found by, folded into one string once.</summary>
    private readonly string _searchText;

    /// <summary>Whether every word typed in the filter is somewhere in this row.</summary>
    /// <remarks>Every word, anywhere, in any order — the same rule the detected list above it uses, so
    /// one filter box does not behave differently from the other on the same page. What is searched is
    /// what the row shows: its name, its address and the provider it speaks.</remarks>
    public bool MatchesFilter(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return true;

        foreach (var token in filter.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!_searchText.Contains(token, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    public ManualConnectionViewModel(ManualDatabaseConnection mc)
    {
        Id = mc.Id;
        Provider = mc.Provider;
        Alias = mc.Alias;
        Server = mc.Server;
        Instance = mc.Instance;
        Database = mc.Database;
        Port = mc.Port;
        UseIntegratedSecurity = mc.UseIntegratedSecurity;
        _searchText = $"{Alias} {Server} {Instance} {Database} {Provider} {ProviderShort}";
    }
}
