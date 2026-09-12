using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace mTiles.Services.Speech;

/// <summary>
/// Who, if anyone, this desktop has already given a shortcut to.
/// </summary>
/// <remarks>
/// <para><b>Why it can be asked at all, and why it has to be.</b> The dictation shortcut is a tunnelling
/// handler on our own window (<see cref="DictationHotkeys"/>), so a shortcut the compositor has taken is
/// not a shortcut we lose a race for — it never arrives. Nothing is on screen, nothing is in the log,
/// and the user is left holding two keys at an application that was never told. Measured 2026-09-12 on
/// Plasma 6: <c>Alt+Space</c>, which ships as this application's default, is KRunner's, and it is
/// <em>not</em> in <c>kglobalshortcutsrc</c> — it is a compiled-in default, so reading the desktop's
/// configuration files would have found nothing and reported the key free.</para>
/// <para><b>One answer, and it is only ever "somebody has this".</b> There is no "free" here: a desktop
/// that cannot be asked, one nobody has written a reader for, and a genuinely unclaimed shortcut all
/// come back null, and null adds no sentence to the page. That collapse is safe precisely because
/// nothing here ever asserts the good case — the moment something wanted to print "this shortcut is
/// available", the three would have to be told apart again.</para>
/// <para><b>Not a substitute for the empirical answer.</b> <c>SpeechSetupFlow.ShortcutHintDelay</c> is
/// still the universal one: it notices that the keys were held and nothing arrived, on every desktop,
/// including the ones below and the grabs nobody exposes at all (an input-method switcher takes
/// <c>Ctrl+Space</c> and appears in none of these registries). This is the half that can name a
/// culprit.</para>
/// <para>Deliberately uncached. It runs when a shortcut is typed, when the Speech page loads and when
/// the wizard reaches its last step — never in a loop — and a remembered answer would go on warning
/// about a shortcut the user had just freed in their desktop settings, which is the one thing they
/// would do in response to reading it.</para>
/// </remarks>
internal static class DesktopShortcuts
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    /// <summary>Whatever already holds <paramref name="gesture"/>, or null.</summary>
    public static Task<ShortcutOwner?> OwnerAsync(HotkeyGesture gesture)
    {
        // Windows publishes no register to ask, so what it has taken is written down instead — and read
        // here rather than on a thread, because a table lookup is not I/O. See WindowsShortcuts.
        if (OperatingSystem.IsWindows()) return Task.FromResult(WindowsShortcuts.Owner(gesture));

        // macOS has the same shortcuts-of-its-own problem and no reader here yet. Answering null is the
        // honest shape of that, and the wizard's own timeout still covers it.
        if (!OperatingSystem.IsLinux()) return Task.FromResult<ShortcutOwner?>(null);

        // Process.WaitForExit blocks, and every caller is on the UI thread.
        return Task.Run(() => Owner(gesture));
    }

    private static ShortcutOwner? Owner(HotkeyGesture gesture)
    {
        try
        {
            var desktop = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "";

            if (desktop.Contains("KDE", StringComparison.OrdinalIgnoreCase))
                return FromKde(gesture);

            if (desktop.Contains("GNOME", StringComparison.OrdinalIgnoreCase))
                return FromGnome(gesture);

            if (desktop.Contains("Hyprland", StringComparison.OrdinalIgnoreCase)
                || Environment.GetEnvironmentVariable("HYPRLAND_INSTANCE_SIGNATURE") is { Length: > 0 })
                return FromHyprland(gesture);

            return null;
        }
        catch (Exception ex)
        {
            // A desktop that will not answer is a desktop with nothing to say. It must never be the
            // reason a settings page throws.
            Trace.TraceInformation("Asking the desktop about a shortcut failed: {0}", ex.Message);
            return null;
        }
    }

    // ─────────────────────────── KDE ───────────────────────────

    /// <summary>Quoted strings in what <c>gdbus</c> prints, which is a GVariant rather than JSON.</summary>
    private static readonly Regex GVariantStrings = new(@"'((?:[^'\\]|\\.)*)'", RegexOptions.Compiled);

    /// <summary>
    /// KGlobalAccel's own register, over its own D-Bus API.
    /// </summary>
    /// <remarks>
    /// <para>Through <c>gdbus</c> rather than hand-written D-Bus, which is the choice
    /// <see cref="DesktopTextScale"/> already made and for the same reason: the alternative is a protocol
    /// implementation that cannot be exercised from a Windows development machine, bought for one call.
    /// </para>
    /// <para>The answer is a tuple per action — unique name, friendly name, component unique name,
    /// component friendly name, then contexts and keys. The <b>fourth</b> string is the one worth
    /// printing: measured, <c>Alt+Space</c> answers <c>('_launch', 'KRunner', 'org.kde.krunner.desktop',
    /// 'KRunner', …)</c>, and it is the component that a user goes looking for in their system settings.
    /// An unclaimed key answers <c>(@a(ssssssaiai) [],)</c> — no strings at all, which is why counting
    /// them is enough to tell the two apart.</para>
    /// </remarks>
    private static ShortcutOwner? FromKde(HotkeyGesture gesture)
    {
        if (ShortcutSpelling.QtKeyCode(gesture) is not { } code) return null;

        var output = Run("gdbus",
        [
            "call", "--session",
            "--dest", "org.kde.kglobalaccel",
            "--object-path", "/kglobalaccel",
            "--method", "org.kde.KGlobalAccel.getGlobalShortcutsByKey",
            code.ToString(),
        ]);
        if (output is null) return null;

        var strings = GVariantStrings.Matches(output).Select(m => m.Groups[1].Value).ToArray();
        if (strings.Length == 0) return null;

        // The component's friendly name, falling back to the action's own when the shape is not the one
        // measured: something holds the key either way, and saying so with a worse name beats silence.
        var name = strings.Length > 3 ? strings[3] : strings.Length > 1 ? strings[1] : strings[0];
        return string.IsNullOrWhiteSpace(name) ? null : new ShortcutOwner(name, CanBeFreed: true);
    }

    // ─────────────────────────── GNOME ───────────────────────────

    /// <summary>The schemas a shortcut a user could collide with is kept in.</summary>
    /// <remarks>Not the custom keybindings, which are a list of relocatable schema paths rather than
    /// keys and would need a second round of lookups; what they cost by being missing is one unnamed
    /// culprit, and what they would cost by being guessed at is a wrong one.</remarks>
    private static readonly string[] GnomeSchemas =
    [
        "org.gnome.desktop.wm.keybindings",
        "org.gnome.shell.keybindings",
        "org.gnome.mutter.keybindings",
        "org.gnome.mutter.wayland.keybindings",
        "org.gnome.settings-daemon.plugins.media-keys",
    ];

    /// <summary>
    /// GNOME keeps its shortcuts in GSettings, so they can be listed and read.
    /// </summary>
    /// <remarks>A line is <c>&lt;schema&gt; &lt;key&gt; ['&lt;Alt&gt;space']</c>, and the value may hold
    /// several accelerators or none. Read on this machine, which has the schema installed without
    /// running GNOME: <c>org.gnome.desktop.wm.keybindings activate-window-menu ['&lt;Alt&gt;space']</c> —
    /// the same shortcut Windows takes for the same purpose, and the reason this default is worth
    /// questioning on more desktops than the one that raised it.</remarks>
    private static ShortcutOwner? FromGnome(HotkeyGesture gesture)
    {
        foreach (var schema in GnomeSchemas)
        {
            var output = Run("gsettings", ["list-recursively", schema]);
            if (output is null) continue;

            foreach (var line in output.Split('\n'))
            {
                var parts = line.Split(' ', 3);
                if (parts.Length < 3) continue;

                foreach (Match match in GVariantStrings.Matches(parts[2]))
                {
                    if (ShortcutSpelling.MatchesGnomeAccelerator(gesture, match.Groups[1].Value))
                        return new ShortcutOwner($"The desktop's \"{parts[1]}\"", CanBeFreed: true);
                }
            }
        }

        return null;
    }

    // ─────────────────────────── Hyprland ───────────────────────────

    /// <summary>
    /// Hyprland answers about its own binds, and they are all in the user's config.
    /// </summary>
    /// <remarks><b>Unmeasured</b> — written from Hyprland's documentation rather than against a running
    /// compositor, like the mask in <see cref="ShortcutSpelling.MatchesHyprlandBind"/>. Everything about
    /// it fails soft: a shape that is not what is expected is an exception caught above, and the user is
    /// left with the wizard's timeout, which is where they were before.</remarks>
    private static ShortcutOwner? FromHyprland(HotkeyGesture gesture)
    {
        var output = Run("hyprctl", ["binds", "-j"]);
        if (output is null) return null;

        using var document = JsonDocument.Parse(output);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return null;

        foreach (var bind in document.RootElement.EnumerateArray())
        {
            if (!bind.TryGetProperty("modmask", out var mask) || mask.ValueKind != JsonValueKind.Number)
                continue;
            if (!bind.TryGetProperty("key", out var key) || key.ValueKind != JsonValueKind.String)
                continue;

            if (!ShortcutSpelling.MatchesHyprlandBind(gesture, mask.GetInt32(), key.GetString() ?? ""))
                continue;

            var dispatcher = bind.TryGetProperty("dispatcher", out var d) ? d.GetString() : null;
            return new ShortcutOwner(
                string.IsNullOrWhiteSpace(dispatcher)
                    ? "A Hyprland binding"
                    : $"A Hyprland binding ({dispatcher})",
                CanBeFreed: true);
        }

        return null;
    }

    // ─────────────────────────── Running the thing ───────────────────────────

    /// <summary>Standard output, or null for anything that did not work.</summary>
    /// <remarks>
    /// <para>Bounded, and the timeout is the point rather than tidiness: this is called while a settings
    /// page is waiting to say something, and a desktop service that has wedged must cost two seconds and
    /// a missing sentence rather than a spinner nobody can cancel.</para>
    /// <para><b>Both pipes are read asynchronously, and that is what makes the bound real.</b> A
    /// <c>ReadToEnd</c> before the wait blocks until the child closes its output, so the deadline was
    /// only ever checked after the very thing it was written for — a <c>gdbus</c> wedged on a D-Bus reply
    /// with its stdout still open — had already parked the thread for good. Standard error is redirected
    /// and therefore has to be drained too: a child that fills its 64 KB error buffer deadlocks in
    /// exactly the same way, waiting for a reader that never comes.</para>
    /// </remarks>
    private static string? Run(string executable, string[] arguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        using var process = Process.Start(start);
        if (process is null) return null;

        using var deadline = new CancellationTokenSource(Timeout);
        var streams = Task.WhenAll(
            process.StandardOutput.ReadToEndAsync(deadline.Token),
            process.StandardError.ReadToEndAsync(deadline.Token));

        try
        {
            process.WaitForExitAsync(deadline.Token).GetAwaiter().GetResult();
            var output = streams.GetAwaiter().GetResult()[0];
            return process.ExitCode == 0 ? output : null;
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            return null;
        }
    }
}
