using System.Text.Json;
using System.Text.Json.Serialization;
using mTiles.Models;

namespace mTiles.Services;

/// <summary>
/// Reading an enum out of a file without letting an unrecognised value destroy the file.
/// </summary>
/// <remarks>
/// <para>Enums in this application's files are written as <em>names</em> (<see cref="JsonDefaults"/>
/// registers <see cref="JsonStringEnumConverter"/>), and a name no longer in the enum is a
/// <see cref="JsonException"/> — which <c>GoalStatePersistence</c> correctly treats as a damaged file,
/// moves aside as <c>.bad-…</c>, and starts the tile empty over. That is right for a truncated write and
/// badly wrong here: the ordinary way an unknown name gets into a goal file is a <b>downgrade</b>. A
/// newer build writes a value this one has never heard of, the user rolls back a version, and the whole
/// session is deleted for holding a field it will not use for anything.</para>
/// <para>"Stable enum" is not an argument against this. <c>GoalStopReason</c> looked stable and gained a
/// fifth member; <c>GoalSeverity</c> looked stable and gained a fourth.</para>
/// <para>Numbers are read too, since a hand-edited file may hold one, and are subject to the same rule:
/// a number outside the enum is unknown, not a cast. <c>Enum.IsDefined</c> is the check, because the
/// cast itself succeeds for any number at all.</para>
/// </remarks>
internal static class TolerantEnum
{
    /// <summary>The member this token names, or null when nothing here knows it.</summary>
    internal static T? Parse<T>(ref Utf8JsonReader reader) where T : struct, Enum
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return Enum.TryParse<T>(reader.GetString(), ignoreCase: true, out var parsed)
                       && Enum.IsDefined(parsed)
                    ? parsed
                    : null;

            case JsonTokenType.Number when reader.TryGetInt64(out var number):
                var value = (T)Enum.ToObject(typeof(T), number);
                return Enum.IsDefined(value) ? value : null;

            default:
                // Anything else — an object, an array — is not a mistyped member, it is a different
                // shape. Skipped rather than thrown for the same reason as above: one property is worth
                // less than the transcript beside it.
                reader.Skip();
                return null;
        }
    }
}

/// <summary>
/// A nullable enum that reads an unrecognised value as <c>null</c> instead of throwing.
/// </summary>
/// <remarks>
/// Null rather than a default member, and that is the whole reason this one is nullable-only: null is
/// already the file's way of saying "nothing has been recorded here", so an unreadable value degrades
/// into the one state that claims nothing and offers nothing. Falling back to the first member would
/// have the tile assert something specific about a run it cannot describe.
/// </remarks>
internal sealed class TolerantEnumConverter<T> : JsonConverter<T?> where T : struct, Enum
{
    public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Null ? null : TolerantEnum.Parse<T>(ref reader);

    public override void Write(Utf8JsonWriter writer, T? value, JsonSerializerOptions options)
    {
        if (value is { } present)
            writer.WriteStringValue(present.ToString());
        else
            writer.WriteNullValue();
    }
}

/// <summary>
/// A non-nullable enum that reads an unrecognised value as a named fallback instead of throwing.
/// </summary>
/// <remarks>
/// The fallback has to be chosen rather than defaulted, because "the first member" is an accident of
/// declaration order and these are read into properties that mean something. Each subclass names one and
/// says why: what matters is that the value is <em>survivable</em>, not that it is right, because the
/// alternative on the other side of this is losing the whole file.
/// </remarks>
internal abstract class TolerantEnumOrDefaultConverter<T> : JsonConverter<T> where T : struct, Enum
{
    protected abstract T Fallback { get; }

    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        TolerantEnum.Parse<T>(ref reader) ?? Fallback;

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}

/// <summary>A phase this build does not know reads as <see cref="GoalPhase.Goal"/> — the tile comes back
/// with its transcript and waiting for a goal, rather than not coming back at all.</summary>
internal sealed class TolerantGoalPhaseConverter : TolerantEnumOrDefaultConverter<GoalPhase>
{
    protected override GoalPhase Fallback => GoalPhase.Goal;
}

/// <summary>A role this build does not know reads as <see cref="GoalMessageRole.System"/>: a line in the
/// transcript attributed to the tile is the least misleading place to put something whose speaker cannot
/// be identified, and it is never fed back to a tool as the user's words or the tool's own.</summary>
internal sealed class TolerantGoalMessageRoleConverter : TolerantEnumOrDefaultConverter<GoalMessageRole>
{
    protected override GoalMessageRole Fallback => GoalMessageRole.System;
}
