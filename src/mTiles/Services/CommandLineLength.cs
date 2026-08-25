namespace mTiles.Services;

/// <summary>
/// What a Windows command line can carry, as arithmetic.
/// <para>Its own type because two very different callers need the same two numbers, and neither should
/// have to know the other. <see cref="AiProcessRunner"/> asks in order to refuse a prompt that will not
/// go; <c>GoalPromptBuilder</c> — which is documented as pure and knows nothing about tools — asks in
/// order to build a smaller one instead. Having the builder reach into the process runner for it made
/// that claim false the moment it was written.</para>
/// </summary>
internal static class CommandLineLength
{
    /// <summary>
    /// The length the command line will actually carry: the argument is wrapped and every quote and
    /// backslash inside it is escaped, so a prompt of code — which is what this carries — grows on the
    /// way onto it. Measuring the raw string let one through that then threw.
    /// </summary>
    public static int Quoted(string text) =>
        text.Length + text.Count(c => c is '"' or '\\') + 2;

    /// <summary>
    /// How many characters of argument this executable can be given, or <c>null</c> when the question
    /// does not arise.
    /// <para>Null off Windows, where the limit is something closer to two megabytes and applying 32 767
    /// would refuse arguments that would have gone through perfectly well. The tighter of the two
    /// Windows limits belongs to a <c>.cmd</c> shim, which is what npm installs and what
    /// <c>AiToolDetector</c> looks for first — so it is the common case, not the exotic one.</para>
    /// </summary>
    public static int? Budget(string executablePath) =>
        !OperatingSystem.IsWindows()
            ? null
            // Room for the executable path and the flags around the argument. Floored at zero: this
            // answers "how many characters may I have", and a pathologically long path made it negative.
            // Both callers happen to handle that — one refuses on `<= 0`, the other on `limit <= 0` —
            // but only because both were written by somebody who had the sign in mind, which is not
            // something a contract should rely on the next caller doing.
            : Math.Max(0, (ThroughShell(executablePath) ? 8_191 : 32_767) - executablePath.Length - 256);

    /// <summary>
    /// The budget to assume when the executable is not known yet.
    /// <para>The tighter of the two Windows limits, less room for a path of ordinary length. Used where
    /// a prompt has to be built before the tool has been resolved: the alternative is assuming no limit
    /// at all, and then handing the result to a <c>.cmd</c> shim that refuses it.</para>
    /// <para>A named constant rather than <c>Budget("something-not-found-yet.cmd")</c>, which is what
    /// this was — a fake path passed in to get the arithmetic to come out right.</para>
    /// </summary>
    public static int? Tightest() =>
        !OperatingSystem.IsWindows() ? null : 8_191 - 260 - 256;

    public static bool ThroughShell(string executablePath) =>
        executablePath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
        || executablePath.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
}
