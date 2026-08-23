using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace mTiles.Services;

public sealed class AiOutputChunk
{
    public string Type { get; init; } = "text";
    public string Content { get; init; } = "";
}

public interface IAiToolRunner
{
    // No model parameter — each tool uses its own default model.
    // Tools support many providers so there's no way to build a universal model list.
    // If tools add a model listing command in the future, we can re-add model selection.
    void ConfigureProcess(ProcessStartInfo psi, string prompt, int maxTurns, bool streaming);

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
    AiOutputChunk? ParseLine(string line);
}

public sealed class ClaudeToolRunner : IAiToolRunner
{
    /// <summary><c>claude -p</c> with no prompt after it reads the prompt from standard input.</summary>
    public bool AcceptsPromptOnStdin => true;

    public void ConfigureProcess(ProcessStartInfo psi, string prompt, int maxTurns, bool streaming)
    {
        // No prompt argument: `claude -p` with nothing after it reads it from standard input, which
        // AiProcessRunner writes. `prompt` is unused here for exactly that reason.
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add("--output-format");
        psi.ArgumentList.Add(streaming ? "stream-json" : "text");
        psi.ArgumentList.Add("--max-turns");
        psi.ArgumentList.Add(maxTurns.ToString());
    }

    public AiOutputChunk? ParseLine(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            if (!root.TryGetProperty("type", out var typeProp))
                return null;

            var type = typeProp.GetString() ?? "";

            if (type == "assistant" && root.TryGetProperty("message", out var msg)
                && msg.TryGetProperty("content", out var contentArr)
                && contentArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in contentArr.EnumerateArray())
                {
                    if (block.TryGetProperty("text", out var text))
                        return new AiOutputChunk { Type = "text", Content = text.GetString() ?? "" };
                }
            }

            if (type == "content_block_delta" && root.TryGetProperty("delta", out var delta)
                && delta.TryGetProperty("text", out var deltaText))
            {
                return new AiOutputChunk { Type = "text", Content = deltaText.GetString() ?? "" };
            }

            if (type == "result")
            {
                if (root.TryGetProperty("result", out var result))
                    return new AiOutputChunk { Type = "result", Content = result.GetString() ?? "" };
                if (root.TryGetProperty("subtype", out var subtype) && subtype.GetString() == "error_response")
                    return new AiOutputChunk { Type = "error", Content = "Claude returned an error." };
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public sealed class CodexToolRunner : IAiToolRunner
{
    public void ConfigureProcess(ProcessStartInfo psi, string prompt, int maxTurns, bool streaming)
    {
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add(prompt);
    }

    public AiOutputChunk? ParseLine(string line) =>
        string.IsNullOrWhiteSpace(line) ? null : new AiOutputChunk { Content = line };
}

public sealed class OpenCodeToolRunner : IAiToolRunner
{
    public void ConfigureProcess(ProcessStartInfo psi, string prompt, int maxTurns, bool streaming)
    {
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add(prompt);
    }

    public AiOutputChunk? ParseLine(string line) =>
        string.IsNullOrWhiteSpace(line) ? null : new AiOutputChunk { Content = line };
}

public sealed class PiToolRunner : IAiToolRunner
{
    public void ConfigureProcess(ProcessStartInfo psi, string prompt, int maxTurns, bool streaming)
    {
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add(prompt);
        psi.ArgumentList.Add("--mode");
        psi.ArgumentList.Add("text");
    }

    public AiOutputChunk? ParseLine(string line) =>
        string.IsNullOrWhiteSpace(line) ? null : new AiOutputChunk { Content = line };
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
    public void ConfigureProcess(ProcessStartInfo psi, string prompt, int maxTurns, bool streaming) =>
        psi.ArgumentList.Add(prompt);

    public AiOutputChunk? ParseLine(string line) =>
        string.IsNullOrWhiteSpace(line) ? null : new AiOutputChunk { Content = line };
}

public static class AiProcessRunner
{
    private static readonly ConcurrentDictionary<string, IAiToolRunner> Runners = new(StringComparer.OrdinalIgnoreCase)
    {
        ["claude"] = new ClaudeToolRunner(),
        ["openclaude"] = new ClaudeToolRunner(),
        ["codex"] = new CodexToolRunner(),
        ["opencode"] = new OpenCodeToolRunner(),
        ["pi"] = new PiToolRunner()
    };

    public static void RegisterRunner(string toolBinary, IAiToolRunner runner) =>
        Runners[toolBinary] = runner;

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
        // Windows only. The limits below are `CreateProcess`'s and `cmd.exe`'s; a POSIX system allows
        // something closer to two megabytes, and applying 32 767 there would refuse prompts that would
        // have gone through perfectly well.
        if (!OperatingSystem.IsWindows()) return;

        var throughShell = executablePath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                           || executablePath.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
        var limit = throughShell ? 8_191 : 32_767;

        // Room for the executable path and the flags around the prompt. A path long enough to leave
        // nothing over is its own problem, and saying "at most -40 characters" would not name it.
        var budget = limit - executablePath.Length - 256;
        if (budget <= 0)
            throw new InvalidOperationException(
                $"The path to this tool is {executablePath.Length} characters, which leaves no room on " +
                "a command line for a prompt. Move the tool somewhere shorter.");

        // Measured as it will be *quoted*, not as it is: the argument is wrapped and every quote and
        // backslash inside it is escaped, so a prompt of code — which is what this carries — grows on
        // the way onto the command line. Measuring the raw string let one through that then threw.
        var quoted = prompt.Length + prompt.Count(c => c is '"' or '\\') + 2;
        if (quoted <= budget) return;

        throw new InvalidOperationException(
            $"The prompt is {quoted} characters once quoted and {Path.GetFileName(executablePath)} can be " +
            $"given at most {budget} on a command line" +
            (throughShell ? " (it is a .cmd shim, which is the tighter of the two Windows limits)" : "") +
            ". The working tree and the plan are already capped, so this is a goal or a set of answers " +
            "that will not fit — shorten them, or use a tool that accepts its prompt on standard input.");
    }

    public static async Task<string> RunPlainAsync(
        string executablePath,
        string prompt,
        string workingDirectory,
        IAiToolRunner runner,
        int maxTurns = 20,
        CancellationToken ct = default)
    {
        if (!runner.AcceptsPromptOnStdin)
            GuardPromptLength(executablePath, prompt);

        var psi = CreateProcessStartInfo(executablePath, workingDirectory);
        psi.RedirectStandardInput = runner.AcceptsPromptOnStdin;
        runner.ConfigureProcess(psi, prompt, maxTurns, streaming: false);

        using var process = new Process { StartInfo = psi };
        process.Start();

        // The readers start first, then the prompt goes down stdin. The other order deadlocks on a
        // prompt large enough to fill the pipe: this side blocks writing the rest of it while the child
        // blocks writing output nobody is draining, which is the size of prompt stdin exists for.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

        // Registered before the write, not after. A prompt big enough to block — the child not draining
        // it — would otherwise sit here with nothing left to interrupt it, so pausing during the write
        // hung the tile until the tool gave up on its own.
        using var reg = ct.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); } catch { }
        });

        if (runner.AcceptsPromptOnStdin)
            await WritePromptAsync(process, prompt);

        var output = await stdoutTask;
        var stderr = await stderrTask;

        await WaitForExitWithTimeoutAsync(process);

        ct.ThrowIfCancellationRequested();

        if (process.HasExited && process.ExitCode != 0 && !string.IsNullOrWhiteSpace(stderr))
            return $"{output.Trim()}\n\n[stderr] {stderr.Trim()}".Trim();

        return output.Trim();
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

    private static async Task KillAndWaitAsync(Process process)
    {
        if (!process.HasExited)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
        }
        await WaitForExitWithTimeoutAsync(process);
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
