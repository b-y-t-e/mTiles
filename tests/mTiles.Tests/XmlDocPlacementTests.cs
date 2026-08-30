using System.Text.RegularExpressions;
using System.Runtime.CompilerServices;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// No documentation block describes two members.
/// </summary>
/// <remarks>
/// <para>Inserting a member above an existing one puts the new member's <c>///</c> lines between the
/// old block and the member it belongs to. The result compiles, produces no warning — this project
/// does not generate a documentation file, and a doubled <c>&lt;summary&gt;</c> is not a diagnostic
/// even where one is generated — and leaves one member carrying somebody else's reasoning while the
/// member the reader came for has none.</para>
/// <para>It happened three times in one afternoon, to <c>CaptureBaselineAsync</c>,
/// <c>CaptureEndAsync</c> and <c>Held</c>, and each time it was found by a person reading the file.
/// In a codebase where the comment carries the reason a decision was made, that is a defect with no
/// symptom: the code is right and the explanation beside it is about something else.</para>
/// </remarks>
public class XmlDocPlacementTests
{
    /// <summary>
    /// Every contiguous run of <c>///</c> lines belongs to one member, so it has one of each tag.
    /// </summary>
    /// <remarks>
    /// <c>&lt;remarks&gt;</c> as well as <c>&lt;summary&gt;</c>, and that is not symmetry for its own
    /// sake: a block can be spliced in with its summary merged into the one above and its remarks left
    /// doubled, which is what this test missed while it counted summaries alone. Both tags are
    /// once-per-member; <c>&lt;param&gt;</c> and <c>&lt;para&gt;</c> are not, and are left alone.
    /// </remarks>
    [Theory]
    [InlineData("<summary>")]
    [InlineData("<remarks>")]
    public void No_documentation_block_carries_two_of_one_tag(string tag)
    {
        var offenders = new List<string>();

        foreach (var file in SourceFiles())
        {
            var line = 0;
            var runStart = 0;
            var summaries = 0;

            foreach (var text in File.ReadLines(file))
            {
                line++;
                var trimmed = text.TrimStart();

                if (trimmed.StartsWith("///", StringComparison.Ordinal))
                {
                    if (summaries == 0 && runStart == 0) runStart = line;
                    if (trimmed.Contains(tag, StringComparison.Ordinal)) summaries++;
                    continue;
                }

                // `[Fact]`, `[Theory]`, `[ObservableProperty]` and the like sit between the block and
                // the member and do not end it. A **blank line does**: the compiler attaches only the
                // run immediately above a member, so two blocks with a gap between them are two
                // members' documentation and not one block with two summaries.
                if (trimmed.StartsWith('[')) continue;

                if (summaries > 1) offenders.Add($"{Path.GetFileName(file)}:{runStart}");

                summaries = 0;
                runStart = 0;
            }
        }

        Assert.True(offenders.Count == 0,
            "a documentation block describes two members — the first summary belongs to a member "
            + "somewhere below it, which now has none:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>Both projects, since the rule is about how the files are read, not what they do.</summary>
    private static IEnumerable<string> SourceFiles()
    {
        var root = Root();

        foreach (var directory in new[] { "src", "tests" })
            foreach (var file in Directory.EnumerateFiles(
                         Path.Combine(root, directory), "*.cs", SearchOption.AllDirectories))
                if (!file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                    && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                    yield return file;
    }

    /// <summary>The repository root, taken from this file's own compile-time path.</summary>
    /// <remarks>
    /// Not <c>AppContext.BaseDirectory</c>. The output directory moves — a build redirected with
    /// <c>BaseOutputPath</c> lands outside the repository altogether, and walking up from there finds
    /// no <c>src</c> and fails the test for a reason that has nothing to do with what it checks.
    /// <c>CallerFilePath</c> is filled in by the compiler from the source tree being built, so it is
    /// right on a developer's machine and on a build agent with its own checkout alike.
    /// </remarks>
    private static string Root([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    /// <summary>
    /// Every <c>cref</c> naming a type of ours names a member that exists on it.
    /// </summary>
    /// <remarks>
    /// <para>Renaming a member leaves every <c>&lt;see cref="Type.Member"/&gt;</c> pointing at nothing,
    /// and nothing says so: this project generates no documentation file, so the compiler never
    /// resolves a <c>cref</c> at all. <c>IAiAgent.EffortFlag</c> and <c>PermissionFlag</c> were
    /// both dead this way within a day of becoming <c>…For</c> methods — a comment describing something
    /// that does not exist, in a codebase where the comment carries the reason.</para>
    /// <para><b>Conservative on purpose.</b> Only the simple <c>Type.Member</c> form is checked, only
    /// where the type is one of ours and is unambiguous by simple name, and anything else is skipped —
    /// a test that guesses would fail on the day somebody writes a perfectly good reference to a
    /// framework type.</para>
    /// </remarks>
    [Fact]
    public void No_cref_names_a_member_that_does_not_exist()
    {
        var ours = typeof(mTiles.Services.FileMentionMatcher).Assembly.GetTypes()
            .GroupBy(t => t.Name)
            .Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.Single());

        var offenders = new List<string>();

        foreach (var file in SourceFiles())
        foreach (var reference in References(File.ReadAllText(file)))
        {
            var dot = reference.LastIndexOf('.');
            if (dot <= 0) continue;

            var typeName = reference[..dot];
            var member = reference[(dot + 1)..];

            // Only a bare `Type.Member`: anything with a namespace, a generic argument or a nested type
            // is outside what this can resolve without guessing.
            if (typeName.Contains('.') || typeName.Contains('{')) continue;
            if (!ours.TryGetValue(typeName, out var type)) continue;

            if (type.GetMember(member,
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static
                    | System.Reflection.BindingFlags.FlattenHierarchy).Length == 0)
                offenders.Add($"{Path.GetFileName(file)}: {typeName}.{member}");
        }

        Assert.True(offenders.Count == 0,
            "a cref names a member that is not there: " + string.Join(" | ", offenders));
    }

    /// <summary>The <c>cref</c> targets in a file, with any parameter list stripped.</summary>
    private static IEnumerable<string> References(string source)
    {
        foreach (Match match in Regex.Matches(source, "cref=\"([^\"]+)\""))
        {
            var target = match.Groups[1].Value;

            // `Match(IReadOnlyList{string}, string, int)` is a reference to `Match`.
            var parameters = target.IndexOf('(');
            if (parameters > 0) target = target[..parameters];

            // `T:`, `M:` and friends are explicit-id crefs and name their own type in full.
            if (target.Length > 1 && target[1] == ':') continue;

            yield return target.Trim();
        }
    }

    /// <summary>
    /// No source file holds a stray control character.
    /// </summary>
    /// <remarks>
    /// <para>A tab or a form feed inside a verbatim string is a literal control character, and it reads
    /// on screen as the escape it is not: <c>@"C:\shots</c> followed by a real tab and <c>wo.png"</c>
    /// looks exactly like the same path written with an escape. It happened to eight lines of
    /// <c>GoalImageAttachmentTests</c> — a non-verbatim string turned verbatim after its escapes had
    /// already been expanded — and the tests went on passing, because a fixture that is wrong the same
    /// way in the data and in the assertion is internally consistent.</para>
    /// <para>What makes it worth a test rather than a fix is the next step: any tool that normalises
    /// whitespace, and any editor set to strip tabs, silently changes what those tests assert. The file
    /// would still compile and still pass, about something else.</para>
    /// <para>Tab, form feed and vertical tab, anywhere in the file rather than only inside a string:
    /// this codebase indents with spaces, so one of those in a <c>.cs</c> file is either this bug or a
    /// formatting slip. A literal <c>ESC</c> or <c>Ctrl+C</c> is left alone, because elsewhere in this
    /// assembly those <em>are</em> the test data and neither pretends to be an escape sequence nor is
    /// disturbed by tidying whitespace. It caught itself, too: the first version of this very test was
    /// written with the same collapsed escapes.</para>
    /// </remarks>
    [Fact]
    public void No_source_file_holds_a_stray_control_character()
    {
        var offenders = new List<string>();

        foreach (var file in SourceFiles())
        {
            var line = 0;
            foreach (var text in File.ReadLines(file))
            {
                line++;

                // Only the control characters that are *also whitespace* — tab, form feed, vertical
                // tab. A literal ESC or Ctrl+C is deliberate test data elsewhere in this assembly (the
                // dictation sink and the pairing tests are about exactly those bytes), and neither
                // masquerades as an escape sequence nor is disturbed by a tool that tidies whitespace.
                //
                // Written as a predicate rather than a list of escapes on purpose: the escapes are what
                // this test exists to catch, and writing them here is how its first version caught
                // itself.
                foreach (var c in text)
                    if (char.IsControl(c) && char.IsWhiteSpace(c))
                    {
                        offenders.Add($"{Path.GetFileName(file)}:{line} U+{(int)c:X4}");
                        break;
                    }
            }
        }

        Assert.True(offenders.Count == 0,
            "a control character is sitting in the source, where it reads as the escape it is not: "
            + string.Join(", ", offenders));
    }
}