using mTiles.Models;

namespace mTiles.Services.Database;

/// <summary>What an import would do to the stored manual connections.</summary>
public sealed record ManualConnectionMergeResult(
    List<ManualDatabaseConnection> Merged,
    int Updated,
    int Added,
    int WithoutPassword)
{
    /// <summary>What the user is shown before any of it happens.</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (Updated > 0) parts.Add($"{Updated} will be overwritten");
            if (Added > 0) parts.Add($"{Added} will be added");
            if (parts.Count == 0) return "Nothing in this file changes anything here.";

            var sentence = string.Join(" and ", parts) + ".";
            if (WithoutPassword > 0)
                sentence += $" {WithoutPassword} of them carry no password — the one already stored "
                            + "here is kept where there is one, and the rest are typed in once.";
            return sentence;
        }
    }
}

/// <summary>
/// An imported list of manual connections folded into the stored one: overwrite what matches, add what
/// is missing, remove nothing.
/// </summary>
/// <remarks>
/// <para><b>What "matches" means is not a matter of taste</b>, for exactly the reason
/// <see cref="ManualConnectionClash"/> gives: <see cref="DbRegistry"/> files an instance under its
/// address and under its alias, so those are the two ways two rows can be the same database. Matching on
/// <see cref="ManualDatabaseConnection.Id"/> alone would let a colleague's export — whose ids are theirs
/// and not ours — add a second row for a database already here, and the two would then fight over one
/// key in the registry with neither page saying so. Id first, because it is exact; then the address;
/// then the alias.</para>
/// <para><b>An import never removes.</b> A merge that also deleted would make handing somebody a file the
/// same act as handing them the whole configuration, and the row it took would be the one they had added
/// themselves that morning. "Overwrite and add" is the whole of it.</para>
/// <para><b>A password that did not travel does not erase the one that is here.</b> An export written
/// without a passphrase carries no passwords at all, and read as "the password is now empty" it would
/// log every colleague out of every database on the first import. Empty means <em>not said</em>, the same
/// rule <c>SettingsService.KeepExistingSecrets</c> follows.</para>
/// <para>Pure, and in a class of its own for the reason <c>ChainPolicy</c> is: it is a rule, argued in a
/// table test rather than rediscovered from somebody's screenshot.</para>
/// </remarks>
public static class ManualConnectionMerge
{
    /// <summary>Folds <paramref name="incoming"/> into <paramref name="stored"/>, without touching either.</summary>
    public static ManualConnectionMergeResult Merge(
        IEnumerable<ManualDatabaseConnection> stored,
        IEnumerable<ManualDatabaseConnection> incoming)
    {
        var merged = stored.ToList();
        var updated = 0;
        var added = 0;
        var withoutPassword = 0;

        foreach (var candidate in incoming)
        {
            if (candidate.Server.Trim().Length == 0 || candidate.Database.Trim().Length == 0)
                continue;

            if (candidate.Password.Length == 0) withoutPassword++;

            var index = IndexOfMatch(merged, candidate);
            if (index >= 0)
            {
                merged[index] = Overwritten(candidate, merged[index], merged);
                updated++;
                continue;
            }

            var fresh = Normalised(candidate);
            fresh.Id = FreeId(merged, candidate.Id);
            merged.Add(fresh);
            added++;
        }

        return new ManualConnectionMergeResult(merged, updated, added, withoutPassword);
    }

    /// <summary>The position of the stored row this candidate <em>is</em>, or -1 when it is a new one.</summary>
    private static int IndexOfMatch(
        List<ManualDatabaseConnection> stored, ManualDatabaseConnection candidate)
    {
        var byId = stored.FindIndex(s => s.Id == candidate.Id);
        if (byId >= 0) return byId;

        var byAddress = stored.FindIndex(s => Same(Address(s), Address(candidate)));
        if (byAddress >= 0) return byAddress;

        var alias = candidate.Alias.Trim();
        if (alias.Length == 0) return -1;
        return stored.FindIndex(s => Same(s.Alias.Trim(), alias));
    }

    /// <summary>The stored row as the candidate overwrites it: the candidate's fields and password,
    /// except where the candidate's value would land on another row.</summary>
    /// <remarks>The alias and the address are the two keys <see cref="DbRegistry"/> files a row under,
    /// and matching the row by one of them says nothing about the other: a candidate that is this row
    /// by address can carry a name a <em>different</em> row already holds, and adopting it here is the
    /// clash <see cref="ManualConnectionClash"/> refuses at save time — reached past the form by a
    /// merge. Whichever key the candidate's value would land on another row keeps the stored one, which
    /// is also how the name and the place the row already had survive — the port with the place, since
    /// a stored address on the candidate's port is a third server neither of the two named.</remarks>
    private static ManualDatabaseConnection Overwritten(
        ManualDatabaseConnection candidate, ManualDatabaseConnection existing,
        List<ManualDatabaseConnection> merged)
    {
        var fresh = Normalised(candidate);
        fresh.Id = existing.Id;
        fresh.Password = candidate.Password.Length > 0 ? candidate.Password : existing.Password;

        if (AliasTaken(merged, fresh)) fresh.Alias = existing.Alias;
        if (AddressTaken(merged, fresh))
            (fresh.Server, fresh.Instance, fresh.Database, fresh.Port) =
                (existing.Server, existing.Instance, existing.Database, existing.Port);

        return fresh;
    }

    private static bool AliasTaken(List<ManualDatabaseConnection> stored, ManualDatabaseConnection row) =>
        row.Alias.Trim().Length > 0
        && stored.Any(s => s.Id != row.Id && Same(s.Alias.Trim(), row.Alias.Trim()));

    private static bool AddressTaken(List<ManualDatabaseConnection> stored, ManualDatabaseConnection row) =>
        stored.Any(s => s.Id != row.Id && Same(Address(s), Address(row)));

    /// <summary>An id nothing else here is using.</summary>
    /// <remarks>Two exports merged in turn can carry the same id for different databases — the ids are
    /// whoever generated them — and a duplicate id is a row that can never be edited or deleted on its
    /// own afterwards.</remarks>
    private static string FreeId(List<ManualDatabaseConnection> stored, string proposed) =>
        proposed.Length > 0 && stored.All(s => s.Id != proposed) ? proposed : Guid.NewGuid().ToString();

    /// <summary>The candidate as this merge writes it: the text fields trimmed, the port a valid one.</summary>
    /// <remarks>The only field list here is the list of what gets <em>normalised</em>; every other
    /// field travels through <see cref="ManualDatabaseConnection.Clone"/>, so one added to the model
    /// reaches the merge without touching this class.</remarks>
    private static ManualDatabaseConnection Normalised(ManualDatabaseConnection mc)
    {
        var copy = mc.Clone();
        copy.Alias = copy.Alias.Trim();
        copy.Server = copy.Server.Trim();
        copy.Instance = copy.Instance.Trim();
        copy.Database = copy.Database.Trim();
        copy.Username = copy.Username.Trim();
        copy.Port = Math.Clamp(copy.Port, 0, 65535);
        return copy;
    }

    /// <summary>The address <see cref="ManualConnectionClash"/> compares — asked of it, not restated.</summary>
    private static string Address(ManualDatabaseConnection mc) => ManualConnectionClash.Address(mc);

    private static bool Same(string a, string b) =>
        a.Length > 0 && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
