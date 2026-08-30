using mTiles.Models;

namespace mTiles.Services;

/// <summary>One SOLID principle: its letter, what it is called, the line a prompt states it with, and
/// how to read and write its switch.</summary>
/// <param name="Letter">The single character the panel shows. It is also what makes the row readable at
/// a glance — five chips spelling SOLID, in that order.</param>
/// <param name="Name">The full name, for the tooltip.</param>
/// <param name="Rule">The bullet handed to the tool, without its leading dash. One line each: this is
/// fixed overhead in three prompts a lap, charged against a command-line budget.</param>
/// <param name="IsOn">Whether a given set of switches has this one on.</param>
/// <param name="SetOn">Turns this one on or off in a given set of switches.</param>
internal readonly record struct SolidPrinciple(
    string Letter,
    string Name,
    string Rule,
    Func<SolidPrinciples, bool> IsOn,
    Action<SolidPrinciples, bool> SetOn);

/// <summary>
/// The one map from a SOLID principle to everything anything here needs to know about it.
/// </summary>
/// <remarks>
/// Its own file, and a table rather than five of everything, because the alternative is the same five
/// members spelled out in four places — the model, the prompt text, the panel and the tests — where
/// three of them agreeing and one not is a switch that silently does nothing. The same shape as
/// <c>AiBehaviours</c> and <c>SpeechEngines</c>, for the same reason.
/// </remarks>
internal static class SolidPrincipleCatalog
{
    /// <summary>In the order that spells the acronym, because the panel shows the letters in a row and
    /// any other order would read as a mistake.</summary>
    public static readonly IReadOnlyList<SolidPrinciple> All =
    [
        new("S", "Single Responsibility",
            "Single Responsibility: each class and method has one reason to change",
            p => p.SingleResponsibility, (p, v) => p.SingleResponsibility = v),
        new("O", "Open/Closed",
            "Open/Closed: open for extension, closed for modification",
            p => p.OpenClosed, (p, v) => p.OpenClosed = v),
        new("L", "Liskov Substitution",
            "Liskov Substitution: a subtype must work anywhere its base type does",
            p => p.Liskov, (p, v) => p.Liskov = v),
        new("I", "Interface Segregation",
            "Interface Segregation: no caller depends on members it does not use",
            p => p.InterfaceSegregation, (p, v) => p.InterfaceSegregation = v),
        new("D", "Dependency Inversion",
            "Dependency Inversion: depend on abstractions, not on concrete implementations",
            p => p.DependencyInversion, (p, v) => p.DependencyInversion = v),
    ];
}
