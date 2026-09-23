using mTiles.Models;
using mTiles.Services;
using mTiles.Services.Database;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The connections file: what it carries, what it refuses, and what importing one does to the list.
/// </summary>
public class ManualConnectionPortabilityTests
{
    private static ManualDatabaseConnection Connection(string alias, string server, string database,
        string password = "", string? id = null, int port = 0) => new()
    {
        Id = id ?? Guid.NewGuid().ToString(),
        Alias = alias,
        Server = server,
        Database = database,
        Username = "reader",
        Password = password,
        Port = port,
    };

    private static string TempFile() =>
        Path.Combine(Path.GetTempPath(), $"mtiles-test-{Guid.NewGuid():N}.json");

    // ───────────────────────────── The file ─────────────────────────────

    [Fact]
    public void A_passphrase_carries_the_passwords_and_nothing_else_does()
    {
        var path = TempFile();
        var connections = new[] { Connection("prod", "db1", "orders", "s3cret") };

        try
        {
            ManualConnectionsPortability.Export(connections, path, "correct horse");
            var text = File.ReadAllText(path);

            // The password is in the file, and not as itself.
            Assert.DoesNotContain("s3cret", text);

            var bundle = ManualConnectionsPortability.Read(path, out var problem);
            Assert.NotNull(bundle);
            Assert.Equal("", problem);
            Assert.True(ManualConnectionsPortability.IsProtected(bundle!));

            var opened = ManualConnectionsPortability.Open(bundle!, "correct horse", out problem);
            Assert.NotNull(opened);
            Assert.Equal("s3cret", opened![0].Password);
            Assert.Equal("prod", opened[0].Alias);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Without_a_passphrase_the_configuration_travels_and_the_passwords_do_not()
    {
        var path = TempFile();
        try
        {
            ManualConnectionsPortability.Export(
                [Connection("prod", "db1", "orders", "s3cret")], path, "");

            var bundle = ManualConnectionsPortability.Read(path, out _)!;
            Assert.False(ManualConnectionsPortability.IsProtected(bundle));

            var opened = ManualConnectionsPortability.Open(bundle, "", out _)!;
            Assert.Equal("db1", opened[0].Server);
            Assert.Equal("", opened[0].Password);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_wrong_passphrase_is_named_as_one()
    {
        var path = TempFile();
        try
        {
            ManualConnectionsPortability.Export(
                [Connection("prod", "db1", "orders", "s3cret")], path, "right");

            var bundle = ManualConnectionsPortability.Read(path, out _)!;
            Assert.Null(ManualConnectionsPortability.Open(bundle, "wrong", out var problem));
            Assert.Contains("passphrase", problem, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>The whole reason the file carries a kind: neither importer may take the other's file.</summary>
    [Fact]
    public void A_settings_export_is_refused_by_name()
    {
        var path = TempFile();
        try
        {
            SettingsPortability.Export(new AppSettings(), path);
            Assert.Null(ManualConnectionsPortability.Read(path, out var problem));
            Assert.Contains("not a connections file", problem);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ───────────────────────────── The merge ─────────────────────────────

    [Fact]
    public void A_matching_address_is_overwritten_rather_than_added_twice()
    {
        var stored = new List<ManualDatabaseConnection> { Connection("prod", "db1", "orders", "old", port: 1433) };
        var incoming = new[] { Connection("production", "DB1", "Orders", "new", port: 5432) };

        var result = ManualConnectionMerge.Merge(stored, incoming);

        Assert.Single(result.Merged);
        Assert.Equal(1, result.Updated);
        Assert.Equal(0, result.Added);
        Assert.Equal("production", result.Merged[0].Alias);
        Assert.Equal("new", result.Merged[0].Password);
        // The port is the candidate's too: the address was this row's own, so nothing stands in its way.
        Assert.Equal(5432, result.Merged[0].Port);
    }

    [Fact]
    public void A_matching_alias_is_overwritten_too()
    {
        var stored = new List<ManualDatabaseConnection> { Connection("prod", "db1", "orders") };
        var incoming = new[] { Connection("prod", "db2", "orders") };

        var result = ManualConnectionMerge.Merge(stored, incoming);

        Assert.Single(result.Merged);
        Assert.Equal("db2", result.Merged[0].Server);
    }

    [Fact]
    public void A_password_that_did_not_travel_keeps_the_one_stored_here()
    {
        var stored = new List<ManualDatabaseConnection> { Connection("prod", "db1", "orders", "mine") };
        var incoming = new[] { Connection("prod", "db1", "orders") };

        var result = ManualConnectionMerge.Merge(stored, incoming);

        Assert.Equal("mine", result.Merged[0].Password);
        Assert.Equal(1, result.WithoutPassword);
    }

    [Fact]
    public void What_is_missing_is_added_and_nothing_is_ever_removed()
    {
        var stored = new List<ManualDatabaseConnection> { Connection("mine", "db1", "orders") };
        var incoming = new[] { Connection("theirs", "db2", "billing", port: 5433) };

        var result = ManualConnectionMerge.Merge(stored, incoming);

        Assert.Equal(2, result.Merged.Count);
        Assert.Equal(1, result.Added);
        Assert.Contains(result.Merged, c => c.Alias == "mine");
        Assert.Equal(5433, result.Merged.Single(c => c.Alias == "theirs").Port);
    }

    /// <summary>Two exports merged in turn must not leave two rows that cannot be told apart.</summary>
    [Fact]
    public void A_colliding_id_on_a_different_database_gets_one_of_its_own()
    {
        var shared = Guid.NewGuid().ToString();
        var stored = new List<ManualDatabaseConnection> { Connection("mine", "db1", "orders", id: shared) };
        var incoming = new[] { Connection("theirs", "db2", "billing", id: shared) };

        // Matching on the id first is what makes this an overwrite rather than a second row — which is
        // the point: one id is one row, and the address is what an import of somebody else's file is
        // matched by.
        var result = ManualConnectionMerge.Merge(stored, incoming);
        Assert.Single(result.Merged);
        Assert.Equal("db2", result.Merged[0].Server);
    }

    [Fact]
    public void The_merged_list_never_holds_a_clash_the_form_would_refuse()
    {
        var stored = new List<ManualDatabaseConnection>
        {
            Connection("a", "db1", "orders"),
            Connection("b", "db2", "billing"),
        };
        var incoming = new[]
        {
            Connection("a", "db1", "orders"),
            Connection("c", "db3", "stock"),
        };

        var merged = ManualConnectionMerge.Merge(stored, incoming).Merged;

        foreach (var connection in merged)
            Assert.Null(ManualConnectionClash.Find(merged, connection));
    }

    /// <summary>The path the invariant above missed: a candidate that is a stored row by address can
    /// carry a name a different row already holds, and adopting it would put two rows under one alias
    /// in the registry — the query-answered-by-the-wrong-server clash the form refuses at save time.
    /// The stored row keeps its own name.</summary>
    [Fact]
    public void An_alias_another_row_holds_is_not_adopted_by_the_overwritten_one()
    {
        var stored = new List<ManualDatabaseConnection>
        {
            Connection("a", "db1", "orders"),
            Connection("b", "db2", "billing"),
        };
        var incoming = new[] { Connection("b", "db1", "orders", "new") };

        var result = ManualConnectionMerge.Merge(stored, incoming);

        Assert.Equal(2, result.Merged.Count);
        Assert.Equal(1, result.Updated);
        Assert.Equal(0, result.Added);

        var updated = result.Merged.Single(c => c.Server == "db1");
        Assert.Equal("a", updated.Alias);
        Assert.Equal("new", updated.Password);
        Assert.All(result.Merged, c => Assert.Null(ManualConnectionClash.Find(result.Merged, c)));
    }

    /// <summary>The same rule for the other key, on the other match: a candidate matched by id can
    /// carry an address another row holds. The address stays the stored one — and with it the port,
    /// which the registry key does not compare and so nothing else would signal: a stored address on
    /// the candidate's port is a third server neither of the two named. The alias, which was free,
    /// is adopted.</summary>
    [Fact]
    public void An_address_another_row_holds_is_not_adopted_either()
    {
        var shared = Guid.NewGuid().ToString();
        var stored = new List<ManualDatabaseConnection>
        {
            Connection("a", "db1", "orders"),
            Connection("b", "db2", "billing", id: shared, port: 1433),
        };
        var incoming = new[] { Connection("b2", "db1", "orders", id: shared, port: 5432) };

        var result = ManualConnectionMerge.Merge(stored, incoming);

        var row = result.Merged.Single(c => c.Id == shared);
        Assert.Equal("db2", row.Server);
        Assert.Equal(1433, row.Port);
        Assert.Equal("b2", row.Alias);
        Assert.All(result.Merged, c => Assert.Null(ManualConnectionClash.Find(result.Merged, c)));
    }

    [Fact]
    public void A_row_with_no_server_or_database_is_not_imported()
    {
        var result = ManualConnectionMerge.Merge([], [Connection("broken", "", "")]);

        Assert.Empty(result.Merged);
        Assert.Equal(0, result.Added);
    }
}
