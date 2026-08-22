using System.Diagnostics;
using System.Text;

namespace mTiles.Services.Phone;

/// <summary>What happened when the user asked for the firewall to be opened.</summary>
internal enum FirewallOutcome
{
    /// <summary>The rule is in place.</summary>
    Allowed,

    /// <summary>The user dismissed the elevation prompt. Not an error — a decision.</summary>
    Declined,

    /// <summary>This platform cannot be driven from here; <see cref="FirewallAdvice.ManualCommand"/> says what to run.</summary>
    Manual,

    /// <summary>It was attempted and failed. The message says why.</summary>
    Failed,
}

/// <summary>The result of a repair attempt, and what to tell the user afterwards.</summary>
internal sealed record FirewallResult(FirewallOutcome Outcome, string Message);

/// <summary>What the panel should offer when nothing connects.</summary>
/// <param name="CanRepair">Whether <see cref="IFirewallGuide.TryAllowAsync"/> is worth offering as a button.</param>
/// <param name="Explanation">One sentence naming the likely cause, in the user's terms.</param>
/// <param name="ManualCommand">A command to copy, for platforms where this cannot be automated. Empty otherwise.</param>
internal sealed record FirewallAdvice(bool CanRepair, string Explanation, string ManualCommand);

/// <summary>
/// Opening the local firewall for the bridge's port.
/// </summary>
/// <remarks>
/// An interface for the usual reason and one unusual one: the platforms differ not in *how* they do this
/// but in whether it can be done at all. Windows has a consented-elevation convention (UAC) that a GUI
/// may legitimately invoke; Linux has no equivalent that can be driven from a desktop application without
/// guessing at a privilege helper, so the honest implementation there hands over a command instead of
/// pretending. A single implementation with an <c>if</c> would have made that difference look like a
/// detail, when it is the whole design.
/// </remarks>
internal interface IFirewallGuide
{
    /// <summary>What to show when the phone cannot reach the bridge.</summary>
    FirewallAdvice GetAdvice(int port);

    /// <summary>
    /// Asks the operating system to allow inbound connections to <paramref name="port"/>, prompting the
    /// user for the privileges to do so. Never silent: the prompt is the consent.
    /// </summary>
    Task<FirewallResult> TryAllowAsync(int port);
}

/// <summary>
/// Windows Firewall, through an elevated PowerShell one-liner.
/// </summary>
/// <remarks>
/// <b>The block rule is the real target.</b> When a process first listens on a non-loopback address,
/// Windows raises its own "Allow this app to communicate" dialog — and if the user dismisses it, Windows
/// writes a *block* rule and never asks again. The feature then fails for ever with no message anywhere,
/// which is the single most common way something like this dies on a Windows machine. So this does not
/// merely add an allow rule: it first removes every inbound rule pointing at this executable, blocking
/// ones included, because an existing block would otherwise win over anything added afterwards.
/// <para><b>Private profile only.</b> The bridge is meant to be reachable from a phone on the user's own
/// Wi-Fi. Opening it on the public profile would expose it on café and airport networks too, which is
/// exactly where a paired-device bridge should not be listening — and the pairing token is the second
/// line of defence, not a reason to skip the first.</para>
/// <para><b>Scoped to the executable, not to a port number.</b> The bridge does not always get the port
/// it asked for — Windows reserves blocks of them for Hyper-V and WSL at boot — so it falls back to a
/// free one, and a rule naming a single port would silently stop matching the moment that happened. The
/// program is the identity that matters here in any case; the alternative is a rule needing
/// re-approval, with a UAC prompt, every time the machine hands out a different number.</para>
/// <para>The trade-off holds only while this program has exactly one purpose-built inbound listener. The
/// rule says "mTiles may accept connections", not "mTiles may accept connections on the bridge's port",
/// so anything else that ever listens inbound from this executable inherits the permission without
/// anybody deciding to grant it. If a second listener is ever added, this goes back to naming a port and
/// the fallback has to be reconsidered with it.</para>
/// </remarks>
internal sealed class WindowsFirewallGuide : IFirewallGuide
{
    internal const string RuleName = "mTiles phone bridge";

    /// <summary>The script's own exit code for "the rule is in place but this network is Public".</summary>
    internal const int NoPrivateNetwork = 2;

    /// <summary>How long to wait for the elevation prompt to be answered.</summary>
    private static readonly TimeSpan ElevationTimeout = TimeSpan.FromMinutes(2);

    public FirewallAdvice GetAdvice(int port) => new(
        CanRepair: true,
        Explanation:
            "Windows Firewall may be blocking the connection. If you dismissed the "
            + "“Allow access” prompt when this started, Windows recorded that as a block and "
            + "will not ask again. Repairing it asks for administrator rights, then replaces every "
            + "existing inbound firewall rule for mTiles with a single one allowing it on private "
            + "networks — so any inbound rule you added for mTiles yourself is removed.",
        ManualCommand: "");

    public async Task<FirewallResult> TryAllowAsync(int port)
    {
        var program = Environment.ProcessPath;
        if (string.IsNullOrEmpty(program))
            return new FirewallResult(FirewallOutcome.Failed, "The application's own path could not be determined.");

        try
        {
            // The full path, not "powershell.exe". This is launched with runas, so PATH resolution here
            // is a way to get somebody else's binary elevated: anything earlier on PATH named
            // powershell.exe would be run as administrator by a user who thought they were fixing a
            // firewall rule.
            var shell = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe");

            var startInfo = new ProcessStartInfo(shell)
            {
                // -EncodedCommand takes base64 UTF-16, which sidesteps quoting entirely: the script
                // contains a Windows path with spaces and several nested quotation marks, and building
                // that as a command line is how an injection or a silent no-op gets in.
                Arguments = $"-NoProfile -NonInteractive -EncodedCommand {Encode(BuildScript(program))}",
                UseShellExecute = true,   // required for runas
                Verb = "runas",           // the UAC prompt: this is where the user consents
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            using var process = Process.Start(startInfo);
            if (process is null)
                return new FirewallResult(FirewallOutcome.Failed, "The firewall command could not be started.");

            // Bounded. The wait is on a UAC prompt, which a user can leave on screen indefinitely — and
            // without a timeout the panel's command never completes, so its button stays busy for the
            // rest of the session with nothing to show for it.
            using var patience = new CancellationTokenSource(ElevationTimeout);

            try
            {
                await process.WaitForExitAsync(patience.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return new FirewallResult(FirewallOutcome.Failed,
                    "The firewall change was not confirmed in time. Try again, and answer the "
                    + "administrator prompt when it appears.");
            }

            return process.ExitCode switch
            {
                // Says what was done, not what is now possible. Windows Firewall is not the only thing
                // that can be in the way — a third-party firewall, or a blocking rule that names a port
                // rather than this program — and claiming the phone will get through is a promise this
                // has no way to keep.
                0 => new FirewallResult(FirewallOutcome.Allowed,
                    "The rule is in place: Windows Firewall allows mTiles on private networks. If the "
                    + "phone still cannot connect, something other than Windows Firewall is blocking it."),

                // The rule exists but applies to nothing: Windows has this machine's network classified
                // as Public, and a Private-profile rule is inert there. Reporting success would send the
                // user hunting for a different fault entirely.
                NoPrivateNetwork => new FirewallResult(FirewallOutcome.Failed,
                    "The rule was added, but Windows treats this network as Public, where it does not "
                    + "apply. Set the network to Private in Windows settings, or use the Tailscale code "
                    + "instead."),

                var code => new FirewallResult(FirewallOutcome.Failed,
                    $"The firewall rule could not be created (exit code {code})."),
            };
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED — the user said no to UAC. A decision, reported as such rather than as a
            // failure, because telling somebody their deliberate refusal went wrong is just noise.
            return new FirewallResult(FirewallOutcome.Declined,
                "The firewall was left unchanged.");
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Firewall rule creation failed: {0}", ex);
            return new FirewallResult(FirewallOutcome.Failed, ex.Message);
        }
    }

    /// <summary>
    /// Remove every inbound rule for this executable, then add exactly one.
    /// </summary>
    /// <remarks>
    /// Scoped to this program's own path, so it can neither read nor disturb rules belonging to anything
    /// else on the machine. <c>-ErrorAction SilentlyContinue</c> throughout because "there was no rule to
    /// remove" is the ordinary case on a first run, not a failure worth a non-zero exit.
    /// <para>No port, and so no port parameter either: the rule is scoped to the executable. See the note
    /// on the class for why, and for the condition under which that stays the right trade.</para>
    /// </remarks>
    internal static string BuildScript(string program) =>
        // Doubled braces are the interpolation holes ($$), so PowerShell's own braces stay single and
        // the script below reads as the script that actually runs.
        $$"""
          $ErrorActionPreference = 'Continue'
          $program = '{{Escape(program)}}'
          Get-NetFirewallApplicationFilter -Program $program -ErrorAction SilentlyContinue | Get-NetFirewallRule -ErrorAction SilentlyContinue | Where-Object { $_.Direction -eq 'Inbound' } | Remove-NetFirewallRule -ErrorAction SilentlyContinue
          Remove-NetFirewallRule -DisplayName '{{RuleName}}' -ErrorAction SilentlyContinue
          New-NetFirewallRule -DisplayName '{{RuleName}}' -Direction Inbound -Action Allow -Program $program -Protocol TCP -Profile Private -ErrorAction Stop | Out-Null
          $rule = Get-NetFirewallRule -DisplayName '{{RuleName}}' -ErrorAction SilentlyContinue
          if (-not $rule) { exit 3 }
          $private = Get-NetConnectionProfile -ErrorAction SilentlyContinue | Where-Object { $_.NetworkCategory -eq 'Private' }
          if (-not $private) { exit {{NoPrivateNetwork}} }
          exit 0
          """;

    /// <summary>Doubles single quotes — the only escape a PowerShell single-quoted string has.</summary>
    private static string Escape(string value) => value.Replace("'", "''");

    private static string Encode(string script) =>
        Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
}

/// <summary>
/// Hands over the command rather than running it.
/// </summary>
/// <remarks>
/// Deliberately does nothing on its own. There is no desktop-wide consented-elevation prompt to invoke,
/// the firewall in use has to be guessed at, and a GUI application that shells out to <c>sudo</c> either
/// finds no terminal to prompt in or teaches the user to grant root to a program that asked politely.
/// Printing the command respects that the person running Linux can decide for themselves.
/// </remarks>
internal sealed class ManualFirewallGuide : IFirewallGuide
{
    public FirewallAdvice GetAdvice(int port) => new(
        CanRepair: false,
        Explanation: "A local firewall may be blocking the connection.",
        ManualCommand: $"sudo ufw allow {port}/tcp   # or: sudo firewall-cmd --add-port={port}/tcp");

    public Task<FirewallResult> TryAllowAsync(int port) => Task.FromResult(
        new FirewallResult(FirewallOutcome.Manual, "Run the command shown to open the port."));
}

internal static class FirewallGuide
{
    public static IFirewallGuide ForThisMachine() =>
        OperatingSystem.IsWindows() ? new WindowsFirewallGuide() : new ManualFirewallGuide();
}
