namespace mTiles.ViewModels;

/// <summary>The three things dictation needs before it works, in the order they depend on each other.</summary>
public enum SpeechSetupStep
{
    /// <summary>Which model, and getting it onto the disk. Nothing else can be tried without one.</summary>
    Model,

    /// <summary>Which microphone. The one setting whose failure is silent — a wrong device produces no
    /// audio rather than an error.</summary>
    Microphone,

    /// <summary>Say something and see it come back. The only step that proves the other two.</summary>
    Test,
}

/// <summary>
/// The wizard's rules, with no view model, no service and no dispatcher attached.
/// </summary>
/// <remarks>
/// Small enough to look trivial, and worth having apart anyway: "which step comes next" and "may the
/// user leave this one" are the whole of what a wizard is, and here they can be read in a table test
/// rather than clicked through. The order is a dependency, not a preference — there is nothing to test
/// with no model, and choosing a microphone before there is anything to transcribe with is a form to
/// fill in rather than a step.
/// </remarks>
public static class SpeechSetupFlow
{
    private static readonly SpeechSetupStep[] Order =
        [SpeechSetupStep.Model, SpeechSetupStep.Microphone, SpeechSetupStep.Test];

    public static IReadOnlyList<SpeechSetupStep> Steps => Order;

    /// <summary>One-based, for "Step 2 of 3".</summary>
    public static int NumberOf(SpeechSetupStep step) => IndexOf(step) + 1;

    public static bool IsLast(SpeechSetupStep step) => step == Order[^1];

    public static SpeechSetupStep? Next(SpeechSetupStep step)
    {
        var index = IndexOf(step);
        return index >= 0 && index + 1 < Order.Length ? Order[index + 1] : null;
    }

    public static SpeechSetupStep? Previous(SpeechSetupStep step)
    {
        var index = IndexOf(step);
        return index > 0 ? Order[index - 1] : null;
    }

    private static int IndexOf(SpeechSetupStep step) => Array.IndexOf(Order, step);

    /// <summary>
    /// Whether the user may move on from <paramref name="step"/>.
    /// </summary>
    /// <param name="modelReady">A model chosen <em>and</em> on this machine.</param>
    /// <remarks>
    /// Only the model step blocks, and it blocks on the one condition the rest of the wizard depends on.
    /// The microphone step never blocks: the system default is a real answer, and an empty device list
    /// means this machine has no audio at all — a wizard that refuses to continue there would trap
    /// somebody with nothing they can do about it.
    /// </remarks>
    public static bool CanLeave(SpeechSetupStep step, bool modelReady) =>
        step != SpeechSetupStep.Model || modelReady;
}
