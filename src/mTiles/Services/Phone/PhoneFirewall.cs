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
    /// What the firewall looks like <em>right now</em>, read without asking for any privileges. Empty
    /// when there is nothing to say.
    /// </summary>
    /// <remarks>
    /// The point is that the failure this class exists to fight is silent. A missing rule, a block rule
    /// Windows wrote when its own prompt was dismissed, a network Windows has classified as Public, a
    /// managed machine whose policy ignores local rules — every one of them looks identical from the
    /// phone: a page that will not load. Saying which it is costs one unelevated process and turns an
    /// unanswerable question into a sentence.
    /// <para>The port is passed even though Windows does not use it — its rule is scoped to the
    /// executable. Linux firewalls are scoped to ports, and the whole value of the answer there is the
    /// command with the right number in it. This parameter was removed once, correctly, when the only
    /// implementation that could use it did nothing; it came back with that implementation.</para>
    /// </remarks>
    Task<string> DiagnoseAsync(int port);

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
/// <para><b>Private and Domain, never Public.</b> The bridge is meant to be reachable from a phone on a
/// network the user is actually on — their own Wi-Fi, or the office network of a machine joined to a
/// domain. Opening it on the public profile would expose it on café and airport networks too, which is
/// exactly where a paired-device bridge should not be listening — and the pairing token is the second
/// line of defence, not a reason to skip the first.
/// <br/>Domain was missing, and the consequence was worse than the feature not working: the check at the
/// end of the script asked only whether a <em>Private</em> network was active, so a domain-joined machine
/// was told "Windows treats this network as Public" and sent to change a network category that a domain
/// controller sets and the user cannot touch. A diagnosis that is wrong costs more than none.</para>
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

    /// <summary>
    /// The script's own exit codes. Shared by the repair and the read-only check, which run the same
    /// verification: two copies of "is this machine reachable" would answer differently the first time
    /// one of them was edited.
    /// </summary>
    internal const int NotCovered = 2;         // the network a phone would be on is not one the rule covers
    internal const int NoRule = 3;             // nothing allows mTiles in
    internal const int PolicyIgnoresLocalRules = 4;
    internal const int Blocked = 5;            // a block rule for this executable
    internal const int CheckFailed = 6;        // the question could not be asked
    internal const int NoNetwork = 7;          // this machine is not on any network

    /// <summary>How long to wait for the elevation prompt to be answered.</summary>
    private static readonly TimeSpan ElevationTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The read-only check asks nobody for anything, so it may not sit there.
    /// </summary>
    /// <remarks>
    /// Deliberately below <c>PhoneBridgeViewModel.StartupPatience</c>, which is what closing the panel
    /// waits for. Twenty seconds here outlasted that ten, so closing a panel on a machine where
    /// <c>Get-NetFirewallRule</c> was slow held the bridge open and logged "still starting" about a
    /// bridge that had started long before. The check no longer runs inside that wait either — both,
    /// because one of them is a number somebody will change.
    /// </remarks>
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(8);

    public FirewallAdvice GetAdvice(int port) => new(
        CanRepair: true,
        Explanation:
            "Windows Firewall may be blocking the connection. If you dismissed the "
            + "“Allow access” prompt when this started, Windows recorded that as a block and "
            + "will not ask again. Repairing it asks for administrator rights, then replaces every "
            + "existing inbound firewall rule for mTiles with a single one allowing it on private "
            + "and domain networks — so any inbound rule you added for mTiles yourself is removed.",
        ManualCommand: "");

    public async Task<string> DiagnoseAsync(int port)
    {
        var program = Environment.ProcessPath;
        if (string.IsNullOrEmpty(program)) return "";

        var code = await RunCheckAsync(program).ConfigureAwait(false);

        return code switch
        {
            // First, because it beats everything added afterwards and because it is the failure this
            // whole class exists for: Windows writes it when its own "Allow access" prompt is dismissed,
            // and then never asks again.
            Blocked =>
                "Windows Firewall has a rule that blocks mTiles — Windows writes one when its “Allow "
                + "access” prompt is dismissed, and does not ask again. Repair replaces it.",

            NoRule =>
                "Windows Firewall has no rule allowing mTiles in, so a phone on your network cannot "
                + "reach this machine. Repair adds one.",

            NotCovered =>
                "The rule that lets mTiles in does not cover the network your phone would be on — "
                + "Windows most likely classifies it as Public. Set that network to Private in Windows "
                + "settings, repair the rule below, or use the Tailscale code instead.",

            // Deliberately not silence: this is the panel whose whole job is to say what is wrong, and
            // "I could not find out" is a different answer from "nothing is wrong".
            CheckFailed =>
                "Windows Firewall could not be inspected on this machine, so this panel cannot say "
                + "whether it is in the way. If the phone cannot connect, use the Tailscale code.",

            // Silence, and this is the one case that earns it. There is nothing about the firewall to
            // say, and the panel already has a better message for a machine with no addresses on it.
            // Reported as NotCovered it sent the user to change a network category on a machine that is
            // not on a network — a false diagnosis, pointing at settings with nothing to change.
            NoNetwork => "",

            // Named separately because repairing cannot fix it and would look like it had: the rule is
            // created, Windows reports success, and goes on ignoring it.
            PolicyIgnoresLocalRules =>
                "This machine's firewall is managed by group policy, which is set to ignore locally "
                + "created rules — so a rule for mTiles will not be used even once it exists. Ask "
                + "whoever manages the machine to allow it, or use the Tailscale code instead.",

            _ => "",
        };
    }

    /// <summary>Runs the read-only half of the script, unelevated. Any failure to ask is "no answer".</summary>
    private static async Task<int> RunCheckAsync(string program)
    {
        try
        {
            var shell = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe");

            var startInfo = new ProcessStartInfo(shell)
            {
                Arguments = $"-NoProfile -NonInteractive -EncodedCommand {Encode(BuildCheckScript(program))}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var process = Process.Start(startInfo);

            // CheckFailed, not 0. Zero means "asked, and nothing is wrong" — and the panel says nothing
            // for it, which is indistinguishable from silence in exactly the case this exists for. The
            // script already has a code for "could not ask"; the host had been answering with the code
            // for "all clear".
            if (process is null) return CheckFailed;

            using var patience = new CancellationTokenSource(CheckTimeout);

            try
            {
                // Drained before the wait, as everywhere else here. Redirecting a stream and never
                // reading it is a deadlock waiting for enough output to fill the pipe: PowerShell
                // writing a single error record about an unavailable cmdlet would have hung this until
                // the timeout, and the timeout's answer is "no diagnosis" — twenty seconds spent to
                // learn nothing, in the one place whose whole job is to say what is wrong.
                var stdout = process.StandardOutput.ReadToEndAsync(patience.Token);
                var stderr = process.StandardError.ReadToEndAsync(patience.Token);
                await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
                await process.WaitForExitAsync(patience.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return CheckFailed;
            }

            return process.ExitCode;
        }
        catch (Exception ex)
        {
            // A check that cannot run says so. It used to return 0 — "asked, all clear" — so a missing
            // PowerShell, a policy blocking the script or a machine under load produced a confidently
            // silent panel, which is the failure mode this whole class was written against.
            Trace.TraceWarning("Firewall check failed: {0}", ex);
            return CheckFailed;
        }
    }

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
                    "The rule is in place: Windows Firewall allows mTiles on private and domain "
                    + "networks. If the phone still cannot connect, something other than Windows "
                    + "Firewall is blocking it."),

                // The rule exists but applies to nothing: every active network is classified Public, and
                // the rule does not cover that profile deliberately. Reporting success would send the
                // user hunting for a different fault entirely.
                NotCovered => new FirewallResult(FirewallOutcome.Failed,
                    "The rule was added, but it does not cover the network your phone would be on — "
                    + "Windows most likely classifies it as Public, which this rule deliberately leaves "
                    + "alone. Set that network to Private in Windows settings, or use the Tailscale "
                    + "code instead."),

                // Created, reported as created, and ignored. Without this the user is told the firewall
                // was fixed and left with a phone that still cannot connect — the exact outcome the
                // whole of this class is written to avoid.
                PolicyIgnoresLocalRules => new FirewallResult(FirewallOutcome.Failed,
                    "The rule was added, but this machine's firewall is managed by group policy, which "
                    + "is set to ignore locally created rules — so Windows will not use it. Ask whoever "
                    + "manages the machine, or use the Tailscale code instead."),

                // Both of these mean the repair did not take, and both became reachable here when the
                // verification became shared: the script now ends by asking the same four questions the
                // panel's check asks, so it can report that the rule it has just created is not there,
                // or that a block rule for this executable survived the removal. Neither is expected —
                // and "exit code 3" is not something to hand a user in place of a sentence.
                NoRule => new FirewallResult(FirewallOutcome.Failed,
                    "The rule was not there afterwards. Something on this machine is removing it — a "
                    + "security product, or a policy. Use the Tailscale code instead."),

                Blocked => new FirewallResult(FirewallOutcome.Failed,
                    "A rule blocking mTiles is still in place after the repair, so something other than "
                    + "this application is putting it back. Use the Tailscale code instead."),

                CheckFailed => new FirewallResult(FirewallOutcome.Failed,
                    "The rule was added, but Windows Firewall could not be inspected afterwards, so "
                    + "whether it took effect is unknown."),

                // Not a failure of the rule. Said rather than passed off as success, because "it is in
                // place" would be claiming something about a network nobody can see yet.
                NoNetwork => new FirewallResult(FirewallOutcome.Allowed,
                    "The rule is in place. This machine is not on a network at the moment, so whether "
                    + "it covers the one your phone will be on cannot be told until it is."),

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
          New-NetFirewallRule -DisplayName '{{RuleName}}' -Direction Inbound -Action Allow -Program $program -Protocol TCP -Profile Domain,Private -ErrorAction Stop | Out-Null
          {{Verification}}
          """;

    /// <summary>
    /// The read-only half: exactly the verification the repair ends with, run on its own.
    /// </summary>
    /// <remarks>
    /// Needs no elevation — every cmdlet here reads — so it can run when the panel opens rather than
    /// only after somebody has been through a UAC prompt.
    /// </remarks>
    internal static string BuildCheckScript(string program) =>
        $$"""
          $ErrorActionPreference = 'Continue'
          $program = '{{Escape(program)}}'
          {{Verification}}
          """;

    /// <summary>
    /// Whether this machine is actually reachable, most decisive question first.
    /// </summary>
    /// <remarks>
    /// <para>One string used by both scripts. Written twice, the repair's verdict and the panel's hint
    /// would disagree the first time either was edited — and the two are read minutes apart by the
    /// same person looking at the same panel.</para>
    /// <para><b>Rules are found by program, never by name.</b> Looking the allow rule up by its
    /// <c>DisplayName</c> asked whether <em>this application</em> had created one, when the question is
    /// whether anything lets mTiles in — and Windows' own "Allow access" prompt writes rules named
    /// after the program, not after us. A machine where the user had answered that prompt correctly was
    /// told "no rule allowing mTiles in" and offered a repair that deletes every inbound rule for the
    /// executable and replaces it. Destructive advice, on a configuration that already worked.</para>
    /// <para><b>Only networks that can carry a phone are asked about.</b> "Is any active profile
    /// Private?" is answered yes by a Tailscale adapter, a VPN, or a Hyper-V switch, so a machine whose
    /// real Wi-Fi is Public was reported as fine — silence in exactly the case the check exists for.
    /// The filter is the default route, the same test <c>NetworkEndpointSource</c> uses to decide which
    /// addresses are worth putting in a QR code: an adapter nothing routes through cannot carry a phone.
    /// It is a filter rather than a requirement — if nothing has a default route, every profile is
    /// considered, because reporting nothing would be worse than considering too much.</para>
    /// <para><b>The rule's own profiles are compared against the network's.</b> A rule that exists is not
    /// a rule that applies: <c>Domain,Private</c> does nothing on a Public network, which is what exit
    /// <see cref="NotCovered"/> now means — "the network your phone is on is not one this rule
    /// covers", rather than the narrower "everything here is Public".</para>
    /// <para><c>$granted</c> is the union of the profiles of <b>every</b> enabled inbound allow rule for
    /// this executable, not of ours alone — which is the right question ("is mTiles allowed in on this
    /// network?") and rests on the same condition as the rule itself: while this program has one
    /// purpose-built inbound listener, any rule letting it in is letting the bridge in. If a second
    /// listener is ever added, a rule that exists for that one would answer this question on the
    /// bridge's behalf, and both this and the rule's scope have to be reconsidered together.</para>
    /// <para><b>A check that could not run says so</b> (<see cref="CheckFailed"/>). Without it a machine
    /// whose NetSecurity cmdlets fail — no module, a broken WMI repository — reported "no rule", and
    /// the offered repair could not have worked either.</para>
    /// <para><c>DomainAuthenticated</c> is what <c>Get-NetConnectionProfile</c> calls a domain network,
    /// while <c>Get-NetFirewallProfile</c> and a rule's own <c>Profile</c> call it <c>Domain</c>; the
    /// mapping is why the names are translated rather than passed through.</para>
    /// <para>The policy question is asked last but is not the least: <c>AllowLocalFirewallRules</c> set
    /// to False by group policy means a rule can be created, reported as created, and ignored. It is a
    /// tri-state (<c>NotConfigured</c> is neither true nor false), so it is compared against False
    /// rather than tested for truth.</para>
    /// </remarks>
    private static readonly string Verification =
        $$"""
          try {
            $inbound = @(Get-NetFirewallApplicationFilter -Program $program -ErrorAction SilentlyContinue | Get-NetFirewallRule -ErrorAction Stop | Where-Object { $_.Direction -eq 'Inbound' -and $_.Enabled -eq 'True' })
            if (@($inbound | Where-Object { $_.Action -eq 'Block' }).Count -gt 0) { exit {{Blocked}} }

            $allowing = @($inbound | Where-Object { $_.Action -eq 'Allow' })
            if ($allowing.Count -eq 0) { exit {{NoRule}} }

            $routed = @((Get-NetRoute -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue).InterfaceIndex)
            $active = @(Get-NetConnectionProfile -ErrorAction Stop)
            $relevant = @($active | Where-Object { $routed -contains $_.InterfaceIndex })
            if ($relevant.Count -eq 0) { $relevant = $active }

            $needed = @($relevant | ForEach-Object { if ($_.NetworkCategory -eq 'DomainAuthenticated') { 'Domain' } else { [string]$_.NetworkCategory } } | Select-Object -Unique)
            if ($needed.Count -eq 0) { exit {{NoNetwork}} }

            $granted = @($allowing | ForEach-Object { [string]$_.Profile -split ',' } | ForEach-Object { $_.Trim() } | Select-Object -Unique)
            $covered = @($needed | Where-Object { $granted -contains $_ -or $granted -contains 'Any' })
            if ($covered.Count -eq 0) { exit {{NotCovered}} }

            $ignored = @(Get-NetFirewallProfile -Name $covered -ErrorAction SilentlyContinue | Where-Object { $_.AllowLocalFirewallRules -eq $false })
            if ($ignored.Count -gt 0) { exit {{PolicyIgnoresLocalRules}} }

            exit 0
          } catch {
            exit {{CheckFailed}}
          }
          """;

    /// <summary>Doubles single quotes — the only escape a PowerShell single-quoted string has.</summary>
    private static string Escape(string value) => value.Replace("'", "''");

    private static string Encode(string script) =>
        Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
}

/// <summary>
/// Hands over the command rather than running it — but works out <em>which</em> command first.
/// </summary>
/// <remarks>
/// <para><b>Nothing here changes anything.</b> There is no desktop-wide consented-elevation prompt to
/// invoke, and a GUI application that shells out to <c>sudo</c> either finds no terminal to prompt in or
/// teaches the user to grant root to a program that asked politely. Printing the command respects that
/// the person running Linux can decide for themselves.</para>
/// <para>What it does do is stop guessing. Offering <c>ufw</c> <em>and</em> <c>firewall-cmd</c> side by
/// side asks the user to work out which firewall their own machine runs — and on the distributions
/// where this actually bites, they differ: an Arch desktop set up with a configured <c>ufw</c> denying
/// inbound, against one whose installer enabled <c>firewalld</c>. <c>systemctl is-active</c> answers that
/// for an ordinary user, in milliseconds, and reads nothing but service state.</para>
/// </remarks>
internal sealed class ManualFirewallGuide : IFirewallGuide
{
    /// <summary>Service state is a cheap read. If it is not back by now, something is wrong with the
    /// machine and the panel is not the place to say so.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    public FirewallAdvice GetAdvice(int port) => new(
        CanRepair: false,
        Explanation: "A local firewall may be blocking the connection.",
        ManualCommand: $"{UfwCommand(port)}   # or: {FirewalldCommand(port)}");

    public async Task<string> DiagnoseAsync(int port) =>
        Sentence(await ActiveFirewallAsync().ConfigureAwait(false), port);

    public Task<FirewallResult> TryAllowAsync(int port) => Task.FromResult(
        new FirewallResult(FirewallOutcome.Manual, "Run the command shown to open the port."));

    /// <summary>
    /// What to say about a machine running this firewall, or nothing when none of them is running.
    /// </summary>
    /// <remarks>
    /// Pure, and separate from the probe, because this is the part with an opinion in it: which command,
    /// spelled how. Silence when nothing is running is deliberate — saying "no firewall is blocking
    /// this" would be a claim about nftables rules nobody here has looked at, in a panel whose other
    /// sentences are all things that were actually read.
    /// </remarks>
    internal static string Sentence(string? activeFirewall, int port) => activeFirewall switch
    {
        "firewalld" =>
            $"firewalld is running on this machine, so port {port} is probably closed to your phone. "
            + $"To open it: {FirewalldCommand(port)}",

        "ufw" =>
            $"ufw is running on this machine, so port {port} is probably closed to your phone. "
            + $"To open it: {UfwCommand(port)}",

        _ => "",
    };

    /// <summary>Permanent, and reloaded. <c>--add-port</c> alone lasts until the next reload or reboot,
    /// which is a phone that works today and not on Monday — the worst kind of instruction to have
    /// followed.</summary>
    private static string FirewalldCommand(int port) =>
        $"sudo firewall-cmd --permanent --add-port={port}/tcp && sudo firewall-cmd --reload";

    private static string UfwCommand(int port) => $"sudo ufw allow {port}/tcp";

    /// <summary>
    /// Which of the two is running, asked of systemd and nobody else.
    /// </summary>
    /// <remarks>
    /// <c>systemctl is-active</c> needs no privileges and changes nothing. Any failure at all — no
    /// systemd, no systemctl on PATH, a machine that will not answer — is "do not know", which is the
    /// same as "say nothing": this is a hint beside a working panel.
    /// </remarks>
    private static async Task<string?> ActiveFirewallAsync()
    {
        if (!OperatingSystem.IsLinux()) return null;

        foreach (var service in (string[])["firewalld", "ufw"])
            if (await IsActiveAsync(service).ConfigureAwait(false))
                return service;

        return null;
    }

    private static async Task<bool> IsActiveAsync(string service)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("systemctl")
            {
                ArgumentList = { "is-active", service },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (process is null) return false;

            using var patience = new CancellationTokenSource(ProbeTimeout);

            try
            {
                // Drained before the wait, as everywhere else here.
                var stdout = process.StandardOutput.ReadToEndAsync(patience.Token);
                var stderr = process.StandardError.ReadToEndAsync(patience.Token);
                await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
                await process.WaitForExitAsync(patience.Token).ConfigureAwait(false);

                // The word, not the exit code: `is-active` exits non-zero for "inactive" and for
                // "unknown" alike, and prints which. A masked or absent unit is not a firewall in the
                // way.
                return (await stdout).Trim() == "active";
            }
            catch (OperationCanceledException)
            {
                // Killed, not abandoned. Walking away from a systemctl that will not answer leaves it
                // running for the life of the application, and the panel can be opened again and again.
                try { process.Kill(entireProcessTree: true); } catch { }
                return false;
            }

        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Could not ask systemd about {service}: {ex.Message}");
            return false;
        }
    }
}

internal static class FirewallGuide
{
    public static IFirewallGuide ForThisMachine() =>
        OperatingSystem.IsWindows() ? new WindowsFirewallGuide() : new ManualFirewallGuide();
}
