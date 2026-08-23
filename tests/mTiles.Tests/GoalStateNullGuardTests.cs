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
}
