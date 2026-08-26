using System.Text.Json.Serialization;

namespace mTiles.Models;

/// <summary>
/// Which of the five SOLID principles this goal's work is held to.
/// </summary>
/// <remarks>
/// <para>All five, unless the user says otherwise. The rules go into the plan, implement and review
/// prompts, and the review is asked to report a violation as a warning — which with the panel's default
/// tolerance of zero warnings is a hard gate. So switching one off is not advice the tool is free to
/// ignore: it is the difference between a run that finishes and a run that spends its remaining attempts
/// arguing about an abstraction the user does not want.</para>
/// <para>Five plain booleans rather than a <c>[Flags]</c> enum, and that is a serialisation decision
/// rather than a stylistic one. Enums in this application's files are written as names
/// (<c>JsonDefaults</c> registers <c>JsonStringEnumConverter</c>), and the tolerant readers that stop an
/// unknown name destroying a goal file are built on <c>Enum.IsDefined</c> — which answers <b>false</b>
/// for every combination of flags that is not itself a declared member. The one protection this file
/// already has could not have covered the one field that needed it.</para>
/// <para>A missing property keeps its initialiser, so a goal file written before this existed comes back
/// with all five on, which is what it ran under. A property that <em>is</em> in the file wins, including
/// when it is false — that is the user's answer.</para>
/// <para>A <c>record</c> rather than a class, for the equality. It is the one thing on
/// <c>GoalCompletionCriteria</c> that is not a number or a string, and that object is walked by
/// reflection and compared property by property to prove <c>Copy</c> forgot nothing — a check that is
/// worth having precisely because a property left out of <c>Copy</c> fails nothing else and quietly
/// resets itself every time the tile is reopened. Under reference equality this property would pass
/// that check only by accident, when the copy shared the instance, which is the bug and not the
/// proof.</para>
/// </remarks>
public sealed record SolidPrinciples
{
    public bool SingleResponsibility { get; set; } = true;
    public bool OpenClosed { get; set; } = true;
    public bool Liskov { get; set; } = true;
    public bool InterfaceSegregation { get; set; } = true;
    public bool DependencyInversion { get; set; } = true;

    /// <summary>Whether anything here is on at all. When nothing is, the prompts say so out loud rather
    /// than falling silent: a model told nothing about SOLID reviews against it anyway.</summary>
    /// <remarks>
    /// Not written to the goal file. It is a question about the five above, not a sixth switch, and
    /// <c>System.Text.Json</c> writes any public getter whether or not it can read one back — so
    /// without this it went into every goal file in the user's repository looking like a setting that
    /// could be changed, while a hand-edited value would be silently ignored and contradict the
    /// switches beside it.
    /// </remarks>
    [JsonIgnore]
    public bool Any =>
        SingleResponsibility || OpenClosed || Liskov || InterfaceSegregation || DependencyInversion;

    /// <summary>Whether some are on and some are off — the only case in which the prompt has to name
    /// what is out of scope.</summary>
    /// <remarks>Derived, and not written to the file — see <see cref="Any"/>.</remarks>
    [JsonIgnore]
    public bool Partial =>
        Any && !(SingleResponsibility && OpenClosed && Liskov
                 && InterfaceSegregation && DependencyInversion);

    /// <summary>A copy nothing else holds a reference to.</summary>
    public SolidPrinciples Copy() => this with { };
}
