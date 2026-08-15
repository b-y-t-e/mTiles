using mTiles.Models;

namespace mTiles.Services;

public static class ShellDetector
{
    private static Dictionary<string, ShellType>? _typeLookup;

    public static ShellType GetTypeByName(string shellName)
    {
        _typeLookup ??= Detect().ToDictionary(s => s.Name, s => s.Type, StringComparer.OrdinalIgnoreCase);
        return _typeLookup.TryGetValue(shellName, out var t) ? t : ShellType.Other;
    }

    public static List<ShellProfile> Detect()
    {
        var profiles = new List<ShellProfile>();

        if (OperatingSystem.IsWindows())
        {
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
                    profiles.Add(new ShellProfile { Name = "Git Bash", ExecutablePath = path, Args = ["--login", "-i"], Type = ShellType.Bash });
                    break;
                }
            }

            var pwsh = FindExecutable("pwsh.exe")
                       ?? FindExecutable("powershell.exe");
            if (pwsh != null)
                profiles.Add(new ShellProfile { Name = "PowerShell", ExecutablePath = pwsh, Type = ShellType.PowerShell });

            var cmd = FindExecutable("cmd.exe");
            if (cmd != null)
                profiles.Add(new ShellProfile { Name = "CMD", ExecutablePath = cmd, Type = ShellType.Cmd });
        }
        else
        {
            var shell = Environment.GetEnvironmentVariable("SHELL") ?? "/bin/bash";
            var shellName = Path.GetFileNameWithoutExtension(shell);
            profiles.Add(new ShellProfile { Name = Path.GetFileName(shell), ExecutablePath = shell, Args = ["-l"], Type = InferType(shell) });

            if (shellName != "bash" && File.Exists("/bin/bash"))
                profiles.Add(new ShellProfile { Name = "bash", ExecutablePath = "/bin/bash", Args = ["-l"], Type = ShellType.Bash });

            if (shellName != "zsh" && File.Exists("/bin/zsh"))
                profiles.Add(new ShellProfile { Name = "zsh", ExecutablePath = "/bin/zsh", Args = ["-l"], Type = ShellType.Zsh });

            var fishPath = File.Exists("/usr/bin/fish") ? "/usr/bin/fish" : File.Exists("/bin/fish") ? "/bin/fish" : null;
            if (fishPath != null && shellName != "fish")
                profiles.Add(new ShellProfile { Name = "fish", ExecutablePath = fishPath, Args = ["-l"], Type = ShellType.Fish });
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
                Args = args,
                Type = settings.CustomShellType
            };
        }

        if (!string.IsNullOrEmpty(settings.DefaultShellName))
        {
            var match = detected.FirstOrDefault(s =>
                s.Name.Equals(settings.DefaultShellName, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;
        }

        if (!OperatingSystem.IsWindows())
        {
            var bash = detected.FirstOrDefault(s => s.Name.Equals("bash", StringComparison.OrdinalIgnoreCase));
            if (bash != null) return bash;
        }

        var fallbackExe = OperatingSystem.IsWindows() ? "powershell.exe" : "bash";
        return detected.FirstOrDefault()
            ?? new ShellProfile { Name = "Default", ExecutablePath = fallbackExe, Type = InferType(fallbackExe) };
    }

    /// <summary>
    /// The shell a profile's <em>command chain</em> runs in — the same one the tile uses, unless that is
    /// <c>cmd.exe</c>, which is swapped for PowerShell or a POSIX shell.
    /// </summary>
    /// <remarks>
    /// <para><c>cmd</c> cannot run what these profiles are made of. It does not parse its command line by
    /// the <c>CommandLineToArgvW</c> rules the PTY backend quotes with, it runs only the first line of a
    /// multi-line command, and it does not treat <c>;</c> as a separator — measured, and the last of
    /// those is what silently reduced the seeded OpenCode profile to a bare shell, because its fallback
    /// is two commands in one.</para>
    /// <para>Only the chain is moved. The interactive shell a tile ends up in stays the one the user
    /// chose: <c>cmd</c> is a perfectly good thing to be typing into, and this is not the place to have
    /// an opinion about that. What it cannot be is the thing running a profile's commands behind
    /// their back.</para>
    /// <para>Substituting rather than refusing, because a tile that will not launch teaches nobody
    /// anything. It is traced by the caller, so the swap is visible where a surprise would be explained.</para>
    /// <para>The type is checked <em>before</em> <see cref="Detect"/> is called, and that ordering is
    /// load-bearing rather than tidiness: detection walks every directory on <c>PATH</c> and stats a
    /// handful of fixed locations, this runs on the UI thread on every tile launch, and all but a few
    /// profiles are not <c>cmd</c> at all.</para>
    /// </remarks>
    public static ShellProfile ResolveForCommands(ShellProfile shell) =>
        shell.Type != ShellType.Cmd ? shell : ResolveForCommands(shell, Detect());

    /// <param name="detected">The shells to choose a replacement from. A parameter so the rule can be
    /// read in a test without depending on what the machine running it happens to have installed.</param>
    /// <inheritdoc cref="ResolveForCommands(ShellProfile)"/>
    internal static ShellProfile ResolveForCommands(ShellProfile shell, IReadOnlyList<ShellProfile> detected)
    {
        if (shell.Type != ShellType.Cmd)
            return shell;

        // In order of preference, and the order is the point: PowerShell is on every Windows machine and
        // is where a cmd user's profile most likely still works, so it comes first. A POSIX shell after
        // it. Anything at all that is not cmd last — a shell whose flag mapping we guess at (`-c`) still
        // beats the one measured to mishandle every command it is given.
        var replacement =
            detected.FirstOrDefault(s => s.Type == ShellType.PowerShell)
            ?? detected.FirstOrDefault(s => s.Type is ShellType.Bash or ShellType.Zsh)
            ?? detected.FirstOrDefault(s => s.Type != ShellType.Cmd);
        if (replacement != null)
            return replacement;

        // Nothing else is installed. Handing the chain back to cmd is the least bad answer left: the
        // commands may still work — this is about `;`, quoting and multiple lines, not about everything —
        // and the alternative is a tile that runs nothing at all. The caller warns.
        return shell;
    }

    public static ShellProfile ResolveFromUserProfile(UserShellProfile userProfile, AppSettings settings)
    {
        var detected = Detect();
        var match = detected.FirstOrDefault(s =>
            s.Name.Equals(userProfile.ShellName, StringComparison.OrdinalIgnoreCase));
        return match ?? ResolveDefault(settings);
    }

    public static ShellType InferType(string executablePath)
    {
        var name = Path.GetFileNameWithoutExtension(executablePath).ToLowerInvariant();
        return name switch
        {
            "pwsh" or "powershell" => ShellType.PowerShell,
            "cmd" => ShellType.Cmd,
            "bash" or "sh" => ShellType.Bash,
            "zsh" => ShellType.Zsh,
            "fish" => ShellType.Fish,
            _ => ShellType.Other
        };
    }

    internal static string? FindExecutable(string name)
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
