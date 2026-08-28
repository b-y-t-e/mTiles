using mTiles.Services;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The <c>.ignore</c> / <c>.rgignore</c> rules a workspace carries.
/// </summary>
/// <remarks>
/// A stated subset of the gitignore syntax, so what it does and does not understand is argued here
/// rather than discovered by a file quietly staying on a list.
/// </remarks>
public class FileSuggestionIgnoreTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "mtiles-ignore-" + Guid.NewGuid());

    public FileSuggestionIgnoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* a temp directory */ }
    }

    private FileSuggestionIgnore With(string contents, string name = ".ignore")
    {
        File.WriteAllText(Path.Combine(_dir, name), contents);
        return FileSuggestionIgnore.Read(_dir);
    }

    [Fact]
    public void A_workspace_with_neither_file_ignores_nothing()
    {
        var ignore = FileSuggestionIgnore.Read(_dir);

        Assert.True(ignore.IsEmpty);
        Assert.False(ignore.Ignores("src/Goal.cs"));
    }

    [Fact]
    public void Both_file_names_are_read() =>
        Assert.True(With("secret.txt", ".rgignore").Ignores("secret.txt"));

    [Fact]
    public void Comments_and_blank_lines_are_not_rules() =>
        Assert.True(With("# a comment\n\n   \n").IsEmpty);

    /// <summary>A bare name matches at any depth — gitignore's rule.</summary>
    [Fact]
    public void An_unanchored_name_matches_anywhere()
    {
        var ignore = With("notes.md");

        Assert.True(ignore.Ignores("notes.md"));
        Assert.True(ignore.Ignores("docs/deep/notes.md"));
    }

    [Fact]
    public void A_leading_slash_anchors_to_the_workspace_root()
    {
        var ignore = With("/notes.md");

        Assert.True(ignore.Ignores("notes.md"));
        Assert.False(ignore.Ignores("docs/notes.md"));
    }

    /// <summary>A pattern with a separator in it is a path, not a name to look for.</summary>
    [Fact]
    public void A_pattern_holding_a_separator_is_anchored_too()
    {
        var ignore = With("docs/notes.md");

        Assert.True(ignore.Ignores("docs/notes.md"));
        Assert.False(ignore.Ignores("other/docs/notes.md"));
    }

    /// <summary>Naming a directory ignores everything under it, which nothing else here would notice.</summary>
    [Fact]
    public void A_directory_takes_everything_under_it_with_it()
    {
        var ignore = With("generated/");

        Assert.True(ignore.Ignores("generated/"));
        Assert.True(ignore.Ignores("generated/deep/Api.cs"));
        Assert.False(ignore.Ignores("src/Goal.cs"));
    }

    /// <summary>A trailing slash means directories only, so a file of that name survives.</summary>
    [Fact]
    public void A_directory_rule_does_not_take_a_file_of_the_same_name()
    {
        var ignore = With("generated/");

        Assert.False(ignore.Ignores("generated"));
    }

    [Fact]
    public void A_star_stops_at_the_separator()
    {
        var ignore = With("*.min.js");

        Assert.True(ignore.Ignores("web/app.min.js"));
        Assert.False(ignore.Ignores("web/app.js"));
    }

    [Fact]
    public void Two_stars_span_separators()
    {
        var ignore = With("web/**/vendor.js");

        Assert.True(ignore.Ignores("web/a/b/vendor.js"));
    }

    /// <summary>
    /// The last rule that matches wins, which is the whole of what <c>!</c> means.
    /// </summary>
    [Fact]
    public void A_negation_brings_back_what_an_earlier_line_took()
    {
        var ignore = With("*.md\n!README.md");

        Assert.True(ignore.Ignores("docs/guide.md"));
        Assert.False(ignore.Ignores("README.md"));
    }

    /// <summary>Order matters: a negation before the rule it undoes undoes nothing.</summary>
    [Fact]
    public void A_negation_before_its_rule_does_nothing()
    {
        var ignore = With("!README.md\n*.md");

        Assert.True(ignore.Ignores("README.md"));
    }

    /// <summary>An unreadable or missing directory is a workspace with no rules, not a failure.</summary>
    [Fact]
    public void A_directory_that_is_not_there_ignores_nothing() =>
        Assert.True(FileSuggestionIgnore.Read(Path.Combine(_dir, "nope")).IsEmpty);

    /// <summary>
    /// <c>/**/</c> matches zero directories as well as many.
    /// </summary>
    /// <remarks>
    /// Git's rule, and the only thing the two stars say that one does not. Compiled as a plain
    /// <c>.*</c> between two slashes it demanded at least one directory, so <c>a/b</c> slipped past a
    /// rule the user had written to catch it — a file they had ignored quietly staying on the list,
    /// which is the direction of failure this class says it does not take.
    /// </remarks>
    [Fact]
    public void A_globstar_between_slashes_matches_no_directories_at_all()
    {
        var ignore = With("web/**/vendor.js");

        Assert.True(ignore.Ignores("web/vendor.js"));
        Assert.True(ignore.Ignores("web/a/vendor.js"));
        Assert.True(ignore.Ignores("web/a/b/vendor.js"));
    }

    /// <summary>
    /// Case follows the filesystem, as git's <c>core.ignorecase</c> and ripgrep both do.
    /// </summary>
    /// <remarks>
    /// It was case-insensitive everywhere, and on Linux that is exactly the failure the class promises
    /// not to have: <c>Build/</c> also hid <c>build/</c>, so a file the user had said nothing about
    /// disappeared with nothing on screen to say why.
    /// </remarks>
    [Fact]
    public void Case_follows_the_filesystem()
    {
        var ignore = With("Build/");

        Assert.True(ignore.Ignores("Build/out.txt"));

        var foldsCase = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
        Assert.Equal(foldsCase, ignore.Ignores("build/out.txt"));
    }

    /// <summary>
    /// A leading <c>**/</c> includes the root, which is where people most expect it to bite.
    /// </summary>
    /// <remarks>
    /// Git reads <c>**/x</c> as "x at any depth, this level included". Counted as anchored — it does
    /// hold a separator — it compiled to a pattern demanding at least one directory, so the commonest
    /// idiom in these files quietly missed the file in the root.
    /// </remarks>
    [Fact]
    public void A_leading_globstar_includes_the_root()
    {
        var ignore = With("**/vendor.js");

        Assert.True(ignore.Ignores("vendor.js"));
        Assert.True(ignore.Ignores("web/vendor.js"));
        Assert.True(ignore.Ignores("web/a/b/vendor.js"));
    }

    /// <summary>And it stays a whole-segment match, not a suffix one.</summary>
    [Fact]
    public void A_leading_globstar_does_not_match_half_a_name()
    {
        var ignore = With("**/vendor.js");

        Assert.False(ignore.Ignores("notvendor.js"));
        Assert.False(ignore.Ignores("web/notvendor.js"));
    }

    /// <summary>A leading <c>**/</c> on a directory rule still means directories only.</summary>
    [Fact]
    public void A_leading_globstar_keeps_the_directory_rule()
    {
        var ignore = With("**/generated/");

        Assert.True(ignore.Ignores("generated/x.cs"));
        Assert.True(ignore.Ignores("src/deep/generated/x.cs"));
        Assert.False(ignore.Ignores("generated"));
    }
}