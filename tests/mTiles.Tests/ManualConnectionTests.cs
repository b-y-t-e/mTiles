using mTiles.Models;
using mTiles.Services.Database;
using mTiles.ViewModels;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The manual connection list: what may be saved, what the filter leaves, and what a clone starts as.
/// </summary>
[Collection(ProviderSeamCollection.Name)]
public sealed class ManualConnectionTests : IDisposable
{
    private readonly TempSettings _settings = new();

    public void Dispose() => _settings.Dispose();

    private static ManualDatabaseConnection Conn(string alias, string server, string database,
        string instance = "") =>
        new() { Alias = alias, Server = server, Database = database, Instance = instance };

    /// <summary>Two rows that would take the same slot in the registry are refused.</summary>
    /// <remarks><c>DbRegistry.Register</c> files an instance under its address <b>and</b> under its
    /// lowercased alias, so a duplicate of either is not a tidy-list problem: the second overwrites the
    /// first, and an agent asking for that name reaches a database nobody pointed it at.</remarks>
    [Theory]
    // The name, whatever it is spelled like.
    [InlineData("Reports", "srv1", "db1", "reports", "srv2", "db2", true)]
    [InlineData("Reports", "srv1", "db1", "REPORTS", "srv2", "db2", true)]
    [InlineData("Reports", "srv1", "db1", " Reports ", "srv2", "db2", true)]
    // The address, with or without a name on it.
    [InlineData("Reports", "srv1", "db1", "Other", "srv1", "db1", true)]
    [InlineData("", "srv1", "db1", "", "SRV1", "DB1", true)]
    // Different in the one field that matters is not a clash.
    [InlineData("Reports", "srv1", "db1", "Other", "srv1", "db2", false)]
    [InlineData("Reports", "srv1", "db1", "Other", "srv2", "db1", false)]
    [InlineData("", "srv1", "db1", "", "srv1", "db2", false)]
    public void A_name_or_an_address_may_be_used_once(string alias, string server, string database,
        string newAlias, string newServer, string newDatabase, bool clashes)
    {
        var stored = new List<ManualDatabaseConnection> { Conn(alias, server, database) };
        var candidate = Conn(newAlias, newServer, newDatabase);

        Assert.Equal(clashes, ManualConnectionClash.Find(stored, candidate) != null);
    }

    /// <summary>An instance is part of the address, so it tells two rows apart.</summary>
    [Fact]
    public void An_instance_is_part_of_the_address()
    {
        var stored = new List<ManualDatabaseConnection> { Conn("", "srv1", "db1", instance: "prod") };

        Assert.Null(ManualConnectionClash.Find(stored, Conn("", "srv1", "db1", instance: "test")));
        Assert.NotNull(ManualConnectionClash.Find(stored, Conn("", "srv1", "db1", instance: "PROD")));
    }

    /// <summary>Saving a row that has not moved is not a clash with itself.</summary>
    /// <remarks>The obvious way to get this wrong, and the one that would make every edit unsavable:
    /// the row being edited is in the stored list already.</remarks>
    [Fact]
    public void A_row_does_not_clash_with_itself()
    {
        var stored = new List<ManualDatabaseConnection> { Conn("Reports", "srv1", "db1") };
        var edited = stored[0];

        Assert.Null(ManualConnectionClash.Find(stored, edited));
    }

    [Fact]
    public void The_filter_narrows_the_list_and_says_when_it_leaves_nothing()
    {
        var vm = WithConnections(
            Conn("Reports", "sql1", "reporting"),
            Conn("Orders", "sql1", "orders"),
            Conn("Archive", "pg1", "archive"),
            Conn("", "pg2", "scratch"));

        Assert.True(vm.ShowManualConnectionFilter);
        Assert.Equal(4, vm.FilteredManualConnections.Count);

        // Every word, anywhere, in any order - the rule the detected list above it already follows.
        vm.ManualConnectionFilter = "sql1 orders";
        Assert.Equal("Orders", Assert.Single(vm.FilteredManualConnections).Alias);

        vm.ManualConnectionFilter = "nothing here";
        Assert.Empty(vm.FilteredManualConnections);
        Assert.True(vm.ManualConnectionFilterFoundNothing);

        vm.ClearManualConnectionFilterCommand.Execute(null);
        Assert.Equal(4, vm.FilteredManualConnections.Count);
        Assert.False(vm.ManualConnectionFilterFoundNothing);
    }

    /// <summary>A short list is not given a filter box.</summary>
    [Fact]
    public void Three_connections_are_not_worth_a_filter()
    {
        var vm = WithConnections(
            Conn("A", "s", "a"), Conn("B", "s", "b"), Conn("C", "s", "c"));

        Assert.False(vm.ShowManualConnectionFilter);
    }

    /// <summary>A clone opens the form on a copy, under a name that is free.</summary>
    [Fact]
    public void Cloning_fills_the_form_from_the_row_and_renames_it()
    {
        var vm = WithConnections(Conn("Reports", "sql1", "reporting"));
        _settings.Service.Settings.Database.ManualConnections[0].Username = "sa";
        _settings.Service.Settings.Database.ManualConnections[0].Password = "hunter2";

        vm.CloneManualConnectionCommand.Execute(vm.ManualConnections[0]);

        Assert.True(vm.IsEditingManualConnection);
        Assert.True(vm.IsEditingAnything);
        Assert.Equal("Reports copy", vm.EditConnAlias);
        Assert.Equal("sql1", vm.EditConnServer);
        Assert.Equal("reporting", vm.EditConnDatabase);
        Assert.Equal("sa", vm.EditConnUsername);
        Assert.Equal("hunter2", vm.EditConnPassword);

        // Nothing is stored until Save, so cancelling leaves one connection behind.
        vm.CancelEditManualConnectionCommand.Execute(null);
        Assert.Single(_settings.Service.Settings.Database.ManualConnections);
    }

    /// <summary>The suggested name steps past copies that already exist.</summary>
    [Fact]
    public void A_second_clone_gets_its_own_name()
    {
        var vm = WithConnections(Conn("Reports", "sql1", "reporting"), Conn("Reports copy", "sql1", "other"));

        vm.CloneManualConnectionCommand.Execute(vm.ManualConnections[0]);

        Assert.Equal("Reports copy 2", vm.EditConnAlias);
    }

    /// <summary>A clone cannot be saved until it stops being a duplicate — and then it can.</summary>
    [Fact]
    public void A_clone_is_refused_until_it_differs()
    {
        var vm = WithConnections(Conn("Reports", "sql1", "reporting"));

        vm.CloneManualConnectionCommand.Execute(vm.ManualConnections[0]);
        vm.SaveManualConnectionCommand.Execute(null);

        // The address is still the original's, so the form stays open and says so.
        Assert.True(vm.IsEditingManualConnection);
        Assert.NotNull(vm.EditConnProblem);
        Assert.Single(_settings.Service.Settings.Database.ManualConnections);

        // Answering the message clears it, and then the save goes through.
        vm.EditConnDatabase = "reporting_2025";
        Assert.Null(vm.EditConnProblem);

        vm.SaveManualConnectionCommand.Execute(null);
        Assert.False(vm.IsEditingManualConnection);
        Assert.Equal(2, _settings.Service.Settings.Database.ManualConnections.Count);
        Assert.Equal(2, vm.ManualConnections.Count);
        Assert.Equal(2, vm.FilteredManualConnections.Count);
    }

    /// <summary>A name somebody else is already using is refused on an ordinary save too.</summary>
    [Fact]
    public void Saving_under_another_rows_name_is_refused()
    {
        var vm = WithConnections(Conn("Reports", "sql1", "reporting"), Conn("Orders", "sql1", "orders"));

        vm.EditManualConnectionCommand.Execute(vm.ManualConnections[1]);
        vm.EditConnAlias = "reports";
        vm.SaveManualConnectionCommand.Execute(null);

        Assert.True(vm.IsEditingManualConnection);
        Assert.NotNull(vm.EditConnProblem);
        // Refused before anything was written back, so the stored row is untouched.
        Assert.Equal("Orders", _settings.Service.Settings.Database.ManualConnections[1].Alias);
    }

    private SettingsViewModel WithConnections(params ManualDatabaseConnection[] connections)
    {
        _settings.Service.Settings.Database.ManualConnections.AddRange(connections);
        var vm = new SettingsViewModel(_settings.Service);
        vm.SelectTabCommand.Execute(SettingsTabs.Database);
        vm.SelectDbSubTabCommand.Execute(DbSubTabs.Manual);
        Assert.True(vm.IsDbManualSubTab);
        Assert.False(vm.IsDbDiscoveredSubTab);
        return vm;
    }
}
