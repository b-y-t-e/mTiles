namespace MTerminal.Models;

public sealed class ShellProfile
{
    public string Name { get; init; } = string.Empty;
    public string ExecutablePath { get; init; } = string.Empty;
    public string[] Args { get; init; } = [];

    public static List<ShellProfile> Detect()
    {
        var profiles = new List<ShellProfile>();

        if (OperatingSystem.IsWindows())
        {
            var pwsh = FindExecutable("pwsh.exe")
                       ?? FindExecutable("powershell.exe");
            if (pwsh != null)
                profiles.Add(new ShellProfile { Name = "PowerShell", ExecutablePath = pwsh });

            var gitBashPaths = new[]
            {
                @"C:\Program Files\Git\bin\bash.exe",
                @"C:\Program Files (x86)\Git\bin\bash.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Git", "bin", "bash.exe")
            };
            foreach (var path in gitBashPaths)
            {
                if (File.Exists(path))
                {
                    profiles.Add(new ShellProfile { Name = "Git Bash", ExecutablePath = path, Args = ["--login", "-i"] });
                    break;
                }
            }

            var cmd = FindExecutable("cmd.exe");
            if (cmd != null)
                profiles.Add(new ShellProfile { Name = "CMD", ExecutablePath = cmd });
        }
        else
        {
            var shell = Environment.GetEnvironmentVariable("SHELL") ?? "/bin/bash";
            profiles.Add(new ShellProfile { Name = Path.GetFileName(shell), ExecutablePath = shell, Args = ["-l"] });

            if (shell != "/bin/bash" && File.Exists("/bin/bash"))
                profiles.Add(new ShellProfile { Name = "bash", ExecutablePath = "/bin/bash", Args = ["-l"] });

            if (File.Exists("/bin/zsh"))
                profiles.Add(new ShellProfile { Name = "zsh", ExecutablePath = "/bin/zsh", Args = ["-l"] });
        }

        return profiles;
    }

    public static ShellProfile ResolveDefault(AppSettings settings)
    {
        var detected = Detect();

        if (!string.IsNullOrEmpty(settings.CustomShellPath))
        {
            var args = string.IsNullOrWhiteSpace(settings.CustomShellArgs)
                ? []
                : settings.CustomShellArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return new ShellProfile
            {
                Name = "Custom",
                ExecutablePath = settings.CustomShellPath,
                Args = args
            };
        }

        if (!string.IsNullOrEmpty(settings.DefaultShellName))
        {
            var match = detected.FirstOrDefault(s =>
                s.Name.Equals(settings.DefaultShellName, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;
        }

        return detected.FirstOrDefault()
            ?? new ShellProfile { Name = "Default", ExecutablePath = OperatingSystem.IsWindows() ? "powershell.exe" : "bash" };
    }

    private static string? FindExecutable(string name)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            var full = Path.Combine(dir, name);
            if (File.Exists(full)) return full;
        }
        return null;
    }
}
