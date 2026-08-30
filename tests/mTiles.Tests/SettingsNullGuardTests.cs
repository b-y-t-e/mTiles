using System.Reflection;
using System.Text.Json;
using mTiles.Models;
using mTiles.Services;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// Every settings property that something dereferences refuses a null, whoever sets it.
/// </summary>
/// <remarks>
/// <para>A property initialiser does not survive deserialisation: <c>"Speech": null</c> in the file
/// overwrites the fresh object and is not an error, so the load's own catch never sees it. What follows
/// is a <c>NullReferenceException</c> while the main window is being built — the application does not
/// start, and says nothing about why.</para>
/// <para>By reflection rather than by a list, because the list is the part that rots. Somebody adding a
/// collection to <c>AppSettings</c> next year gets this test failing on their property rather than a
/// bug report from whoever hand-edited their settings file.</para>
/// </remarks>
public class SettingsNullGuardTests
{
    /// <summary>
    /// The settings objects that are read during startup, and everything they hold.
    /// </summary>
    /// <remarks>
    /// Walked from <see cref="AppSettings"/> rather than listed. The list was
    /// <c>[AppSettings, SpeechSettings, DatabaseSettings]</c> — the three that had been found the hard
    /// way — and it stopped one level short of <c>PostgreSqlDiscoverySettings.Ports</c>, which is walked
    /// with a bare <c>foreach</c> by the discovery scan and read by the settings view model while the
    /// main window is being built. A <c>"Ports": null</c> was therefore not a database that fails to be
    /// found; it was an application that does not start, sitting one hop below where anybody was looking.
    /// A list of the mistakes already made is not a test of the ones available.
    /// </remarks>
    public static TheoryData<Type> SettingsTypes => [.. Reachable(typeof(AppSettings))];

    /// <summary>Every settings type reachable from <paramref name="root"/> by following properties that
    /// hold one.</summary>
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
                {
                    // Only our own settings objects are descended into. The framework's own types are not
                    // ours to guard: a List<> or a Dictionary<> is checked as a property of its owner.
                    if (held.Namespace == typeof(AppSettings).Namespace && !held.IsEnum && !held.IsValueType)
                        queue.Enqueue(held);
                }
        }

        // Only what can be built without arguments, which every settings object can — anything else would
        // be a type this test cannot instantiate rather than one it has decided to skip.
        return seen.Where(t => t.GetConstructor(Type.EmptyTypes) is not null);
    }

    /// <summary>The type itself, and what it holds if it is a collection of something.</summary>
    /// <remarks>
    /// <c>List&lt;UserShellProfile&gt;</c> has to lead to <c>UserShellProfile</c>, or the walk stops at
    /// every collection and the four types that are only ever reached through one — the shell profiles,
    /// the custom AI tools, the manual database connections — go unchecked. Which is where the first
    /// version of this walk stopped, having replaced a list of three types with a loop that found five.
    /// </remarks>
    private static IEnumerable<Type> Held(Type type)
    {
        yield return type;

        if (type.IsArray && type.GetElementType() is { } element)
            yield return element;

        if (type.IsGenericType)
            foreach (var argument in type.GetGenericArguments())
                yield return argument;
    }

    /// <summary>The walk has to reach past the first level, or it is the old list wearing a loop.</summary>
    [Fact]
    public void The_walk_reaches_the_types_that_hide_a_level_down()
    {
        var reached = Reachable(typeof(AppSettings)).ToList();

        Assert.Contains(typeof(DatabaseSettings), reached);
        Assert.Contains(typeof(PostgreSqlDiscoverySettings), reached);   // AppSettings → Database → here
        Assert.Contains(typeof(SpeechSettings), reached);
    }

    [Theory]
    [MemberData(nameof(SettingsTypes))]
    public void Null_is_refused_by_every_reference_property(Type type)
    {
        var instance = Activator.CreateInstance(type)!;

        foreach (var property in Guarded(type, includeStrings: false))
        {
            property.SetValue(instance, null);

            Assert.True(property.GetValue(instance) is not null,
                $"{type.Name}.{property.Name} accepted a null — a settings file saying so stops the " +
                "application from starting. Guard it in the setter, as its neighbours are.");
        }
    }

    /// <summary>
    /// The dictation settings are held to the stricter rule: their strings are refused a null too.
    /// </summary>
    /// <remarks>
    /// <para>Elsewhere a null string is survivable — those are compared, bound to a control, or passed
    /// to something that treats null as absent. Here they are taken apart: <c>Language</c> is split to
    /// its base code on the way into the transcript cleaner and handed to whisper as the language to
    /// decode in, <c>Hotkey</c> is parsed, and the other two go to a file path and a device lookup.</para>
    /// <para>A <c>"Language": null</c> in the settings file used to throw inside the transcription
    /// pipeline's own catch: the application ran perfectly well and every dictated sentence came back as
    /// "Transcription failed: Object reference not set", with nothing on screen connecting that to a
    /// settings file.</para>
    /// </remarks>
    [Fact]
    public void Speech_strings_refuse_a_null_as_well()
    {
        var speech = new SpeechSettings();

        foreach (var property in Guarded(typeof(SpeechSettings), includeStrings: true))
        {
            property.SetValue(speech, null);

            Assert.True(property.GetValue(speech) is not null,
                $"SpeechSettings.{property.Name} accepted a null. These are parsed and split rather " +
                "than merely compared, so a settings file saying so fails every dictation instead of " +
                "reporting anything a user could act on.");
        }

        Assert.Equal("auto", speech.Language);
    }

    /// <summary>
    /// The nullable strings in the settings, which is a list somebody has to have looked at.
    /// </summary>
    /// <remarks>
    /// <para>Two rules meet here and they say opposite things. This file skips a property declared
    /// <c>string?</c>, on the grounds that the annotation is a promise that null is an answer.
    /// <c>NullToEmptyStringConverter</c> turns <em>every</em> null string in the settings file into an
    /// empty one, annotation or not — and it cannot do otherwise: a <c>JsonConverter&lt;string&gt;</c> is
    /// chosen by type and is never told which property it is filling.</para>
    /// <para>So the converter wins for anything read from the file, and the annotation survives only for
    /// what code assigns directly. That is harmless for both of these — each is read through
    /// <c>IsNullOrEmpty</c>, so empty and null are the same answer — but it is harmless by inspection
    /// rather than by construction, and inspection does not repeat itself. Pinning the list is what makes
    /// the next <c>string?</c> in the settings arrive as a failing test asking whether its author meant
    /// null to mean something.</para>
    /// </remarks>
    [Fact]
    public void The_settings_strings_that_may_be_null_are_the_ones_that_have_been_looked_at()
    {
        var nullability = new NullabilityInfoContext();

        var nullable = Reachable(typeof(AppSettings))
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanWrite && p.PropertyType == typeof(string))
                .Where(p => nullability.Create(p).WriteState == NullabilityState.Nullable)
                .Select(p => $"{t.Name}.{p.Name}"))
            .OrderBy(name => name, StringComparer.Ordinal);

        Assert.Equal(
            ["AppSettings.LastWorkspaceId", "AppSettings.LegacyCustomShellArgs",
             "AppSettings.LegacyCustomShellPath", "UserShellProfile.RequiredAiToolBinaryName"],
            nullable);
    }

    /// <summary>And the converter really does overrule the annotation, rather than this being a worry
    /// about something that does not happen.</summary>
    [Fact]
    public void A_nullable_string_in_the_file_still_arrives_empty()
    {
        var json = """
            {
              "LastWorkspaceId": null,
              "ShellProfiles": [ { "Name": "mine", "RequiredAiToolBinaryName": null } ]
            }
            """;

        var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonDefaults.SettingsOptions)!;

        Assert.Equal("", settings.LastWorkspaceId);
        Assert.Equal("", settings.ShellProfiles.Single(p => p.Name == "mine").RequiredAiToolBinaryName);
    }

    /// <summary>Something with a settable reference type: a collection, another settings object, and —
    /// where <paramref name="includeStrings"/> says so — a string. Properties declared nullable are left
    /// alone here: <c>string?</c> is a promise that null is an answer, and the two tests above are where
    /// that promise is reconciled with the converter that breaks it.</summary>
    private static IEnumerable<PropertyInfo> Guarded(Type type, bool includeStrings)
    {
        var nullability = new NullabilityInfoContext();

        return type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite)
            .Where(p => !p.PropertyType.IsValueType)
            .Where(p => includeStrings || p.PropertyType != typeof(string))
            .Where(p => nullability.Create(p).WriteState != NullabilityState.Nullable);
    }

    /// <summary>
    /// The reflection above has to actually find something, or this file passes for ever while testing
    /// nothing.
    /// </summary>
    /// <remarks>
    /// Asked of the whole walk rather than of each type: a settings object made only of numbers and
    /// strings — <c>SqlServerDiscoverySettings</c> is one — has nothing for this rule to check, and that
    /// is a fact about it rather than a hole in the scan.
    /// </remarks>
    [Fact]
    public void The_scan_finds_properties_to_check()
        => Assert.NotEmpty(Reachable(typeof(AppSettings)).SelectMany(t => Guarded(t, includeStrings: false)));
}
