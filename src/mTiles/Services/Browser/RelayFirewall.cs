using System.Diagnostics;
using System.Text;

namespace mTiles.Services.Browser;

/// <summary>What Windows Firewall says about the relay, and the one rule that lets it in.</summary>
/// <remarks>
/// <para><b>Narrower than the phone bridge's rule, on purpose.</b> That one lets this program in on any
/// port from private networks, because a phone can be anywhere on the LAN. This one names the relay's
/// port and accepts nobody but the tailnet (100.64.0.0/10 and Tailscale's IPv6 prefix), on every profile —
/// Windows usually files the Tailscale adapter as Public, and the address filter is what does the
/// restricting, not the profile. So the relay is not reachable from the office or café network the
/// machine sits on even with the rule in place.</para>
/// <para>The phone bridge's repair removes every inbound rule for this executable, so it is told to
/// leave this one alone by name (<see cref="RuleName"/>), and its own check ignores it: a rule open on
/// every profile would otherwise answer the phone's question on its behalf.</para>
/// </remarks>
public static class RelayFirewall
{
    internal const string RuleName = "mTiles browser relay";

    internal const int NoRule = 3;
    internal const int PolicyIgnoresLocalRules = 4;
    internal const int Blocked = 5;
    internal const int CheckFailed = 6;
    internal const int WrongPort = 8;

    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ElevationTimeout = TimeSpan.FromMinutes(2);

    public static bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>Reads the firewall, unelevated. Answers whether it is fine, and a sentence.</summary>
    public static async Task<(bool Ok, string Message)> CheckAsync(int port)
    {
        if (!IsSupported)
            return (true, "Firewall: not checked on this system - allow TCP port " + port + " from 100.64.0.0/10 if other machines cannot connect.");
        if (Environment.ProcessPath is not { Length: > 0 } program)
            return (false, "Firewall: the application's own path is unknown, so it could not be checked.");

        var code = await RunAsync(CheckScript(program, port), elevated: false, CheckTimeout);
        return Describe(code, port, afterRepair: false);
    }

    /// <summary>Adds the rule, behind the administrator prompt. The prompt is the consent.</summary>
    public static async Task<(bool Ok, string Message)> AllowAsync(int port)
    {
        if (!IsSupported)
            return (false, "Only Windows Firewall can be changed from here.");
        if (Environment.ProcessPath is not { Length: > 0 } program)
            return (false, "The application's own path is unknown.");

        try
        {
            var code = await RunAsync(RepairScript(program, port), elevated: true, ElevationTimeout);
            return Describe(code, port, afterRepair: true);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return (false, "The firewall was left unchanged.");
        }
    }

    internal static (bool Ok, string Message) Describe(int code, int port, bool afterRepair) => code switch
    {
        0 => (true, afterRepair
            ? $"Firewall: rule added - port {port} is open to your Tailscale machines only."
            : $"Firewall: port {port} is open to your Tailscale machines only."),
        Blocked => (false, "Firewall: a rule blocks mTiles (Windows writes one when its \"Allow access\" prompt is dismissed). Allow in firewall replaces it."),
        NoRule => (false, afterRepair
            ? "Firewall: the rule was not there afterwards - something on this machine removes it."
            : "Firewall: no rule lets your other machines in yet. Allow in firewall adds one."),
        WrongPort => (false, $"Firewall: the relay rule is for another port. Allow in firewall updates it to {port}."),
        PolicyIgnoresLocalRules => (false, "Firewall: this machine's firewall is managed by group policy and ignores local rules, so the relay cannot be let in from here."),
        CheckFailed => (false, "Firewall: Windows Firewall could not be inspected."),
        -1 => (false, "Firewall: the check did not answer in time."),
        _ => (false, $"Firewall: unexpected answer ({code})."),
    };

    internal static string CheckScript(string program, int port) =>
        $$"""
          $ErrorActionPreference = 'Continue'
          $program = '{{Escape(program)}}'
          {{Verification(port)}}
          """;

    internal static string RepairScript(string program, int port) =>
        $$"""
          $ErrorActionPreference = 'Continue'
          $program = '{{Escape(program)}}'
          Get-NetFirewallApplicationFilter -Program $program -ErrorAction SilentlyContinue | Get-NetFirewallRule -ErrorAction SilentlyContinue | Where-Object { $_.Direction -eq 'Inbound' -and $_.Action -eq 'Block' } | Remove-NetFirewallRule -ErrorAction SilentlyContinue
          Remove-NetFirewallRule -DisplayName '{{RuleName}}' -ErrorAction SilentlyContinue
          New-NetFirewallRule -DisplayName '{{RuleName}}' -Direction Inbound -Action Allow -Program $program -Protocol TCP -LocalPort {{port}} -RemoteAddress 100.64.0.0/10,fd7a:115c:a1e0::/48 -Profile Any -ErrorAction Stop | Out-Null
          {{Verification(port)}}
          """;

    private static string Verification(int port) =>
        $$"""
          try {
            $inbound = @(Get-NetFirewallApplicationFilter -Program $program -ErrorAction SilentlyContinue | Get-NetFirewallRule -ErrorAction Stop | Where-Object { $_.Direction -eq 'Inbound' -and $_.Enabled -eq 'True' })
            if (@($inbound | Where-Object { $_.Action -eq 'Block' }).Count -gt 0) { exit {{Blocked}} }
            $rule = @($inbound | Where-Object { $_.DisplayName -eq '{{RuleName}}' -and $_.Action -eq 'Allow' })
            if ($rule.Count -eq 0) { exit {{NoRule}} }
            $ports = @($rule | Get-NetFirewallPortFilter -ErrorAction Stop | ForEach-Object { [string]$_.LocalPort })
            if (-not ($ports -contains '{{port}}')) { exit {{WrongPort}} }
            $ignored = @(Get-NetFirewallProfile -ErrorAction SilentlyContinue | Where-Object { $_.AllowLocalFirewallRules -eq $false })
            if ($ignored.Count -gt 0) { exit {{PolicyIgnoresLocalRules}} }
            exit 0
          } catch {
            exit {{CheckFailed}}
          }
          """;

    /// <summary>Runs a script, answering its exit code, or -1 when it did not finish in time.</summary>
    private static async Task<int> RunAsync(string script, bool elevated, TimeSpan timeout)
    {
        try
        {
            // The full path: an elevated run resolved through PATH is a way to get somebody else's
            // powershell.exe started as administrator.
            var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe");
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            var startInfo = new ProcessStartInfo(shell)
            {
                Arguments = $"-NoProfile -NonInteractive -EncodedCommand {encoded}",
                UseShellExecute = elevated,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            if (elevated)
                startInfo.Verb = "runas";
            else
                startInfo.RedirectStandardOutput = startInfo.RedirectStandardError = true;

            using var process = Process.Start(startInfo);
            if (process is null) return CheckFailed;

            using var patience = new CancellationTokenSource(timeout);
            try
            {
                if (!elevated)
                    await Task.WhenAll(process.StandardOutput.ReadToEndAsync(patience.Token),
                        process.StandardError.ReadToEndAsync(patience.Token));
                await process.WaitForExitAsync(patience.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return -1;
            }

            return process.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Relay firewall script failed: {0}", ex.Message);
            return CheckFailed;
        }
    }

    private static string Escape(string value) => value.Replace("'", "''");
}
