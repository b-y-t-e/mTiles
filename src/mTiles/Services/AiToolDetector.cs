using System.Diagnostics;
using mTiles.Models;

namespace mTiles.Services;

public static class AiToolDetector
{
    private static readonly AiToolInfo[] KnownTools =
    [
        new() { Name = "Aider", BinaryName = "aider", Description = "AI pair programming in terminal", Url = "https://aider.chat" },
        new() { Name = "Amazon Q", BinaryName = "q", Description = "AWS AI developer tool", Url = "https://aws.amazon.com/q/developer" },
        new() { Name = "Amp", BinaryName = "amp", Description = "Sourcegraph coding agent", Url = "https://ampcode.com" },
        new() { Name = "Antigravity CLI", BinaryName = "agy", Description = "Google agentic dev platform", VersionArgs = "--version", Url = "https://antigravity.google/product/antigravity-cli" },
        new() { Name = "Claude Code", BinaryName = "claude", Description = "Anthropic CLI for Claude", Url = "https://docs.anthropic.com/en/docs/claude-code" },
        new() { Name = "Cline", BinaryName = "cline", Description = "Open-source coding agent", Url = "https://cline.bot" },
        new() { Name = "Codex", BinaryName = "codex", Description = "OpenAI Codex CLI", Url = "https://github.com/openai/codex" },
        new() { Name = "Copilot CLI", BinaryName = "copilot", Description = "GitHub AI coding agent", Url = "https://githubnext.com/projects/copilot-cli" },
        new() { Name = "Devin", BinaryName = "devin", Description = "Cognition AI engineer", Url = "https://devin.ai" },
        new() { Name = "Goose", BinaryName = "goose", Description = "Open-source AI agent", Url = "https://block.github.io/goose" },
        new() { Name = "Grok Build", BinaryName = "grok-build", Description = "xAI terminal agent", Url = "https://x.ai" },
        new() { Name = "Kilo Code", BinaryName = "kilo", Description = "Multi-agent coding CLI", Url = "https://kilocode.ai" },
        new() { Name = "Kimi Code", BinaryName = "kimi", Description = "Moonshot AI coding agent", Url = "https://github.com/MoonshotAI/kimi-code" },
        new() { Name = "Open Claude", BinaryName = "openclaude", Description = "Open-source Claude Code fork", Url = "https://openclaude.gitlawb.com" },
        new() { Name = "OpenCode", BinaryName = "opencode", Description = "Open-source AI coding agent", VersionArgs = "--version", Url = "https://opencode.ai" },
        new() { Name = "Pi Agent", BinaryName = "pi", Description = "Minimal BYOK coding agent", VersionArgs = "--version", Url = "https://github.com/mariozechner/pi-coding-agent" },
        new() { Name = "Qwen Code", BinaryName = "qwen", Description = "Alibaba terminal agent", Url = "https://github.com/QwenLM/qwen-code" },
        new() { Name = "Trae", BinaryName = "trae", Description = "ByteDance coding agent", Url = "https://github.com/bytedance/trae-agent" },
    ];

    public static List<AiToolInfo> Detect(Dictionary<string, string>? customPaths = null, List<UserAiTool>? userTools = null)
    {
        var results = new List<AiToolInfo>();

        foreach (var template in KnownTools)
        {
            var tool = new AiToolInfo
            {
                Name = template.Name,
                Description = template.Description,
                BinaryName = template.BinaryName,
                VersionArgs = template.VersionArgs,
                Url = template.Url
            };

            if (customPaths != null
                && customPaths.TryGetValue(template.BinaryName, out var customPath)
                && File.Exists(customPath))
            {
                tool.ExecutablePath = customPath;
                tool.IsInstalled = true;
                tool.IsCustomPath = true;
            }
            else
            {
                tool.ExecutablePath = FindTool(template.BinaryName);
                tool.IsInstalled = tool.ExecutablePath != null;
            }

            results.Add(tool);
        }

        if (userTools != null)
        {
            foreach (var ut in userTools)
            {
                var tool = new AiToolInfo
                {
                    Name = ut.Name,
                    Description = "Custom tool",
                    BinaryName = ut.BinaryName,
                    VersionArgs = ut.VersionArgs,
                    IsUserDefined = true,
                    UserToolId = ut.Id
                };

                if (!string.IsNullOrEmpty(ut.CustomPath) && File.Exists(ut.CustomPath))
                {
                    tool.ExecutablePath = ut.CustomPath;
                    tool.IsInstalled = true;
                    tool.IsCustomPath = true;
                }
                else
                {
                    tool.ExecutablePath = FindTool(ut.BinaryName);
                    tool.IsInstalled = tool.ExecutablePath != null;
                }

                results.Add(tool);
            }
        }

        return results;
    }

    public static async Task<string?> TestAsync(AiToolInfo tool)
    {
        if (tool.ExecutablePath == null) return null;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var psi = new ProcessStartInfo
            {
                FileName = tool.ExecutablePath,
                Arguments = tool.VersionArgs,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null) return null;

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            try { await process.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { try { process.Kill(); } catch { } }

            var output = string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;
            var firstLine = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
            return string.IsNullOrEmpty(firstLine) ? null : firstLine;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("AI tool test failed for {0}: {1}", tool.Name, ex.Message);
            return null;
        }
    }

    private static string? FindTool(string binaryName)
    {
        if (OperatingSystem.IsWindows())
        {
            return ShellDetector.FindExecutable(binaryName + ".exe")
                ?? ShellDetector.FindExecutable(binaryName + ".cmd")
                ?? ShellDetector.FindExecutable(binaryName + ".bat")
                ?? ShellDetector.FindExecutable(binaryName)
                ?? FindInHomeDirs(binaryName, ".exe", ".cmd");
        }

        return ShellDetector.FindExecutable(binaryName)
            ?? FindInHomeDirs(binaryName, "");
    }

    private static string? FindInHomeDirs(string binaryName, params string[] extensions)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home)) return null;

        var dirs = new[]
        {
            Path.Combine(home, ".local", "bin"),
            Path.Combine(home, "go", "bin"),
            Path.Combine(home, $".{binaryName}", "bin"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"),
            Path.Combine(home, ".cargo", "bin"),
        };

        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var ext in extensions)
            {
                var full = Path.Combine(dir, binaryName + ext);
                if (File.Exists(full)) return full;
            }
            var bare = Path.Combine(dir, binaryName);
            if (File.Exists(bare)) return bare;
        }

        return null;
    }
}
