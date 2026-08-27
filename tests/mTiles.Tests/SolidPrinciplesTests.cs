using System.Reflection;
using System.Text.Json;
using mTiles.Models;
using mTiles.Services;
using mTiles.ViewModels;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The per-goal SOLID switches: the model that holds them, the prompt text they produce, what a goal
/// file does to them, and the row on the criteria panel.
/// </summary>
public class SolidPrinciplesTests
{
    private static string Rules(SolidPrinciples solid) =>
        Builder(solid).BuildPlan("a goal", []);

    private static string Review(SolidPrinciples solid) =>
        Builder(solid).BuildReview("a goal", null);

    private static GoalPromptBuilder Builder(SolidPrinciples solid) =>
        new(() => new GoalCompletionCriteria { Solid = solid });

    /// <summary>
    /// Nobody who never opens the panel loses a rule. The switches exist to take something away, so
    /// their default has to be the behaviour that was there before them.
    /// </summary>
    [Fact]
    public void All_five_are_on_by_default()
    {
        var criteria = new GoalCompletionCriteria();

        Assert.All(SolidPrincipleCatalog.All, p => Assert.True(p.IsOn(criteria.Solid)));
        Assert.True(criteria.Solid.Any);
        Assert.False(criteria.Solid.Partial);
    }

    /// <summary>
    /// Every principle that is on is stated outright. The constant this replaced named two of the five
    /// and waved at the rest with "especially", which left the reviewer to decide for itself what it
    /// was reviewing against.
    /// </summary>
    [Fact]
    public void Every_principle_that_is_on_is_named_in_the_prompt()
    {
        var prompt = Rules(new SolidPrinciples());

        Assert.All(SolidPrincipleCatalog.All, p => Assert.Contains(p.Rule, prompt));
        // Nothing to exclude when nothing is excluded — the sentence would be noise in every prompt of
        // every run that never touched the row.
        Assert.DoesNotContain("out of scope", prompt);
    }

    /// <summary>
    /// A principle switched off is named as out of scope, not merely left out.
    /// <para>This is the half that does the work. A model reviewing C# reports a fat interface whether
    /// it was asked to or not, and the finding arrives as a warning against a tolerance of zero — so
    /// silence about a switched-off principle is not the same as switching it off.</para>
    /// </summary>
    [Fact]
    public void A_principle_that_is_off_is_named_as_out_of_scope()
    {
        var prompt = Rules(new SolidPrinciples { Liskov = false, InterfaceSegregation = false });

        Assert.Contains("Single Responsibility:", prompt);
        Assert.DoesNotContain("Liskov Substitution:", prompt);
        Assert.DoesNotContain("Interface Segregation:", prompt);
        Assert.Contains("The SOLID principles not listed are out of scope", prompt);
    }

    /// <summary>
    /// With none of them on there is no list for "the ones not listed" to point at, so the exclusion is
    /// stated over the whole of SOLID instead — and the one sentence that gives the reviewer a reason
    /// to reach for a warning stops naming it.
    /// </summary>
    [Fact]
    public void With_none_of_them_on_the_whole_of_solid_is_excluded()
    {
        var solid = new SolidPrinciples
        {
            SingleResponsibility = false,
            OpenClosed = false,
            Liskov = false,
            InterfaceSegregation = false,
            DependencyInversion = false,
        };

        var plan = Rules(solid);
        Assert.Contains("Clean Code principles", plan);
        Assert.Contains("SOLID principles are out of scope", plan);
        Assert.All(SolidPrincipleCatalog.All, p => Assert.DoesNotContain(p.Rule, plan));

        var review = Review(solid);
        Assert.Contains("or a Clean Code violation.", review);
        Assert.DoesNotContain("Clean Code / SOLID violation", review);
    }

    /// <summary>Clean Code is not one of the switches, and stays in every prompt that carries the
    /// rules.</summary>
    [Fact]
    public void Clean_code_is_asked_for_whatever_the_switches_say()
    {
        var off = new SolidPrinciples
        {
            SingleResponsibility = false,
            OpenClosed = false,
            Liskov = false,
            InterfaceSegregation = false,
            DependencyInversion = false,
        };

        foreach (var solid in new[] { new SolidPrinciples(), off })
        {
            Assert.Contains("Clean Code principles", Rules(solid));
            Assert.Contains("Clean Code principles", Review(solid));
        }
    }

    /// <summary>
    /// The builder reads the switches on every prompt rather than at construction.
    /// <para><c>GoalWorkflowEngine.Criteria</c> is replaced wholesale by every keystroke in the panel
    /// and the builder outlives it, so a value captured once would have a change to this row take
    /// effect on the next tile instead of the next attempt.</para>
    /// </summary>
    [Fact]
    public void A_switch_flipped_after_the_builder_was_made_reaches_the_next_prompt()
    {
        var criteria = new GoalCompletionCriteria();
        var builder = new GoalPromptBuilder(() => criteria);

        Assert.Contains("Liskov Substitution:", builder.BuildPlan("a goal", []));

        // Both halves of what the panel does: a new criteria object, carrying a new answer.
        criteria = criteria.Copy();
        criteria.Solid.Liskov = false;

        Assert.DoesNotContain("Liskov Substitution:", builder.BuildPlan("a goal", []));
    }

    /// <summary>
    /// A goal file written before this row existed comes back with all five on, which is what it ran
    /// under. An answer that <em>is</em> in the file wins, including a no.
    /// </summary>
    [Fact]
    public void A_goal_file_without_the_field_keeps_every_principle()
    {
        var older = JsonSerializer.Deserialize<GoalCompletionCriteria>(
            """{"MaxIterations":3}""", JsonDefaults.Options)!;
        Assert.All(SolidPrincipleCatalog.All, p => Assert.True(p.IsOn(older.Solid)));

        var partial = JsonSerializer.Deserialize<GoalCompletionCriteria>(
            """{"Solid":{"Liskov":false}}""", JsonDefaults.Options)!;
        Assert.False(partial.Solid.Liskov);
        Assert.True(partial.Solid.SingleResponsibility);

        // The rule everything deserialised here follows: a property initialiser does not survive an
        // explicit null, and what follows a null here is a NullReferenceException in the middle of
        // building a prompt.
        var nulled = JsonSerializer.Deserialize<GoalCompletionCriteria>(
            """{"Solid":null}""", JsonDefaults.Options)!;
        Assert.All(SolidPrincipleCatalog.All, p => Assert.True(p.IsOn(nulled.Solid)));
    }

    /// <summary>
    /// The goal file carries the five answers and nothing derived from them.
    /// </summary>
    /// <remarks>
    /// <c>Any</c> and <c>Partial</c> are questions about the five, not a sixth and seventh switch, and
    /// <c>System.Text.Json</c> writes a public getter whether or not anything can read it back. Left
    /// alone they went into every goal file in the user's own repository as two fields that look like
    /// settings, cannot be changed — no setter, so a hand-edited value is silently ignored — and would
    /// contradict the real ones the moment somebody tried. The same reason
    /// <c>GoalMessage.IsMarkdown</c> next door carries the attribute.
    /// </remarks>
    [Fact]
    public void Nothing_derived_is_written_to_the_goal_file()
    {
        var json = JsonSerializer.Serialize(new GoalCompletionCriteria(), JsonDefaults.Options);

        Assert.Contains("SingleResponsibility", json);
        Assert.DoesNotContain("\"Any\"", json);
        Assert.DoesNotContain("\"Partial\"", json);
    }

    /// <summary>Copy() exists so a caller can hold the criteria as they were; sharing the instance
    /// would let a later edit reach back into the snapshot.</summary>
    [Fact]
    public void Copying_the_criteria_copies_the_switches_rather_than_sharing_them()
    {
        var criteria = new GoalCompletionCriteria();
        var copy = criteria.Copy();

        copy.Solid.OpenClosed = false;

        Assert.True(criteria.Solid.OpenClosed);
    }

    /// <summary>
    /// The catalog is the one map, and this is the test that keeps it one. A sixth property on the
    /// model with no entry here would be a switch nothing shows and nothing writes; an entry whose
    /// getter and setter name different properties would be a chip that lights up the wrong letter.
    /// </summary>
    [Fact]
    public void The_catalog_covers_every_switch_on_the_model_exactly_once()
    {
        var properties = typeof(SolidPrinciples)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(bool) && p.CanWrite)
            .ToList();

        Assert.Equal(properties.Count, SolidPrincipleCatalog.All.Count);
        Assert.Equal("SOLID", string.Concat(SolidPrincipleCatalog.All.Select(p => p.Letter)));

        foreach (var principle in SolidPrincipleCatalog.All)
        {
            // One at a time, from all-on: the switch has to move its own property and no other.
            var all = new SolidPrinciples();
            principle.SetOn(all, false);

            Assert.False(principle.IsOn(all));
            Assert.Equal(1, properties.Count(p => !(bool)p.GetValue(all)!));
        }
    }

    /// <summary>The chips write through the same route as every other edit on the panel, and are
    /// refilled from the criteria rather than rebuilt — the row is bound to the list.</summary>
    [Fact]
    public void The_panel_writes_a_chip_through_and_reads_it_back()
    {
        var criteria = new GoalCompletionCriteria();
        var editor = new GoalCriteriaEditor(() => criteria, c => criteria = c);
        var chips = editor.Solid;

        Assert.Equal("SOLID", string.Concat(chips.Select(c => c.Letter)));
        Assert.All(chips, c => Assert.True(c.IsOn));

        chips[2].IsOn = false;
        Assert.False(criteria.Solid.Liskov);
        Assert.True(criteria.Solid.OpenClosed);

        // Reload happens for reasons that have nothing to do with this row — Continue reloads the panel
        // to show a raised attempt ceiling — so it has to put back what the criteria say, on the same
        // objects the row is bound to.
        criteria.Solid.Liskov = true;
        criteria.Solid.DependencyInversion = false;
        editor.Reload();

        Assert.Same(chips, editor.Solid);
        Assert.True(chips[2].IsOn);
        Assert.False(chips[4].IsOn);
    }

    /// <summary>
    /// Each chip writes the principle it carries, and no other.
    /// <para>The row and the catalog used to be walked in step by index, which is correct only while
    /// the two lists stay the same length in the same order — an invariant nothing stated and one
    /// insertion would have broken silently, lighting the wrong letters and writing the user's answer
    /// onto the wrong principles. This asks the question from the outside: turn one letter off, and the
    /// only switch that moves is the one that letter stands for.</para>
    /// </summary>
    [Fact]
    public void Each_chip_writes_its_own_principle_and_no_other()
    {
        for (var i = 0; i < SolidPrincipleCatalog.All.Count; i++)
        {
            var criteria = new GoalCompletionCriteria();
            var editor = new GoalCriteriaEditor(() => criteria, c => criteria = c);

            editor.Solid[i].IsOn = false;

            var off = SolidPrincipleCatalog.All.Where(p => !p.IsOn(criteria.Solid)).ToList();
            Assert.Equal(editor.Solid[i].Letter, Assert.Single(off).Letter);
        }
    }

    /// <summary>Filling the panel is not editing it: a reload must not write five times on the way to
    /// putting back what it just read.</summary>
    [Fact]
    public void Reloading_the_panel_does_not_count_as_an_edit()
    {
        var criteria = new GoalCompletionCriteria { Solid = { Liskov = false } };
        var writes = 0;
        var editor = new GoalCriteriaEditor(() => criteria, c => { criteria = c; writes++; });

        writes = 0;
        editor.Reload();

        Assert.Equal(0, writes);
    }
}
