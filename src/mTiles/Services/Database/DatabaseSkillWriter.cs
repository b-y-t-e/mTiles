using System.Text;
using mTiles.Models;

namespace mTiles.Services.Database;

/// <summary>
/// Builds the <c>SKILL.md</c> that tells an agent this workspace's databases can be queried, and how.
/// </summary>
/// <remarks>
/// <para><b>A skill rather than a section injected into the project's instruction file</b>, which is
/// what <c>ClaudeLocalMdWriter</c> did. Three things were wrong with that and the skill answers all
/// three: machine-specific detail (a port, server names) was landing in a committed file; the file it
/// wrote was <c>claude.local.md</c>, while Claude Code opens <c>CLAUDE.local.md</c> literally — the same
/// file on Windows and a different one on Linux, where the section was simply invisible; and taking the
/// section back out meant cutting a heading-bounded region out of somebody else's document, which is a
/// whole class of "we missed the boundary" bugs. Removing a skill is deleting a directory.</para>
/// <para><b>And it can say far more.</b> The old section was deliberately thin because it sat in the
/// context of every single turn. A skill's body is loaded only when the model decides it needs it, so
/// there is room for the contract the agent otherwise has to discover by being refused: what
/// <see cref="SqlGuard"/> blocks outright, what read-write means, that a write without permission stops
/// and asks a human, and the limits a large result runs into.</para>
/// <para><b>The description is not metadata, it is the only trigger.</b> What sits in the context is the
/// name and this description; the body is never read unless the model reaches for it. So it names the
/// databases themselves — those are what catch an intention, not the word "database" — and says what to
/// use this <em>instead of</em>, because the model's default alternative is to infer the schema from
/// the code and it has to be named out loud.</para>
/// <para>The name is a fixed slug and never derived from the set of databases: a generated name would
/// rename the skill whenever the set changed and leave the old one behind as an orphan.</para>
/// </remarks>
public static class DatabaseSkillWriter
{
    /// <summary>The skill's name, which is also its directory. Constant — see the class remarks.
    /// </summary>
    public const string SkillName = "mtiles-database";

    /// <summary>
    /// The whole <c>SKILL.md</c> for these databases, or null when there is nothing to offer.
    /// </summary>
    /// <remarks>Null rather than an empty skill: a skill announcing an empty list of databases is worse
    /// than no skill, because its description would still be in the context inviting the model to open
    /// it.</remarks>
    public static string? Build(IReadOnlyList<WorkspaceDatabaseConfig> databases, DbRegistry registry,
        int httpPort)
    {
        var resolved = Resolve(databases, registry);
        if (resolved.Count == 0) return null;

        var baseUrl = $"http://localhost:{httpPort}";
        var sb = new StringBuilder();

        sb.Append("---\n");
        sb.Append("name: ").Append(SkillName).Append('\n');
        sb.Append("description: >\n  ").Append(Description(resolved)).Append('\n');
        sb.Append("---\n\n");

        sb.Append("# Databases of this project\n\n");
        sb.Append("Queries go over the local mTiles HTTP bridge at `").Append(baseUrl)
          .Append("`. It runs on this machine only and holds the credentials itself, so there is ")
          .Append("nothing for you to configure and no password for you to see.\n\n");

        sb.Append("## Databases\n\n");
        foreach (var (config, info) in resolved)
        {
            var access = config.AllowModifications ? "read-write" : "read-only";
            sb.Append("- **").Append(info.DisplayName).Append("** (").Append(info.Provider)
              .Append(", ").Append(access).Append("): `GET ").Append(baseUrl).Append("/query/")
              .Append(UrlPath(config, info)).Append("?sql=SELECT+col+FROM+table`\n");
        }

        sb.Append("\n## Endpoints\n\n");
        sb.Append("- `GET ").Append(baseUrl).Append("/databases` — what you are allowed to query.\n");
        sb.Append("- `GET ").Append(baseUrl)
          .Append("/query/{server}/{database}?sql=...` — the query, URL-encoded.\n");
        sb.Append("- `POST ").Append(baseUrl)
          .Append("/query/{server}/{database}` — the same, with the SQL in the body, for a query too ")
          .Append("long or too awkward for a URL.\n");

        sb.Append("\n## What is allowed\n\n");
        sb.Append("- Plain SQL only. `EXEC sp_executesql` and every other dynamic-SQL wrapper is ")
          .Append("refused — write the statement out.\n");
        sb.Append("- `SELECT` always works.\n");
        sb.Append("- `DROP`, `TRUNCATE` and `ALTER` are blocked on every database, whatever its ")
          .Append("access says. There is no way to ask for them.\n");
        sb.Append("- `INSERT`, `UPDATE` and `DELETE` work on a read-write database. On a read-only one ")
          .Append("they **put a dialog in front of the user and your request waits for their answer** ")
          .Append("— so a write you did not mean to make is a person's decision, and a write you did ")
          .Append("mean may simply take a while.\n");
        sb.Append("- Comments do not get past the guard: `--` and `/* */` are stripped before the ")
          .Append("statement is read.\n");

        sb.Append("\n## Limits\n\n");
        sb.Append("- 50 000 rows and 16 MB per result — page with `TOP`/`LIMIT` and `OFFSET` rather ")
          .Append("than selecting a whole table.\n");
        sb.Append("- 512 KB for a `POST` body.\n");

        return sb.ToString();
    }

    private static string Description(IReadOnlyList<(WorkspaceDatabaseConfig Config, DatabaseInstance Info)> resolved)
    {
        var named = string.Join(", ",
            resolved.Select(r => $"{r.Info.DisplayName} on {r.Info.Provider}"));

        return $"Query this project's live databases ({named}) over the local mTiles HTTP bridge. " +
               "Use it whenever you need real data, a table's schema, the actual column names, or to " +
               "check that a query works — instead of guessing the structure from the code or from " +
               "the migrations.";
    }

    /// <summary>The path segment the bridge itself publishes this database under.</summary>
    private static string UrlPath(WorkspaceDatabaseConfig config, DatabaseInstance info) =>
        !string.IsNullOrWhiteSpace(info.Alias)
            ? info.Alias
            : string.Join("/", config.DatabaseKey.Split('/'));

    /// <summary>Drops the databases the registry no longer knows: a skill naming one of those would send
    /// the agent to an address that answers nothing.</summary>
    private static List<(WorkspaceDatabaseConfig Config, DatabaseInstance Info)> Resolve(
        IReadOnlyList<WorkspaceDatabaseConfig> databases, DbRegistry registry)
    {
        var resolved = new List<(WorkspaceDatabaseConfig, DatabaseInstance)>();
        foreach (var config in databases)
        {
            if (registry.TryGet(config.DatabaseKey, out var entry) && entry != null)
                resolved.Add((config, entry.Info));
        }
        return resolved;
    }
}
