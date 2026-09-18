using System.Diagnostics;
using mTiles.Models;
using mTiles.Services;
using mTiles.Services.Agents;
using mTiles.Services.Providers;
using mTiles.Services.Shells;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// How a shell is told to run a program, and why PowerShell has to be told the file.
/// </summary>
/// <remarks>
/// <para><b>The failure this exists for, reported from a fresh Windows 11 Home.</b> mTiles found Claude
/// Code, offered it, and every agent tile then answered <c>claude.ps1 cannot be loaded because running
/// scripts is disabled on this system</c>. npm installs three shims per tool on Windows — <c>claude</c>,
/// <c>claude.cmd</c> and <c>claude.ps1</c> — and PowerShell's own lookup prefers the <c>.ps1</c>, which
/// the default <c>Restricted</c> execution policy refuses to load. On a developer's machine the policy
/// has usually been changed by something else years earlier, which is why this shipped.</para>
/// <para>The fix is not to loosen that policy for the tile: it would weaken every other script in that
/// session on a machine where somebody switched scripts off on purpose, and it would still fail where
/// the policy comes from group policy. The fix is to name the file, which no policy applies to.</para>
/// </remarks>
public class PowerShellProgramTests
{
    /// <summary>Every shell but PowerShell leaves the lookup to itself.</summary>
    /// <remarks>Which is not laziness: mise, asdf, volta and nvm all answer <c>claude</c> with a
    /// different binary depending on the directory, and a path this application resolved once would pin
    /// one of them. On POSIX a name also cannot resolve to something the platform then refuses to
    /// run — the case PowerShell has and they do not.</remarks>
    [Theory]
    [InlineData("claude", "/home/me/.local/bin/claude")]
    [InlineData("opencode", null)]
    public void A_posix_shell_runs_a_program_by_its_name(string name, string? path)
    {
        Assert.Equal(name, new BashTerminal().Program(name, path, []));
        Assert.Equal(name, new ZshTerminal().Program(name, path, []));
        Assert.Equal(name, new FishTerminal().Program(name, path, []));
        Assert.Equal(name, new GitBashTerminal().Program(name, path, []));
    }

    /// <summary>PowerShell is given the file, through the call operator, quoted.</summary>
    /// <remarks>The operator for the reason <c>Invoke</c> uses it — a quoted first token is a string
    /// expression here, not a command — and the quoting because the ordinary place for these on Windows
    /// is <c>C:\Program Files\nodejs</c>.</remarks>
    [Fact]
    public void Powershell_is_told_which_file_to_run()
    {
        var shell = new PowerShellTerminal();

        Assert.Equal("""& 'C:\Program Files\nodejs\claude.cmd'""",
            shell.Program("claude", @"C:\Program Files\nodejs\claude.cmd", ["--resume", "the-id"]));

        // No path is the machine where the binary is not on our PATH either, so the tile is about to
        // fail whatever we write: the shell's own "not recognized" beats a path invented here.
        Assert.Equal("claude", shell.Program("claude", null, []));
        Assert.Equal("claude", shell.Program("claude", "", []));
    }

    /// <summary>A batch shim is not handed an argument <c>cmd.exe</c> would read a command into.</summary>
    /// <remarks>A <c>.cmd</c> passes its arguments through <c>cmd.exe</c> after PowerShell has taken its
    /// own quotes off, so <c>x&amp;calc</c> — a session id out of a hand-edited layout — would run
    /// <c>calc</c>. Such a line falls back to the name, which cannot run a second command; an
    /// <c>.exe</c> parses its own command line and keeps the path.</remarks>
    [Theory]
    [InlineData("x&calc")]
    [InlineData("%PATH%")]
    [InlineData("a|b")]
    [InlineData(@"C:\Users\A&B\AppData\Roaming\mTiles\sessions\opencode\ses_x.json")]
    public void A_batch_shim_is_refused_an_argument_cmd_would_reinterpret(string argument)
    {
        var shell = new PowerShellTerminal();
        Assert.Equal("claude", shell.Program("claude", @"C:\nodejs\claude.cmd", ["--resume", argument]));
        Assert.Equal("claude", shell.Program("claude", @"C:\nodejs\claude.BAT", [argument]));
        Assert.Equal("""& 'C:\bin\claude.exe'""", shell.Program("claude", @"C:\bin\claude.exe", [argument]));
    }

    /// <summary>A path under an ordinary profile still reaches a batch shim.</summary>
    /// <remarks>A space, a backslash and a colon mean nothing to <c>cmd.exe</c> inside the quotes
    /// PowerShell passes them on in — refusing them would send opencode's import back to its
    /// <c>.ps1</c> on every machine whose account name has a space in it.</remarks>
    [Fact]
    public void A_batch_shim_takes_a_path_under_an_ordinary_profile()
    {
        var shell = new PowerShellTerminal();
        Assert.Equal("""& 'C:\nodejs\opencode.cmd'""", shell.Program("opencode", @"C:\nodejs\opencode.cmd",
            ["import", @"C:\Users\Jan Kowalski\AppData\Roaming\mTiles\sessions\opencode\ses_x.json"]));
    }

    /// <summary>What an agent writes into its own commands is part of the question too.</summary>
    /// <remarks>opencode's fallback carries the import document's path, which is under the user's
    /// profile — and a profile name may carry an <c>&amp;</c>. Asked about the session id alone, the
    /// shell would hand that line to <c>opencode.cmd</c>.</remarks>
    [Fact]
    public void Opencode_tells_the_shell_about_its_import_path()
    {
        AiAgentCatalog.PretendEveryAgentIsInstalled();
        var tileId = Guid.NewGuid().ToString();
        var recording = new RecordingShell();
        var runtime = AgentRuntime.For(new AppSettings(), new AiAgentInstance
        {
            DefaultBehaviour = AiBehaviour.ToolDefault,
            DefaultEffort = AiEffort.ToolDefault,
        });
        var agent = AiAgentCatalog.All.Single(candidate => candidate is OpenCodeAgent);

        agent.Interactive(runtime, agent.SessionIdForTile(tileId), recording);

        Assert.Contains(OpenCodeSession.DocumentPath(tileId), recording.Arguments);
    }

    /// <summary>PowerShell, remembering the arguments it was asked to run a program with.</summary>
    private sealed class RecordingShell : IShellTerminal
    {
        private readonly PowerShellTerminal _shell = new();
        public List<string> Arguments { get; } = [];

        public string Id => _shell.Id;
        public string DisplayName => _shell.DisplayName;
        public string IconId => _shell.IconId;
        public IReadOnlyList<string> DetectPaths() => _shell.DetectPaths();
        public IReadOnlyList<string> InteractiveArgs => _shell.InteractiveArgs;
        public IReadOnlyList<string> CommandArgs => _shell.CommandArgs;
        public IReadOnlyList<string> NoProfileArgs => _shell.NoProfileArgs;
        public string Quote(string value) => _shell.Quote(value);
        public string SetEnv(string name, string value) => _shell.SetEnv(name, value);
        public string UnsetEnv(string name) => _shell.UnsetEnv(name);
        public string WithEnv(IReadOnlyDictionary<string, string?> environment, string command) =>
            _shell.WithEnv(environment, command);

        public string Program(string name, string? path, IReadOnlyList<string> arguments)
        {
            Arguments.AddRange(arguments);
            return _shell.Program(name, path, arguments);
        }
    }

    /// <summary>
    /// No agent's own command line starts with a bare binary name on PowerShell.
    /// </summary>
    /// <remarks>The regression guard, and it is asked of every agent rather than of the one that was
    /// reported: all six are npm-installed CLIs on Windows, and a new one is written by copying an old
    /// one. Each <c>Resume</c> is handed the spelling by <c>AiAgent.Interactive</c> — an agent that
    /// spells its own binary instead is what this catches.</remarks>
    [Fact]
    public void No_agent_launches_a_script_shim_on_powershell()
    {
        // Every agent "found" whatever this machine has installed, so the assertion is about the
        // spelling and never about which CLIs happen to be on PATH here.
        AiAgentCatalog.PretendEveryAgentIsInstalled();
        var shell = new PowerShellTerminal();
        var runtime = AgentRuntime.For(new AppSettings(), new AiAgentInstance
        {
            DefaultBehaviour = AiBehaviour.ToolDefault,
            DefaultEffort = AiEffort.ToolDefault,
        });

        foreach (var agent in AiAgentCatalog.All)
        {
            var plan = agent.Interactive(runtime, "the-id", shell);

            foreach (var command in new[] { plan.Startup, plan.Fallback })
            {
                if (command is null) continue;

                Assert.StartsWith("& '", command, StringComparison.Ordinal);
                Assert.DoesNotContain(".ps1", command, StringComparison.OrdinalIgnoreCase);
                Assert.False(command.StartsWith(agent.BinaryName, StringComparison.Ordinal),
                    $"{agent.Id} runs its binary by name: {command}");
            }
        }
    }

    /// <summary>What this application looks for is never a script in the first place.</summary>
    /// <remarks>The other half of the same rule: <c>ExecutableFinder.Anywhere</c> asks for
    /// <c>.exe</c>, then the <c>.cmd</c> shim, then the extensionless name — and never <c>.ps1</c>. A
    /// <c>.ps1</c> added to that list would put the failure back by the route the shells cannot see.
    /// </remarks>
    [Theory]
    [InlineData("npm")]
    [InlineData("claude")]
    [InlineData("codex")]
    public void What_the_finder_answers_with_is_never_a_script(string name)
    {
        // Whatever this machine has: a name it cannot find is passed through, and that is an answer
        // too — what must never come back is a .ps1, on the machines that do have one.
        Assert.DoesNotContain(".ps1", ExecutableFinder.Anywhere(name) ?? name,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// And on a machine where scripts are switched off, the spelling actually runs.
    /// </summary>
    /// <remarks><b>Measured against a real PowerShell rather than asserted about a string.</b> The
    /// whole claim is about somebody else's command lookup and somebody else's policy, and the string
    /// tests above would pass just as green if either moved. <c>-ExecutionPolicy Restricted</c>
    /// reproduces the reported machine on a developer's one, where the policy has long since been
    /// changed; the control case is the same command spelled as a bare name, which must fail with the
    /// policy error that started this.</remarks>
    [Fact]
    public void A_shim_runs_where_a_script_would_be_refused()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var directory = new TempDirectory();
        var cmd = Path.Combine(directory.Path, "mtiles-probe.cmd");
        File.WriteAllText(cmd, "@echo ran-the-shim\r\n");
        File.WriteAllText(Path.Combine(directory.Path, "mtiles-probe.ps1"), "Write-Host ran-the-script\r\n");

        var shell = new PowerShellTerminal();

        var told = RunRestricted(shell.Program("mtiles-probe", cmd, []), directory.Path);
        Assert.Contains("ran-the-shim", told, StringComparison.Ordinal);

        // The control: the name alone finds the .ps1 and the policy refuses it. If this ever stops
        // failing, the reason for all of the above has gone away and it can go with it.
        var byName = RunRestricted(shell.Program("mtiles-probe", null, []), directory.Path);
        Assert.DoesNotContain("ran-the-shim", byName, StringComparison.Ordinal);
        // The error id rather than the sentence: the sentence is translated on a localized Windows.
        Assert.Contains("UnauthorizedAccess", byName, StringComparison.Ordinal);
    }

    /// <summary>Runs <paramref name="command"/> in Windows PowerShell with the default policy of a
    /// machine nobody has changed, and with <paramref name="onPath"/> in front of <c>PATH</c>.</summary>
    private static string RunRestricted(string command, string onPath)
    {
        var psi = new ProcessStartInfo("powershell.exe")
        {
            ArgumentList = { "-NoProfile", "-ExecutionPolicy", "Restricted", "-Command", command },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.Environment["PATH"] = onPath + Path.PathSeparator + (psi.Environment["PATH"] ?? "");

        using var process = Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit(30_000);
        return output;
    }
}
