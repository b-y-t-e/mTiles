using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using mTiles.Models;

namespace mTiles.Services;

/// <summary>What one piece of a tool's output is.</summary>
public enum AiChunkKind
{
    /// <summary>Words the tool wrote. Kept, and joined in order, as the fallback answer.</summary>
    Text,

    /// <summary>The tool's own final text — its answer, in preference to this side's reassembly.
    /// </summary>
    Result,

    /// <summary>What it is doing this second: a file, a command, a skill. Shown and thrown away.
    /// </summary>
    Activity,

    /// <summary>The tool saying it failed. Never the answer while there is one, and never silently
    /// dropped either.</summary>
    Error,

    /// <summary>A tool call the tool was refused permission for. Counted rather than shown: one of
    /// these is normal — an agent asking for something it does not need — and a run made entirely of
    /// them is the tile's own permission mode being wrong, which is what the count is for.</summary>
    Denied,
}

public sealed class AiOutputChunk
{
    public AiChunkKind Kind { get; init; } = AiChunkKind.Text;
    public string Content { get; init; } = "";
}

/// <summary>
/// What a run produced, and whether the tool said it failed.
/// </summary>
/// <remarks>
/// The flag is separate from the text because they answer different questions and the loop needs both.
/// It used to be text alone, so a run that ended in <c>error_max_turns</c> or a refused API key came
/// back as a non-empty string and was judged <c>Answered</c> — the failure adopted as the plan, or as
/// the review, and acted on. Throwing the text away instead would have been the other half of the same
/// mistake: a failed implementation has usually already written files, and what it managed to say about
/// them is the only account of what is now in the worktree.
/// </remarks>
public readonly record struct AiOutput(string Text, bool Failed, int PermissionDenials = 0)
{
    /// <summary>A run that said something and did not fail. Named rather than implicit: a conversion
    /// from string would set <c>Failed</c> to false silently, and that bit is the whole of what this
    /// type was introduced to stop being decided by accident.</summary>
    public static AiOutput Answered(string text) => new(text, Failed: false);

    /// <summary>A run the tool said had failed, keeping whatever it managed to say.</summary>
    public static AiOutput Failure(string text) => new(text, Failed: true);

    /// <summary>
    /// A bare string is an answer that did not fail.
    /// </summary>
    /// <remarks>
    /// Kept for the tests, which stand in for a tool a few dozen times over and mean "it answered this"
    /// every time. Nothing in the application converts a string any more — every producer here names
    /// <see cref="Answered"/> or <see cref="Failure"/>, so the bit this type exists to carry is chosen
    /// rather than defaulted wherever it actually matters.
    /// </remarks>
    public static implicit operator AiOutput(string text) => Answered(text);
}

public interface IAiToolRunner
{
    /// <summary>
    /// The flag this tool is given for the effort asked of it, and the one it is given for the
    /// permission mode — or null where nothing is passed for that setting.
    /// <para>Two methods rather than two properties, because the answer depends on the setting: see the
    /// last paragraph below.</para>
    /// </summary>
    /// <remarks>
    /// <para><b>Here rather than in <see cref="AiEfforts"/> or <see cref="AiPermissionModes"/>, because
    /// the spelling is the tool's and not this application's.</b> Those two hard-coded
    /// <c>--effort</c> and <c>--permission-mode</c> — Claude Code's words — so a <c>pi</c> older than
    /// <c>--thinking</c> answered <c>error: unknown option '--thinking'</c>, matched neither, and the
    /// user was told only that "the AI tool reported a failure" over a usage message about a flag they
    /// had never typed. That is the exact failure <see cref="RejectedFlag"/> exists to prevent, and it
    /// came back the moment a second tool was given a second spelling.</para>
    /// <para>Both are named together because recognising one needs the other: a usage message is only
    /// worth acting on when it mentions one of them alone, which is what tells "too old for this flag"
    /// from "the command line was wrong in some other way".</para>
    /// <para><b>Asked for the run that happened, not for the tool in general.</b> Every runner here
    /// adds its flags conditionally — Claude Code passes none for the mode the tool already defaults
    /// to, Antigravity passes its one flag only on bypass — so a matcher told the tool's flag
    /// unconditionally reads a usage message as "the flag was refused" over a flag that was never on
    /// the command line. For Antigravity that is not a corner: its effort flag is null, which leaves the
    /// weaker rule (a usage message naming the flag) with nothing to disambiguate against, and every
    /// failure of <c>agy</c> that prints its usage would advise a user on <em>Auto</em> to stop passing
    /// something they were not passing.</para>
    /// </remarks>
    string? EffortFlagFor(AiEffort effort) => null;

    /// <inheritdoc cref="EffortFlagFor"/>
    string? PermissionFlagFor(AiPermissionMode permission) => null;

    // No model parameter — each tool uses its own default model.
    // Tools support many providers so there's no way to build a universal model list.
    // If tools add a model listing command in the future, we can re-add model selection.
    /// <param name="permission">How much the tool may do without asking. Optional, and defaulted to
    /// <see cref="AiPermissionMode.Auto"/> rather than to "pass nothing", because "pass nothing" is
    /// the behaviour that made a headless run refuse every edit on a machine whose Claude Code is at
    /// its factory ask-first default. A tool with no such flag ignores it.</param>
    /// <param name="effort">How hard the tool is asked to think. Defaulted to
    /// <see cref="AiEffort.High"/> rather than to the tool's own, because a goal run is left alone and
    /// an attempt spent on a shallow answer costs as much of the budget as a careful one. A second
    /// parameter every runner but one ignores, spelled the same way <paramref name="permission"/> is:
    /// one flag for one CLI does not earn an options object, and two shapes for the same idea would.
    /// </param>
    void ConfigureProcess(ProcessStartInfo psi, string prompt, bool streaming,
        AiPermissionMode permission = AiPermissionMode.Auto,
        AiEffort effort = AiEffort.High);

    /// <summary>
    /// Whether this tool can report what it is doing as it does it.
    /// </summary>
    /// <remarks>
    /// Opt-in per tool, like <see cref="AcceptsPromptOnStdin"/> and for the same reason: it is a claim
    /// about somebody else's CLI. A tool that does not stream is run exactly as it was before and its
    /// output read at the end.
    /// </remarks>
    bool SupportsStreaming => false;

    /// <summary>
    /// Whether this tool reads its prompt from standard input when the prompt is left off the command
    /// line.
    /// <para>Opt-in, and false by default, because it is a claim about somebody else's CLI. Windows
    /// caps a command line at 32 767 characters — 8 191 through the <c>.cmd</c> shim npm installs — and
    /// a prompt carrying a diff passes that easily, at which point <c>Process.Start</c> throws and the
    /// tile can only offer to try again and fail identically. Stdin removes the limit, but a tool that
    /// does <em>not</em> read stdin would sit waiting for input that never comes, so this is turned on
    /// per tool, by somebody who has checked, rather than assumed for all four.</para>
    /// </summary>
    bool AcceptsPromptOnStdin => false;

    /// <summary>
    /// Everything one line of the tool's output says, in the order it says it.
    /// </summary>
    /// <remarks>
    /// A list rather than one chunk, because a single assistant message carries both prose and tool
    /// calls — "let me look at the cart" and then the Read. Returning one meant choosing, and choosing
    /// the tool call threw the sentence away: invisible while the run ends with a result line, and the
    /// whole of what is left when it does not — which is exactly the interrupted run, where what it
    /// managed to say is all there is to show for it.
    /// </remarks>
    IReadOnlyList<AiOutputChunk> ParseLine(string line);
}

public sealed class ClaudeToolRunner : IAiToolRunner
{
    public string? EffortFlagFor(AiEffort effort) => AiEfforts.Flag(effort) is null ? null : "--effort";

    public string? PermissionFlagFor(AiPermissionMode permission) =>
        AiPermissionModes.Flag(permission) is null ? null : "--permission-mode";

    /// <summary><c>claude -p</c> with no prompt after it reads the prompt from standard input.</summary>
    public bool AcceptsPromptOnStdin => true;

    public bool SupportsStreaming => true;

    /// <summary>
    /// The command line, and one flag that is deliberately absent.
    /// </summary>
    /// <remarks>
    /// <para><b>No <c>--max-turns</c>, at any number.</b> It was 20, then 200, and both were my numbers
    /// rather than anything about the work. An agent that reads a few files, loads a skill and then
    /// edits spends turns quickly, so a ceiling is reachable in ordinary work: the 200 was hit half way
    /// through a real implementation, which is the failure the 20 was raised for. Reporting the stop
    /// honestly — <c>error_max_turns</c> arrives as a result line marked as an error, and
    /// <see cref="ParseLine"/> reports it as one — makes a truncated run visible, but a visible
    /// truncation is still a truncation, and the tile is meant to be left alone for hours. Turns are
    /// the wrong unit for this: what they count is the tool's inner loop, and what the user cares about
    /// is the work.</para>
    /// <para><b>Which leaves one run with no ceiling of any kind, and that is the accepted risk rather
    /// than something covered elsewhere.</b> The attempt budget bounds how many runs a goal gets, not
    /// how long one lasts, and <see cref="RunPlainAsync"/> deliberately has no wall-clock timeout — so
    /// Pause is the whole of the stop. Not "the user can set one in their settings": measured against
    /// Claude Code 2.1.251, <c>maxTurns</c> is a hidden CLI flag, a field in an agent file's front
    /// matter and an SDK option, and <c>settings.json</c> has no equivalent at all. The only ceiling
    /// available for this run is the flag on this line, which is exactly the one being refused.</para>
    /// <para><c>--verbose</c> is not optional with <c>stream-json</c>: print mode refuses the pair
    /// without it.</para>
    /// </remarks>
    public void ConfigureProcess(ProcessStartInfo psi, string prompt, bool streaming,
        AiPermissionMode permission = AiPermissionMode.Auto,
        AiEffort effort = AiEffort.High)
    {
        // No prompt argument: `claude -p` with nothing after it reads it from standard input, which
        // AiProcessRunner writes. `prompt` is unused here for exactly that reason.
        psi.ArgumentList.Add("-p");

        // Said out loud rather than inherited. Without it the run takes whatever mode the user's own
        // Claude Code settings are in, and the factory default is to ask — which a `-p` run cannot do,
        // so every edit is refused and the implementation writes nothing. ToolDefault is the way back
        // to that inheritance for somebody who wants it, and it is the only mode that adds no flag.
        if (AiPermissionModes.Flag(permission) is { } mode)
        {
            psi.ArgumentList.Add("--permission-mode");
            psi.ArgumentList.Add(mode);
        }

        // Measured: an unrecognised *value* is forgiving here — the tool warns and uses its own
        // default — but an unrecognised *flag* is not, and a Claude Code from before --effort existed
        // runs nothing at all. AiEfforts.LooksLikeRejectedEffort is what turns that into a sentence
        // naming the way out rather than "the AI tool reported a failure".
        if (AiEfforts.Flag(effort) is { } level)
        {
            psi.ArgumentList.Add("--effort");
            psi.ArgumentList.Add(level);
        }

        psi.ArgumentList.Add("--output-format");
        psi.ArgumentList.Add(streaming ? "stream-json" : "text");

        if (!streaming) return;

        psi.ArgumentList.Add("--verbose");
    }

    public IReadOnlyList<AiOutputChunk> ParseLine(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            if (!root.TryGetProperty("type", out var typeProp))
                return [];

            var type = typeProp.GetString() ?? "";

            if (type == "assistant" && root.TryGetProperty("message", out var msg)
                && msg.TryGetProperty("content", out var contentArr)
                && contentArr.ValueKind == JsonValueKind.Array)
            {
                // Both halves, in the order the message wrote them. This used to return the tool call
                // and drop the prose, on the grounds that the answer comes from the result line anyway
                // — true until the run is interrupted, which is the one case where what it managed to
                // say is all there is.
                var said = new List<AiOutputChunk>();

                foreach (var block in contentArr.EnumerateArray())
                {
                    if (block.TryGetProperty("text", out var text)
                        && text.GetString() is { Length: > 0 } prose)
                        said.Add(new AiOutputChunk { Kind = AiChunkKind.Text, Content = prose });

                    if (Activity(block) is { Length: > 0 } doing)
                        said.Add(new AiOutputChunk { Kind = AiChunkKind.Activity, Content = doing });
                }

                if (said.Count > 0) return said;
            }

            // A refused tool call comes back as a user turn carrying the tool_result, not as an error
            // line, so nothing above this ever saw one: the run looked like an agent that read some
            // files and decided to change nothing. Counted here so the tile can tell "it declined the
            // work" from "it was not allowed to do the work" — the two produce an identical worktree.
            if (type == "user" && root.TryGetProperty("message", out var userMsg)
                && userMsg.TryGetProperty("content", out var userContent)
                && userContent.ValueKind == JsonValueKind.Array)
            {
                var refused = userContent.EnumerateArray().Count(IsPermissionDenial);
                if (refused > 0)
                    return Enumerable.Repeat(
                        new AiOutputChunk { Kind = AiChunkKind.Denied, Content = "" }, refused).ToList();
            }

            if (type == "content_block_delta" && root.TryGetProperty("delta", out var delta)
                && delta.TryGetProperty("text", out var deltaText))
            {
                return [new AiOutputChunk { Kind = AiChunkKind.Text, Content = deltaText.GetString() ?? "" }];
            }

            if (type == "result")
            {
                // Asked before the text is taken, because the text is there either way. A result line
                // carrying is_error is the tool saying this is what went wrong, not what it produced —
                // and read as an answer it becomes a plan, an implementation, or a review that nobody
                // wrote. The error path keeps it out of the answer unless there is nothing else.
                var failed = root.TryGetProperty("is_error", out var isError)
                             && isError.ValueKind == JsonValueKind.True;

                if (root.TryGetProperty("subtype", out var subtype)
                    && (subtype.GetString() ?? "").StartsWith("error", StringComparison.OrdinalIgnoreCase))
                    failed = true;

                var text = root.TryGetProperty("result", out var result) ? result.GetString() ?? "" : "";

                if (failed)
                    return [new AiOutputChunk
                    {
                        Kind = AiChunkKind.Error,
                        Content = text.Length > 0 ? text : "Claude returned an error.",
                    }];

                if (text.Length > 0)
                    return [new AiOutputChunk { Kind = AiChunkKind.Result, Content = text }];
            }

            return [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Whether one block of a user turn is the harness saying a tool call was not allowed.
    /// </summary>
    /// <remarks>
    /// <para>Matched on the words, because there is no field that says so: a denial arrives as an
    /// ordinary <c>tool_result</c> with <c>is_error</c> set, which is also what a failed command or a
    /// missing file looks like. The error flag is therefore the gate and the wording is the test, and
    /// both halves are needed — the wording alone would count an agent quoting the sentence.</para>
    /// <para>Two spellings, because the harness has used both. Getting this wrong is cheap in one
    /// direction and not the other: a missed denial only leaves the old, unhelpful message in place,
    /// while a false one would tell a user their permission mode is wrong when it is not. Hence the
    /// narrow phrases rather than the word "permission" on its own.</para>
    /// <para>There was a third test here — "permission" and "denied" anywhere in the same result —
    /// as a catch-all, and it was the exact failure the paragraph above forbids, written one line
    /// below it. Those two words appear together in <c>Permission denied (publickey)</c> from a git
    /// push, in <c>bash: ./x: Permission denied</c>, and in <c>EACCES: permission denied, open</c> from
    /// node — every one of them an ordinary <c>tool_result</c> with <c>is_error</c> set, and every one
    /// of them a real failure of the work rather than a refusal by the harness. It turned "your ssh key
    /// is not loaded" into "mTiles was not allowed to touch a file; change the permission mode", which
    /// sends the user to a setting that will not help. A new spelling by the harness costs a missed
    /// denial and the old message; a guess costs the user the diagnosis.</para>
    /// </remarks>
    private static bool IsPermissionDenial(JsonElement block)
    {
        if (!block.TryGetProperty("type", out var kind) || kind.GetString() != "tool_result") return false;
        if (!block.TryGetProperty("is_error", out var isError) || isError.ValueKind != JsonValueKind.True)
            return false;

        var text = block.TryGetProperty("content", out var content) ? Flatten(content) : "";

        return text.Contains("requested permissions", StringComparison.OrdinalIgnoreCase)
               || text.Contains("permission to use", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A tool_result's content, which is a string in the simple case and a list of blocks in
    /// the other. Both shapes are the harness's, not a choice this side gets to make.</summary>
    private static string Flatten(JsonElement content) => content.ValueKind switch
    {
        JsonValueKind.String => content.GetString() ?? "",
        JsonValueKind.Array => string.Join(" ", content.EnumerateArray()
            .Select(b => b.ValueKind == JsonValueKind.Object && b.TryGetProperty("text", out var t)
                ? t.GetString() ?? ""
                : "")),
        _ => "",
    };

    /// <summary>
    /// One tool call, in the fewest words that still say what is happening.
    /// </summary>
    /// <remarks>
    /// The tool's name alone is nearly useless — "Edit" tells you it is editing something — and the
    /// whole input is a JSON object nobody wants in a status strip. What is worth a line is the name
    /// and the one field that says which thing: the file, the command, the skill.
    /// </remarks>
    private static string Activity(JsonElement block)
    {
        if (!block.TryGetProperty("type", out var kind) || kind.GetString() != "tool_use") return "";
        if (!block.TryGetProperty("name", out var nameProp)) return "";

        var name = nameProp.GetString() ?? "";
        if (name.Length == 0) return "";

        if (!block.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Object)
            return name;

        foreach (var field in Subjects)
            if (input.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String
                && value.GetString() is { Length: > 0 } subject)
                return $"{name} {Shorten(subject)}";

        return name;
    }

    /// <summary>The field that says what a tool call is about, in the order worth trying. A path comes
    /// before a pattern because "Grep src/Goal" reads better than "Grep TODO", and a command near the
    /// end because it is the one that is usually long; description is last, as the fallback for a tool
    /// that names nothing else.</summary>
    private static readonly string[] Subjects =
        ["file_path", "path", "notebook_path", "skill", "pattern", "url", "command", "description"];

    /// <summary>One line's worth, with the useful end kept: a path is told apart by its last segment,
    /// not its first, so this trims from the left and marks it.</summary>
    private static string Shorten(string subject)
    {
        var flat = subject.ReplaceLineEndings(" ").Trim();
        if (flat.Length <= 48) return flat;

        // By rune, not by char. A path with an emoji or anything else outside the basic plane is two
        // chars for one character, and cutting at 47 of them splits the pair and leaves a lone
        // surrogate on the status strip — the distinction CommandDisplay.Visible already spells out a
        // few files away.
        var runes = flat.EnumerateRunes().ToList();
        return "\u2026" + string.Concat(runes.Skip(Math.Max(0, runes.Count - 47)));
    }
}

public sealed class CodexToolRunner : IAiToolRunner
{
    public void ConfigureProcess(ProcessStartInfo psi, string prompt, bool streaming,
        AiPermissionMode permission = AiPermissionMode.Auto,
        AiEffort effort = AiEffort.High)
    {
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add(prompt);
    }

    public IReadOnlyList<AiOutputChunk> ParseLine(string line) =>
        string.IsNullOrWhiteSpace(line) ? [] : [new AiOutputChunk { Content = line }];
}

public sealed class OpenCodeToolRunner : IAiToolRunner
{
    public void ConfigureProcess(ProcessStartInfo psi, string prompt, bool streaming,
        AiPermissionMode permission = AiPermissionMode.Auto,
        AiEffort effort = AiEffort.High)
    {
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add(prompt);
    }

    public IReadOnlyList<AiOutputChunk> ParseLine(string line) =>
        string.IsNullOrWhiteSpace(line) ? [] : [new AiOutputChunk { Content = line }];
}

public sealed class PiToolRunner : IAiToolRunner
{
    public string? EffortFlagFor(AiEffort effort) =>
        AiEfforts.Flag(effort) is null ? null : "--thinking";

    public void ConfigureProcess(ProcessStartInfo psi, string prompt, bool streaming,
        AiPermissionMode permission = AiPermissionMode.Auto,
        AiEffort effort = AiEffort.High)
    {
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add(prompt);
        psi.ArgumentList.Add("--mode");
        psi.ArgumentList.Add("text");

        // Measured against `pi --help`: `--thinking off|minimal|low|medium|high|xhigh|max`. Every level
        // AiEffort names exists there under the same word, so the map is the identity and the tile's
        // setting means here what it means for Claude Code. `off` and `minimal` are pi's alone and are
        // deliberately not offered: a Goal run is left alone, and the tile has no level below `low`.
        //
        // pi has no permission flag of its own — `--approve` is about trusting project-local files, not
        // about tool calls — so `permission` goes unused rather than being mapped to something adjacent.
        if (AiEfforts.Flag(effort) is { } level)
        {
            psi.ArgumentList.Add("--thinking");
            psi.ArgumentList.Add(level);
        }
    }

    public IReadOnlyList<AiOutputChunk> ParseLine(string line) =>
        string.IsNullOrWhiteSpace(line) ? [] : [new AiOutputChunk { Content = line }];
}

/// <summary>
/// Google's Antigravity CLI.
/// </summary>
/// <remarks>
/// <para>Measured, and it needed a runner of its own rather than the generic fallback: a bare
/// positional argument does not start a print run, it opens the interactive session and <b>hangs</b> —
/// which, on a path with no wall-clock timeout, is a Goal tile that never comes back.
/// <c>agy --print &lt;prompt&gt;</c> answers on stdout and exits 0.</para>
/// <para><c>--dangerously-skip-permissions</c> is the only permission control it has, so only
/// <see cref="AiPermissionMode.BypassPermissions"/> maps to anything. The three finer modes pass no
/// flag rather than being rounded up to it — asking for "auto" and getting "nothing is asked about at
/// all" is the one direction this must never round.</para>
/// <para><b>No effort flag.</b> Antigravity spends its effort through the model name — the catalogue
/// is <c>gemini-3.7-flash-high</c>, <c>-medium</c>, <c>-low</c> — so a level would have to rewrite
/// whatever model the user has configured, which is a larger claim than this setting makes anywhere
/// else. It is left alone.</para>
/// </remarks>
public sealed class AntigravityToolRunner : IAiToolRunner
{
    public string? PermissionFlagFor(AiPermissionMode permission) =>
        permission == AiPermissionMode.BypassPermissions ? "--dangerously-skip-permissions" : null;

    public void ConfigureProcess(ProcessStartInfo psi, string prompt, bool streaming,
        AiPermissionMode permission = AiPermissionMode.Auto,
        AiEffort effort = AiEffort.High)
    {
        if (permission == AiPermissionMode.BypassPermissions)
            psi.ArgumentList.Add("--dangerously-skip-permissions");

        psi.ArgumentList.Add("--print");
        psi.ArgumentList.Add(prompt);
    }

    public IReadOnlyList<AiOutputChunk> ParseLine(string line) =>
        string.IsNullOrWhiteSpace(line) ? [] : [new AiOutputChunk { Content = line }];
}

/// <summary>
/// What an unrecognised tool gets: the prompt as a plain first argument, and no claim about stdin.
/// <para>The fallback used to be <see cref="ClaudeToolRunner"/>, which was survivable while that ran
/// everything on the command line and became a hang when it moved to standard input — a custom tool
/// was launched with Claude's flags, no prompt anywhere on its command line, and a pipe it had never
/// agreed to read. Passing the prompt as an argument is the one thing every CLI here does.</para>
/// </summary>
public sealed class GenericToolRunner : IAiToolRunner
{
    public void ConfigureProcess(ProcessStartInfo psi, string prompt, bool streaming,
        AiPermissionMode permission = AiPermissionMode.Auto,
        AiEffort effort = AiEffort.High) =>
        psi.ArgumentList.Add(prompt);

    public IReadOnlyList<AiOutputChunk> ParseLine(string line) =>
        string.IsNullOrWhiteSpace(line) ? [] : [new AiOutputChunk { Content = line }];
}

public static class AiProcessRunner
{
    private static readonly ConcurrentDictionary<string, IAiToolRunner> Runners = new(StringComparer.OrdinalIgnoreCase)
    {
        ["claude"] = new ClaudeToolRunner(),
        ["codex"] = new CodexToolRunner(),
        ["opencode"] = new OpenCodeToolRunner(),
        ["pi"] = new PiToolRunner(),
        ["agy"] = new AntigravityToolRunner()
    };

    public static IAiToolRunner GetRunner(string toolBinary) =>
        Runners.GetValueOrDefault(toolBinary) ?? new GenericToolRunner();

    private static readonly TimeSpan ProcessExitTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Refuses a prompt the operating system could not carry, with a message saying so.
    /// <para>The prompt is passed as a command-line argument by every runner here, and Windows caps a
    /// command line at 32 767 characters — 8 191 through a <c>.cmd</c> shim, which is what npm installs.
    /// Over the limit <c>Process.Start</c> throws a <see cref="System.ComponentModel.Win32Exception"/>
    /// whose text says nothing about length, so the tile reported that the tool had failed and offered
    /// to try again, which could only fail identically. This says what actually happened.</para>
    /// </summary>
    private static void GuardPromptLength(string executablePath, string prompt)
    {
        if (PromptBudget(executablePath) is not { } budget) return;

        if (budget <= 0)
            throw new InvalidOperationException(
                $"The path to this tool is {executablePath.Length} characters, which leaves no room on " +
                "a command line for a prompt. Move the tool somewhere shorter.");

        var quoted = CommandLineLength.Quoted(prompt);
        if (quoted <= budget) return;

        var throughShell = CommandLineLength.ThroughShell(executablePath);
        throw new InvalidOperationException(
            $"The prompt is {quoted} characters once quoted and {Path.GetFileName(executablePath)} can be " +
            $"given at most {budget} on a command line" +
            (throughShell ? " (it is a .cmd shim, which is the tighter of the two Windows limits)" : "") +
            ". The working tree and the plan are already capped, so this is a goal or a set of answers " +
            "that will not fit — shorten them, or use a tool that accepts its prompt on standard input.");
    }

    /// <summary>
    /// How many characters of prompt this tool can be handed on a command line, or <c>null</c> when the
    /// question does not arise — a tool that reads standard input has no command line to overflow, and
    /// off Windows the limit is something closer to two megabytes.
    /// <para>Public because the prompt builder needs it <em>before</em> it builds. Refusing an oversized
    /// prompt is the last line of defence and a poor one: the run is judged failed, the tile pauses, and
    /// Resume reproduces the same failure for ever. Knowing the budget in advance lets the borrowed
    /// blocks be trimmed to fit instead, which costs the tool some context and costs the user nothing.
    /// </para>
    /// <para>The arithmetic itself is <see cref="CommandLineLength"/>'s; what this adds is the one thing
    /// only a runner knows — whether the prompt is going on a command line at all.</para>
    /// </summary>
    public static int? PromptBudget(string executablePath, IAiToolRunner? runner = null) =>
        runner?.AcceptsPromptOnStdin == true ? null : CommandLineLength.Budget(executablePath);

    /// <summary>
    /// Runs the tool once and returns everything it printed.
    /// </summary>
    /// <remarks>
    /// <b>Deliberately without an overall timeout</b>, and that is the decision rather than an
    /// oversight. An agent that has been running for forty
    /// minutes is doing the thing it was asked to do, and it writes as it goes — so a timeout would
    /// kill it mid-edit and leave the worktree half-changed, which is worse than waiting. Any number
    /// picked here would be a guess about how long somebody's task takes, applied to work that is
    /// already in the user's files.
    /// <para>What ends a run instead is <paramref name="ct"/>: Pause cancels it and the process tree is
    /// killed. That is a decision made by somebody who can see the tile, which is the right kind of
    /// decision for this.</para>
    /// </remarks>
    /// <param name="onActivity">
    /// Called with a few words about what the tool is doing, as it does it, when the tool can say —
    /// see <see cref="IAiToolRunner.SupportsStreaming"/>. <b>Called from the thread draining the child's
    /// output</b>, so anything touching the UI marshals for itself.
    /// <para>Passing one is what turns streaming on. Without it the tool is run exactly as it was and
    /// its output read at the end, which is what the tools that cannot stream always do.</para>
    /// </param>
    public static async Task<AiOutput> RunPlainAsync(
        string executablePath,
        string prompt,
        string workingDirectory,
        IAiToolRunner runner,
        AiPermissionMode permission = AiPermissionMode.Auto,
        AiEffort effort = AiEffort.High,
        Action<string>? onActivity = null,
        CancellationToken ct = default)
    {
        if (!runner.AcceptsPromptOnStdin)
            GuardPromptLength(executablePath, prompt);

        var streaming = onActivity != null && runner.SupportsStreaming;

        var psi = CreateProcessStartInfo(executablePath, workingDirectory);

        // **Always redirected, whether or not the prompt goes down it.** Left inherited, a tool that
        // decides to be interactive waits on this application's own standard input, which in a windowed
        // process nobody is ever going to type into — so the run does not fail, it stops, on a path
        // that deliberately has no wall-clock timeout, and the tile waits for ever.
        //
        // This is not hypothetical and not about one tool: a bare positional prompt is what
        // `GenericToolRunner` passes, and a bare positional prompt is measured to open an interactive
        // session rather than a print run on at least one of the CLIs here. Every tool without a runner
        // of its own — every custom AI tool a user adds, and any tool whose entry is removed — takes
        // that path. Closing the pipe below turns "waits for input that will never come" into
        // end-of-input, which is a tool that exits and says something.
        psi.RedirectStandardInput = true;
        // Both, and this is the whole of what makes either setting real. `effort` was accepted here
        // and dropped on this line, so ConfigureProcess took its own default of High: every run went
        // out with `--effort high` whatever the strip said, the combo box was decoration, and — worse —
        // a Claude Code from before that flag existed rejected it on every goal with no way for the
        // user to turn it off, because choosing "tool default" changed nothing that got this far.
        runner.ConfigureProcess(psi, prompt, streaming, permission, effort);

        using var process = new Process { StartInfo = psi };
        process.Start();

        // The readers start first, then the prompt goes down stdin. The other order deadlocks on a
        // prompt large enough to fill the pipe: this side blocks writing the rest of it while the child
        // blocks writing output nobody is draining, which is the size of prompt stdin exists for.
        var stdoutTask = streaming
            ? ReadStreamAsync(process.StandardOutput, runner, onActivity!)
            : ReadToEndAsync(process.StandardOutput);
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

        // Registered before the write, not after. A prompt big enough to block — the child not draining
        // it — would otherwise sit here with nothing left to interrupt it, so pausing during the write
        // hung the tile until the tool gave up on its own.
        using var reg = ct.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); } catch { }
        });

        if (runner.AcceptsPromptOnStdin)
        {
            await WritePromptAsync(process, prompt);
        }
        else
        {
            // Nothing to send, so the pipe is closed at once — the same thing `WritePromptAsync` does
            // after writing, and for the same reason: an open pipe with nobody writing to it is
            // indistinguishable, from the child's side, from a user who has not typed yet.
            try { process.StandardInput.Close(); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // The child has already gone. Nothing to close it for.
            }
        }

        var output = await stdoutTask;
        var stderr = await stderrTask;

        await WaitForExitWithTimeoutAsync(process);

        ct.ThrowIfCancellationRequested();

        // A non-zero exit with something on stderr is the plain path's version of the stream's error
        // chunk, and is now reported the same way: the words kept, the fact carried beside them.
        if (process.HasExited && process.ExitCode != 0 && !string.IsNullOrWhiteSpace(stderr))
            return AiOutput.Failure(
                $"{output.Text.Trim()}\n\n[stderr] {stderr.Trim()}".Trim())
                with { PermissionDenials = output.PermissionDenials };

        return new AiOutput(output.Text.Trim(), output.Failed, output.PermissionDenials);
    }

    /// <summary>The whole of standard output, for a tool that cannot say anything about itself as it
    /// goes. Nothing here can fail on its own account — the exit code is the only signal, and the
    /// caller reads it.</summary>
    private static async Task<AiOutput> ReadToEndAsync(TextReader output) =>
        new(await output.ReadToEndAsync(CancellationToken.None), Failed: false);

    /// <summary>
    /// Drains a streaming tool, reporting what it does and keeping what it answers.
    /// </summary>
    /// <remarks>
    /// <para>The answer is the <c>result</c> line when there is one, because that is the tool's own
    /// final text rather than this side's reassembly of the pieces. Falling back to the pieces matters
    /// all the same: a run killed part way through has no result line, and the text it did produce is
    /// better than nothing to show for it.</para>
    /// <para>A line that parses to nothing is dropped, which is most of them — init, usage, tool
    /// results. Reading them is how the tile knows the difference between a tool that finished and one
    /// that stopped, which is the whole reason for streaming: with plain text output those two are the
    /// same string.</para>
    /// </remarks>
    /// <param name="output">
    /// The child's standard output. A <see cref="TextReader"/> rather than the process, so the rules
    /// below can be read off a string in a test instead of needing a tool installed to state them.
    /// </param>
    internal static async Task<AiOutput> ReadStreamAsync(
        TextReader output, IAiToolRunner runner, Action<string> onActivity)
    {
        var text = new StringBuilder();
        string? result = null;
        string? error = null;
        var denied = 0;

        while (await output.ReadLineAsync() is { } line)
        {
            foreach (var chunk in runner.ParseLine(line))
            {
                switch (chunk.Kind)
                {
                    case AiChunkKind.Activity:
                        // Not awaited and not marshalled: this is the caller's business, and holding the
                        // reader while a dispatcher gets round to it stalls the pipe the child is writing
                        // into.
                        try { onActivity(chunk.Content); } catch { /* a status line is not worth a run */ }
                        break;

                    case AiChunkKind.Result:
                        // Only when it says something. An empty result line is what a run that was killed
                        // or that failed leaves behind, and taking it anyway meant an empty string beat the
                        // text the tool had already produced — the answer thrown away in favour of the
                        // absence of one.
                        if (chunk.Content.Length > 0) result = chunk.Content;
                        break;

                    case AiChunkKind.Error:
                        // Kept apart, not appended. The tool's account of its own failure is not a paragraph
                        // of its answer, and glued onto a half-finished one it becomes a sentence the review
                        // prompt reads as something the implementation decided.
                        error = chunk.Content;
                        break;

                    case AiChunkKind.Denied:
                        denied++;
                        break;

                    case AiChunkKind.Text:
                        // Appended without a newline. A whole assistant message ends where it ends, and a
                        // content_block_delta is a fragment — often half a word — so a line break between
                        // them puts one inside the word. Claude emits no deltas without
                        // --include-partial-messages, so this is unreached today and is written for the day
                        // it is not.
                        text.Append(chunk.Content);
                        break;

                    default:
                        break;
                }
            }
        }

        // The tool's own final text first, then whatever it said on the way. The error is never thrown
        // away: dropping it whenever anything else had been printed is how a run that stopped half way
        // through came back looking like one that finished, and the thing that went wrong — a credit
        // balance, a revoked key — was never said out loud anywhere.
        //
        // Labelled rather than glued on, and after the answer rather than into it, which is the same
        // shape RunPlainAsync already uses for a non-zero exit with something on stderr. The verdict
        // stays content-based on purpose: a failed implementation has usually already written files, and
        // what it managed to say about them is worth more to the next attempt than a clean "it failed".
        var answer = result is { Length: > 0 } ? result
            : text.Length > 0 ? text.ToString().TrimEnd()
            : "";

        if (error is not { Length: > 0 }) return AiOutput.Answered(answer) with { PermissionDenials = denied };

        // Both halves. The text is kept because a failed implementation has usually already written
        // files and this is the only account of what is in the worktree; the flag is kept because
        // without it the loop reads that account as an answer and adopts it as the plan, or as the
        // review, and carries on.
        return AiOutput.Failure(
            answer.Length > 0 ? $"{answer}\n\n[error] {error}" : error)
            with { PermissionDenials = denied };
    }

    /// <summary>
    /// Writes the prompt to the child's standard input and closes the pipe.
    /// <para>Closing is the part that matters: a tool reading its prompt from standard input waits for
    /// end-of-input before it starts, so a handle left open hangs the run until the timeout.</para>
    /// </summary>
    private static async Task WritePromptAsync(Process process, string prompt)
    {
        try
        {
            await process.StandardInput.WriteAsync(prompt);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The child stopped reading — it exited early, or it was killed. Not worth throwing over:
            // letting this out skipped the awaits on stdout and stderr, so the tool's own account of
            // what went wrong was thrown away in favour of "the pipe is broken".
            Trace.TraceWarning($"Writing the prompt to standard input ended early: {ex.Message}");
        }
        finally
        {
            try { process.StandardInput.Close(); } catch { /* already gone */ }
        }
    }

    private static async Task WaitForExitWithTimeoutAsync(Process process)
    {
        using var exitCts = new CancellationTokenSource(ProcessExitTimeout);
        try
        {
            await process.WaitForExitAsync(exitCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
        }
    }

    private static ProcessStartInfo CreateProcessStartInfo(string executablePath, string workingDirectory) => new()
    {
        FileName = executablePath,
        WorkingDirectory = workingDirectory,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true,
        StandardOutputEncoding = System.Text.Encoding.UTF8,
        StandardErrorEncoding = System.Text.Encoding.UTF8
    };
}
