using mTiles.Models;

namespace mTiles.Services.Database;

/// <summary>
/// Whether a manual connection would collide with one that is already stored, and in what.
/// </summary>
/// <remarks>
/// <para><b>The registry is a dictionary and both of these are keys in it.</b>
/// <see cref="DbRegistry.Register"/> files an instance under
/// <see cref="DatabaseInstance.Key"/> — server, instance and database — and, when there is one, under
/// its lowercased alias as well. Two connections agreeing on either one therefore do not coexist: the
/// second overwrites the first, so an agent asking for that name reaches a database it was not pointed
/// at, with credentials nobody chose, while both rows are still on the settings page looking fine.
/// Removing one of them then unregisters the other's route as well.</para>
/// <para>This is why the two rules are not a matter of taste about naming: a duplicate is not a tidy
/// list's problem, it is a query answered by the wrong server. It became worth stating out loud when
/// cloning arrived — copying a row is the one gesture whose <em>starting point</em> is a duplicate.</para>
/// <para>Pure, and in a class of its own for the reason <c>ChainPolicy</c> and <c>WorkspaceDisplayOrder</c>
/// are: it is a rule, so it is argued in a table test rather than rediscovered from a screenshot.</para>
/// </remarks>
public static class ManualConnectionClash
{
    /// <summary>What is wrong with <paramref name="candidate"/>, or null when nothing is.</summary>
    /// <param name="stored">Every manual connection already stored. The candidate itself is skipped by
    /// id, so saving an edit does not report the row as clashing with itself.</param>
    public static string? Find(IEnumerable<ManualDatabaseConnection> stored, ManualDatabaseConnection candidate)
    {
        var alias = candidate.Alias.Trim();

        foreach (var other in stored)
        {
            if (other.Id == candidate.Id) continue;

            if (alias.Length > 0 && string.Equals(other.Alias.Trim(), alias, StringComparison.OrdinalIgnoreCase))
                return $"Another connection is already called \"{other.Alias.Trim()}\". " +
                       "The name is how an agent asks for a database, so two cannot share one.";

            if (string.Equals(AddressOf(other), AddressOf(candidate), StringComparison.OrdinalIgnoreCase))
                return $"{Address(other)} is already here" +
                       (other.Alias.Trim().Length > 0 ? $", as \"{other.Alias.Trim()}\"" : "") +
                       ". Point this one at a different database, or give it its own address.";
        }

        return null;
    }

    /// <summary>The address the HTTP bridge files this connection under.</summary>
    /// <remarks>The same shape as <see cref="DatabaseInstance.Key"/>, and it has to stay that way —
    /// this is a statement about what will collide there, not a second opinion about identity. The
    /// provider is deliberately not part of it: the key does not carry one either, so SQL Server and
    /// PostgreSQL on one address and database name are the collision this exists to catch.</remarks>
    private static string AddressOf(ManualDatabaseConnection mc) =>
        Address(mc).ToLowerInvariant();

    private static string Address(ManualDatabaseConnection mc) =>
        mc.Instance.Trim().Length == 0
            ? $"{mc.Server.Trim()}/{mc.Database.Trim()}"
            : $"{mc.Server.Trim()}/{mc.Instance.Trim()}/{mc.Database.Trim()}";
}
