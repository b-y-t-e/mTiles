using System.Reflection;
using System.Text.Json;
using mTiles.Models;
using mTiles.Services;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// Every property of a saved goal refuses a null, whoever sets it.
/// </summary>
/// <remarks>
/// <para>The same rule and the same reasoning as <see cref="SettingsNullGuardTests"/>, applied to the
/// other file this application writes. A property initialiser does not survive deserialisation, so
/// <c>"Messages": null</c> replaces the fresh list and is not an error anything would notice until
/// <c>GoalWorkflowEngine.LoadFrom</c> dereferences it.</para>
/// <para>What made it worse than the settings case is where it landed: in the view model's catch of
/// last resort, which refuses to save for the rest of the tile's life. A goal file with one null in it
/// was therefore treated more harshly than a goal file of corrupt bytes, which is at least set aside so
/// the tile can start again.</para>
/// <para>Walked rather than listed, because the list is the part that rots.</para>
/// </remarks>
public class GoalStateNullGuardTests
{
    public static TheoryData<Type> GoalTypes => [.. Reachable(typeof(GoalTileState))];

    [Fact]
    public void The_walk_reaches_what_a_state_holds_rather_than_only_the_state()
    {
        // GoalMessage is only ever reached through List<GoalMessage>, which is where a walk that does
        // not look inside collections stops.
        Assert.Contains(typeof(GoalMessage), Reachable(typeof(GoalTileState)));
    }

    [Theory]
    [MemberData(nameof(GoalTypes))]
    public void Null_is_refused_by_every_reference_property(Type type)
    {
        var instance = Activator.CreateInstance(type)!;

        foreach (var property in Guarded(type))
        {
            property.SetValue(instance, null);

            Assert.True(property.GetValue(instance) is not null,
                $"{type.Name}.{property.Name} accepted a null. A goal file saying so throws where " +
                "nothing expects it and costs the tile its ability to save. Guard it in the setter, " +
                "as its neighbours are.");
        }
    }

    [Fact]
    public void A_null_inside_a_list_is_refused_as_well_as_a_null_list()
    {
        // The level the guards did not cover. "Messages": null was handled; ["a", null] was not, and it
        // reached GoalWorkflowEngine.LoadFrom — where every clarification turn is labelled with
        // StartsWith — and threw inside the view model's catch of last resort, which stops the tile
        // saving for the rest of its life.
        const string hostile = """
            {
              "OriginalGoal": "a goal",
              "ClarificationHistory": ["User: appsettings.json", null],
              "AttemptLog": [null, "Attempt 1: did a thing"],
              "Messages": [
                null,
                { "Role": "User", "Text": "hello" },
                {
                  "Role": "Assistant",
                  "Text": "a round",
                  "Questions": [null, { "Question": "Which?", "Options": [null, "a"] }],
                  "Findings": [null, { "Title": "null deref" }]
                }
              ],
              "PendingQuestions": [null, { "Question": "Which?", "Options": ["a", null] }],
              "CurrentPhase": "Clarify"
            }
            """;

        var state = JsonSerializer.Deserialize<GoalTileState>(hostile, JsonDefaults.Options)!;

        // Two levels down, which is where this keeps going wrong: the walk covers properties, and a
        // list inside a list element is still named by hand. A null in a question's options ends as a
        // NullReferenceException in GoalQuestionAnswer's constructor or in GoalTranscript.Questions —
        // inside the view model's catch of last resort, which stops the tile saving for good.
        var question = Assert.Single(state.PendingQuestions);
        Assert.Equal(["a"], question.Options);

        Assert.Single(state.ClarificationHistory);
        Assert.Single(state.AttemptLog);
        Assert.Equal(2, state.Messages.Count);
        Assert.All(state.Messages, m => Assert.NotNull(m));

        // A message carries two lists of its own, and both are that same third level: a list inside an
        // element of a list. Neither is reached by the property walk, so both are named here — and the
        // round is the one that arrived last, which is exactly the case this test exists to catch
        // before it is a tile that cannot save.
        var round = state.Messages[^1];
        Assert.Equal(["Which?"], round.Questions.Select(q => q.Question));
        Assert.Equal(["a"], Assert.Single(round.Questions).Options);
        Assert.Equal(["null deref"], round.Findings.Select(f => f.Title));

        var engine = new GoalWorkflowEngine();
        engine.LoadFrom(state);

        Assert.Single(engine.ClarificationHistory);
        Assert.Single(engine.AttemptLog);
    }

    [Fact]
    public void A_phase_or_role_from_a_newer_build_costs_one_field_and_not_the_session()
    {
        // Enums are written as names, so a name this build has never heard of is a JsonException — read
        // by the persistence layer as a damaged file and set aside. The ordinary way one gets in is a
        // downgrade, and the message's own Phase is the likeliest carrier of the three, because there
        // is one per line of transcript rather than one per file.
        const string fromTheFuture = """
            {
              "OriginalGoal": "a goal",
              "CurrentPhase": "Rehearsing",
              "LastStopReason": "SomethingInventedLater",
              "Messages": [{ "Role": "Narrator", "Text": "hello", "Phase": "Rehearsing" }]
            }
            """;

        var state = JsonSerializer.Deserialize<GoalTileState>(fromTheFuture, JsonDefaults.Options)!;

        Assert.Equal("a goal", state.OriginalGoal);
        Assert.Equal(GoalPhase.Goal, state.CurrentPhase);
        Assert.Null(state.LastStopReason);

        var message = Assert.Single(state.Messages);
        Assert.Equal("hello", message.Text);
        Assert.Equal(GoalMessageRole.System, message.Role);
        Assert.Equal(GoalPhase.Goal, message.Phase);
    }

    [Fact]
    public void A_file_full_of_nulls_loads_into_something_usable()
    {
        // The end the guards exist for, stated as the thing a user would notice: a hand-edited or
        // half-written file opens a tile that works, rather than one that has quietly stopped saving.
        const string hostile = """
            {
              "OriginalGoal": null,
              "ClarificationHistory": null,
              "ApprovedPlan": null,
              "SelectedToolName": null,
              "SelectedModel": null,
              "Messages": null,
              "CurrentPhase": "Review",
              "IterationCount": 2
            }
            """;

        var state = JsonSerializer.Deserialize<GoalTileState>(hostile, JsonDefaults.Options)!;

        Assert.Equal("", state.OriginalGoal);
        Assert.Empty(state.ClarificationHistory);
        Assert.Empty(state.Messages);

        // And the engine reads it without throwing, which is the step that used to.
        var engine = new GoalWorkflowEngine();
        engine.LoadFrom(state);

        Assert.Equal(GoalPhase.Review, engine.CurrentPhase);
        Assert.True(engine.IsPaused);
    }

    /// <summary>Every goal type reachable from <paramref name="root"/>, collections looked inside.</summary>
    private static IEnumerable<Type> Reachable(Type root)
    {
        var seen = new HashSet<Type>();
        var queue = new Queue<Type>([root]);

        while (queue.Count > 0)
        {
            var type = queue.Dequeue();
            if (!seen.Add(type))
                continue;

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                foreach (var held in Held(property.PropertyType))
                    if (held.Namespace == typeof(GoalTileState).Namespace && !held.IsEnum && !held.IsValueType)
                        queue.Enqueue(held);
        }

        return seen.Where(t => t.GetConstructor(Type.EmptyTypes) is not null);
    }

    private static IEnumerable<Type> Held(Type type)
    {
        yield return type;

        if (type.IsArray && type.GetElementType() is { } element)
            yield return element;

        if (type.IsGenericType)
            foreach (var argument in type.GetGenericArguments())
                yield return argument;
    }

    /// <summary>
    /// The properties this rule applies to: everything settable that can hold a null, except the ones
    /// declared nullable on purpose. <c>LastReviewFeedback</c> is the only such property — null there
    /// means the last review had nothing to say, and it is read through a null check rather than taken
    /// apart.
    /// </summary>
    private static IEnumerable<PropertyInfo> Guarded(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite)
            .Where(p => !p.PropertyType.IsValueType)
            .Where(p => new NullabilityInfoContext().Create(p).WriteState != NullabilityState.Nullable);

    /// <summary>
    /// Which messages are markdown survives a save and a load.
    /// </summary>
    /// <remarks>
    /// The flag decides how a message is drawn, so a round trip that loses it re-flows a review's
    /// columns on the first restart — which is the failure the flag was turned round to prevent, and it
    /// would come back through persistence instead of through the default.
    /// </remarks>
    [Fact]
    public void Whether_a_message_is_markdown_survives_the_file()
    {
        var saved = new GoalTileState
        {
            OriginalGoal = "a goal",
            Messages =
            [
                new GoalMessage { Role = GoalMessageRole.Assistant, Text = "## Plan", Markdown = true },
                new GoalMessage { Role = GoalMessageRole.Assistant, Text = "error  a.cs:1" },
                new GoalMessage { Role = GoalMessageRole.User, Text = "*mine*" },
            ],
        };

        var loaded = JsonSerializer.Deserialize<GoalTileState>(
            JsonSerializer.Serialize(saved, JsonDefaults.Options), JsonDefaults.Options)!;

        Assert.True(loaded.Messages[0].IsMarkdown);
        Assert.False(loaded.Messages[1].IsMarkdown);
        Assert.False(loaded.Messages[2].IsMarkdown);

        // A file written before the flag existed has no field at all, and must read as the behaviour it
        // was written under: no markdown, whatever the role.
        const string old = """
            {"OriginalGoal":"a goal","Messages":[{"Role":"Assistant","Text":"error  a.cs:1"}]}
            """;

        var older = JsonSerializer.Deserialize<GoalTileState>(old, JsonDefaults.Options)!;
        Assert.False(older.Messages[0].IsMarkdown);
    }
}
