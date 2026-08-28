using mTiles.Services;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// Which files a half-typed mention offers, and in what order.
/// </summary>
/// <remarks>
/// A list like this is judged entirely on its first three rows, and the order is an opinion rather than
/// a fact — so it is argued here, against a list nobody had to create on disk.
/// </remarks>
public class FileMentionMatcherTests
{
    private static readonly string[] Tree =
    [
        "README.md",
        "src/mTiles/Services/GoalPromptBuilder.cs",
        "src/mTiles/ViewModels/GoalTileViewModel.cs",
        "src/mTiles/Views/GoalTileView.axaml",
        "docs/GOAL.md",
    ];

    /// <summary>
    /// Both are word starts, and the shorter path wins on the length bonus alone.
    /// </summary>
    /// <remarks>
    /// The order this used to assert is the same; the reason is not, and the reason is the point. There
    /// is no term here for the file's own name — a match after <c>/</c> earns
    /// <c>BonusBoundary</c> wherever it lands — so a directory is a first-class thing to type. What
    /// separates these two is the bonus for a short path.
    /// </remarks>
    [Fact]
    public void Two_word_starts_are_separated_only_by_how_deep_they_are()
    {
        var matched = FileMentionMatcher.Match(["src/goal/Runner.cs", "src/Goal.cs"], "goal");

        Assert.Equal(["src/Goal.cs", "src/goal/Runner.cs"], matched);
    }

    /// <summary>
    /// A folder name is as good a thing to type as a file name — the case the old ranking could not
    /// serve.
    /// </summary>
    /// <remarks>
    /// Measured against a 3443-file TypeScript repository, where this is the ordinary shape:
    /// <c>@bash</c> put
    /// <c>tools/BashTool/prompt.ts</c> at position 32 of 66 and filled every row with files merely
    /// spelling "bash" in their name, because a hit in a directory was ranked below any hit in a file
    /// name. Here the folder's own files come first: the <c>b</c> after <c>/</c> is a word start, and
    /// <c>bashClassifier</c> pays for the depth it sits at.
    /// </remarks>
    [Fact]
    public void A_folder_that_matches_outranks_a_deeper_file_that_merely_spells_the_word()
    {
        string[] tree =
        [
            "src/tools/BashTool/prompt.ts",
            "src/utils/permissions/bashClassifier.ts",
            "src/tools/BashTool/UI.tsx",
        ];

        var matched = FileMentionMatcher.Match(tree, "bash");

        Assert.Equal(
            ["src/tools/BashTool/UI.tsx", "src/tools/BashTool/prompt.ts",
             "src/utils/permissions/bashClassifier.ts"],
            matched);
    }

    /// <summary>
    /// An acronym finds a file no substring of which contains it.
    /// </summary>
    /// <remarks>
    /// <c>btp</c> is <b>B</b>ashTool/<b>p</b>rompt — the scorer matches a subsequence, so the letters
    /// need only appear in order. This is the class of query the substring ranking could not answer at
    /// all, whatever the user typed.
    /// </remarks>
    [Fact]
    public void An_acronym_finds_what_no_substring_would()
    {
        string[] tree = ["src/tools/BashTool/prompt.ts", "src/utils/permissions/bashClassifier.ts"];

        Assert.Equal(["src/tools/BashTool/prompt.ts"], FileMentionMatcher.Match(tree, "btp"));
    }

    /// <summary>
    /// Smart case: a query in lower case ignores case, one carrying a capital means it.
    /// </summary>
    /// <remarks>
    /// The only way to say "I meant the capital" without a switch nobody would find, and the reason
    /// <see cref="Case_is_not_something_the_user_has_to_get_right"/> is about a lower-case query only.
    /// </remarks>
    [Fact]
    public void A_capital_in_the_query_is_a_request_for_that_capital()
    {
        string[] tree = ["src/ui/theme.cs", "src/UI/Theme.cs"];

        Assert.Equal(["src/ui/theme.cs", "src/UI/Theme.cs"], FileMentionMatcher.Match(tree, "ui"));
        Assert.Equal(["src/UI/Theme.cs"], FileMentionMatcher.Match(tree, "UI"));
    }

    [Fact]
    public void A_name_that_starts_with_what_was_typed_comes_before_one_that_merely_contains_it()
    {
        var matched = FileMentionMatcher.Match(["a/TheGoal.cs", "b/GoalX.cs"], "goal");

        Assert.Equal(["b/GoalX.cs", "a/TheGoal.cs"], matched);
    }

    [Fact]
    public void The_shorter_path_comes_first_when_two_files_match_as_well_as_each_other()
    {
        var matched = FileMentionMatcher.Match(["src/deep/down/Goal.cs", "Goal.cs"], "goal");

        Assert.Equal(["Goal.cs", "src/deep/down/Goal.cs"], matched);
    }

    [Fact]
    public void A_directory_typed_into_the_mention_narrows_it()
    {
        var matched = FileMentionMatcher.Match(Tree, "Views/Goal");

        // ViewModels answers too — every letter of the query is in it, in order — but it answers
        // worse: the run is broken, which costs a gap penalty the exact hit never pays.
        Assert.Equal(
            ["src/mTiles/Views/GoalTileView.axaml", "src/mTiles/ViewModels/GoalTileViewModel.cs"],
            matched);
    }

    [Fact]
    /// <summary>
    /// A bare <c>@</c> offers the tree's top level, not the front of the corpus.
    /// </summary>
    /// <remarks>
    /// Each path's first segment, once, shortest first and
    /// then alphabetically. Handing over the first rows as they come filled the list with folders four
    /// levels down, because that is what sorts earliest — <c>src/mTiles/Services/Database/</c> before
    /// <c>tests/</c>. The question a bare <c>@</c> asks is what is in here.
    /// </remarks>
    public void Nothing_typed_yet_offers_the_top_level_shortest_first()
    {
        var matched = FileMentionMatcher.Match(Tree, "");

        Assert.Equal(["src/", "docs/", "README.md"], matched);
    }

    /// <summary>A folder row and the files under it are one entry, and the separator is not part of it.</summary>
    [Fact]
    public void The_top_level_names_each_place_once()
    {
        var matched = FileMentionMatcher.Match(["src/", "src/a.cs", "src/deep/b.cs", "docs/c.md"], "");

        Assert.Equal(["src/", "docs/"], matched);
    }

    [Fact]
    public void What_matches_nothing_offers_nothing()
    {
        Assert.Empty(FileMentionMatcher.Match(Tree, "kubernetes"));
    }

    [Fact]
    public void The_list_stops_at_the_limit()
    {
        var many = Enumerable.Range(0, 100).Select(i => $"File{i:D3}.cs").ToList();

        Assert.Equal(3, FileMentionMatcher.Match(many, "file", limit: 3).Count);
        Assert.Equal(FileMentionMatcher.DefaultLimit, FileMentionMatcher.Match(many, "file").Count);
    }

    [Fact]
    public void A_lower_case_query_does_not_care_how_the_file_is_spelled()
    {
        Assert.Equal(
            FileMentionMatcher.Match(Tree, "goaltileview"),
            FileMentionMatcher.Match(Tree, "GoalTileView"));
    }

    // ── The folded copy has to line up with the original ──

    /// <summary>
    /// Lowering a path never changes its length, for any character there is.
    /// </summary>
    /// <remarks>
    /// <para>The scorer finds positions in <see cref="FileMentionCorpus.Lowered"/> and reads its
    /// bonuses out of the original path, so the two must agree index for index. Break that and the
    /// bonuses are quietly wrong; break it far enough and the position runs past the end of the
    /// original, which is an <see cref="IndexOutOfRangeException"/> thrown out of a keystroke, on a
    /// task nobody awaits.</para>
    /// <para>It holds because <em>invariant</em> casing is a one-character-for-one mapping, unlike the
    /// full case mapping a culture can apply — where <c>İ</c> (U+0130) becomes <c>i</c> plus a combining
    /// dot. But that is a property of the runtime rather than of this code, and it is the kind of thing
    /// an ICU version could change underneath us, so it is <b>measured</b> rather than assumed: every
    /// character in the BMP, plus a sample from the planes above it where case mapping actually
    /// exists.</para>
    /// </remarks>
    [Fact]
    public void Lowering_never_changes_the_length_of_a_path()
    {
        var offenders = new List<string>();

        for (var c = 0; c <= 0xFFFF; c++)
        {
            if (char.IsSurrogate((char)c)) continue;

            var one = ((char)c).ToString();
            if (one.ToLowerInvariant().Length != one.Length) offenders.Add($"U+{c:X4}");
        }

        // Deseret and Warang Citi are cased scripts outside the BMP, so they are where a surrogate pair
        // would go wrong if anything did.
        foreach (var astral in new[] { 0x10400, 0x104B0, 0x118A0, 0x16E40, 0x1E900 })
        {
            var one = char.ConvertFromUtf32(astral);
            if (one.ToLowerInvariant().Length != one.Length) offenders.Add($"U+{astral:X5}");
        }

        Assert.True(offenders.Count == 0,
            "lowering changed the length of these, so a path holding one puts every later position in "
            + "it out of step with its folded copy: " + string.Join(", ", offenders));
    }

    /// <summary>And a corpus built from awkward names still lines up.</summary>
    [Fact]
    public void A_corpus_folds_every_path_to_the_same_length()
    {
        string[] awkward =
        [
            "docs/İstanbul.md",       // dotted capital I, the classic length-changing candidate
            "docs/Straße.md",         // sharp s, whose *upper* case is two characters
            "src/ΙΣΩ.cs", // Greek capitals, one with a final-form lower case
            "src/Goal.cs",
        ];

        var corpus = new FileMentionCorpus(awkward);

        for (var i = 0; i < corpus.Count; i++)
            Assert.Equal(corpus.Paths[i].Length, corpus.Lowered[i].Length);
    }

    /// <summary>
    /// A path with such a character in it is scored rather than thrown over.
    /// </summary>
    /// <remarks>
    /// The end-to-end version of the two above: whatever the folding does, <c>Match</c> comes back with
    /// an answer. It ran on a task nobody awaits, so an exception here was unobserved — the popup froze
    /// and every keystroke after it did the same, with nothing on screen and nothing in a dialog.
    /// </remarks>
    [Theory]
    [InlineData("stanbul")]
    [InlineData("md")]
    [InlineData("docs/")]
    [InlineData("strasse")]
    public void An_awkward_name_is_scored_rather_than_thrown_over(string query)
    {
        string[] tree = ["docs/İstanbul.md", "docs/Straße.md", "src/Goal.cs"];

        var matched = FileMentionMatcher.Match(tree, query);

        Assert.DoesNotContain("src/Goal.cs", matched);
    }
}