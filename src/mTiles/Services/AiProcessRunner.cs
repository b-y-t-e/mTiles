using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace mTiles.Services;

public sealed class AiOutputChunk
{
    public string Type { get; init; } = "text";
    public string Content { get; init; } = "";
}

public interface IAiToolRunner
{
    void ConfigureProcess(ProcessStartInfo psi, string prompt, string model, int maxTurns, bool streaming);
    AiOutputChunk? ParseLine(string line);
}

public sealed class ClaudeToolRunner : IAiToolRunner
{
    public void ConfigureProcess(ProcessStartInfo psi, string prompt, string model, int maxTurns, bool streaming)
    {
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add(prompt);
        psi.ArgumentList.Add("--model");
        psi.ArgumentList.Add(model);
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

public static class AiProcessRunner
{
    private static readonly ConcurrentDictionary<string, IAiToolRunner> Runners = new(StringComparer.OrdinalIgnoreCase)
    {
        ["claude"] = new ClaudeToolRunner()
    };

    public static void RegisterRunner(string toolBinary, IAiToolRunner runner) =>
        Runners[toolBinary] = runner;

    public static IAiToolRunner GetRunner(string toolBinary) =>
        Runners.GetValueOrDefault(toolBinary) ?? new ClaudeToolRunner();

    private static readonly TimeSpan ProcessExitTimeout = TimeSpan.FromSeconds(10);

    public static async IAsyncEnumerable<AiOutputChunk> RunStreamingAsync(
        string executablePath,
        string prompt,
        string workingDirectory,
        string model,
        IAiToolRunner runner,
        int maxTurns = 20,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var psi = CreateProcessStartInfo(executablePath, workingDirectory);
        runner.ConfigureProcess(psi, prompt, model, maxTurns, streaming: true);

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

        var reader = process.StandardOutput;
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line == null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;

            var chunk = runner.ParseLine(line);
            if (chunk != null)
                yield return chunk;
        }

        await KillAndWaitAsync(process);

        var stderrOutput = await stderrTask;
        if (process.ExitCode != 0 && !ct.IsCancellationRequested && !string.IsNullOrWhiteSpace(stderrOutput))
            yield return new AiOutputChunk { Type = "error", Content = stderrOutput.Trim() };
    }

    public static async Task<string> RunPlainAsync(
        string executablePath,
        string prompt,
        string workingDirectory,
        string model,
        IAiToolRunner runner,
        int maxTurns = 20,
        CancellationToken ct = default)
    {
        var psi = CreateProcessStartInfo(executablePath, workingDirectory);
        runner.ConfigureProcess(psi, prompt, model, maxTurns, streaming: false);

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

        using var reg = ct.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); } catch { }
        });

        var output = await stdoutTask;
        var stderr = await stderrTask;

        await WaitForExitWithTimeoutAsync(process);

        ct.ThrowIfCancellationRequested();

        if (process.ExitCode != 0 && !string.IsNullOrWhiteSpace(stderr))
            return $"{output.Trim()}\n\n[stderr] {stderr.Trim()}".Trim();

        return output.Trim();
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
