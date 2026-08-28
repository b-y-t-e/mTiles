using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using mTiles.Models;
using mTiles.Services.Speech;

namespace mTiles.Services;

public sealed class SettingsService
{
    private readonly string _filePath;
    private Timer? _debounceTimer;

    public AppSettings Settings { get; private set; } = new();

    public event Action? SettingsChanged;

    public SettingsService() : this(null) { }

    /// <param name="settingsFilePath">Where the settings live. Defaults to the user's own file; a test
    /// passes a temporary one, because this constructor both reads <em>and writes</em> (seeding the
    /// default profiles saves) and no test may edit the settings of whoever is running it. Internal for
    /// that reason: it exists for the test assembly, and the application has no business choosing.</param>
    internal SettingsService(string? settingsFilePath)
    {
        _filePath = settingsFilePath ?? AppPaths.GetSettingsFilePath();

        // A bare file name has no directory part, and `GetDirectoryName` answers that with an empty
        // string rather than null — which `CreateDirectory` rejects. So the check is for empty, and it
        // is a real case rather than a defensive one: "settings.json" is a legal argument.
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        // What Load actually managed, not what the directory listing suggested. A file that is there but
        // unreadable — truncated by a full disk, hand-edited into invalid JSON, written by a version that
        // wrote something else — leaves this object holding defaults, which is a fresh installation in
        // every respect except that the first-run steps were skipped. The user then starts with the
        // language set to "auto" and no explanation, because a check on File.Exists said "not new".
        if (!Load())
            SeedSpeechLanguage();

        MigrateLegacySettings();
        SeedDefaultProfiles();
    }

    /// <summary>
    /// Starts dictation off in the language this machine is set up in.
    /// </summary>
    /// <remarks>
    /// <para>Only on a genuinely new settings file: it is a first guess, not a preference to keep
    /// re-applying over somebody who chose <c>auto</c> on purpose.</para>
    /// <para>It matters for the whisper models, which are told which language to expect and do measurably
    /// better for it — their automatic detection works on the first seconds of audio, and a dictated
    /// sentence is often shorter than that. Parakeet ignores the setting entirely (it works the language
    /// out across all 25 it knows), so for the default model this is inert, which is also why it is safe:
    /// the wrong guess costs nothing until somebody switches to whisper, and Settings → Speech shows it.
    /// </para>
    /// </remarks>
    private void SeedSpeechLanguage()
    {
        var starting = StartingLanguage(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);
        if (starting == Settings.Speech.Language)
            return;

        Settings.Speech.Language = starting;
        Save();
    }

    /// <summary>
    /// The language a fresh installation starts in, given the one the machine is set up in.
    /// </summary>
    /// <remarks>
    /// A function of its argument rather than of the machine, so the rule can be read in a table test:
    /// asking <see cref="CultureInfo.CurrentUICulture"/> inside made the only possible test recompute
    /// the implementation and assert it against itself, on whatever culture the build agent happened to
    /// have.
    /// <para>Anything not on the offered list falls back to <c>auto</c>: the list is what Settings shows,
    /// and seeding a code the user cannot see or change would be a setting they could only undo by
    /// editing the file.</para>
    /// </remarks>
    internal static string StartingLanguage(string systemLanguage) =>
        SpeechModelCatalog.Languages.Any(l => l.Code == systemLanguage) ? systemLanguage : "auto";

    /// <summary>
    /// Carries answers forward from settings written by an older version.
    /// <para>Renaming a property means the old value is simply not read, and the new one starts at its
    /// default. That is harmless for a font size. It is not harmless for
    /// <see cref="AppSettings.GitIgnoreWorkspaceDir"/>, whose default writes to the user's repository:
    /// somebody who turned the old switch off had said no, and an update is not the moment to stop
    /// hearing it.</para>
    /// </summary>
    private void MigrateLegacySettings()
    {
        var changed = false;

        // Oldest first, so the newer answer wins where a file carries both. A settings file written
        // before the application was renamed can hold either name, or both, and the two are the same
        // question asked twice rather than two settings.
        if (Settings.LegacyGitHideMTerminalDir is { } hidden)
        {
            Settings.GitIgnoreWorkspaceDir = hidden;
            Settings.LegacyGitHideMTerminalDir = null;   // read once; the next save drops it
            changed = true;
        }

        if (Settings.LegacyGitIgnoreMTerminalDir is { } ignored)
        {
            Settings.GitIgnoreWorkspaceDir = ignored;
            Settings.LegacyGitIgnoreMTerminalDir = null;
            changed = true;
        }

        // The dictation shortcut lost its separate on/off switch — an empty shortcut is what "off" means
        // now. Somebody who had switched it off would otherwise find Alt+Space swallowed again after an
        // update, which is the one answer they had already given.
        if (Settings.Speech.LegacyHotkeyEnabled is false)
        {
            Settings.Speech.Hotkey = "";
            Settings.Speech.LegacyHotkeyEnabled = null;
            changed = true;
        }
        else if (Settings.Speech.LegacyHotkeyEnabled is true)
        {
            Settings.Speech.LegacyHotkeyEnabled = null;   // it was on, which is now simply "a shortcut is set"
            changed = true;
        }

        changed |= MigrateOpenCodeProfile();
        changed |= RemoveSeededOpenClaudeProfile();

        if (changed)
            Save();
    }

    /// <returns>
    /// True when settings were actually read from the file. False means this object holds defaults —
    /// because there was no file, or because there was one and nothing usable came out of it. The
    /// difference matters to the caller: a first run seeds things, and "unreadable" is a first run as
    /// far as the settings in memory are concerned.
    /// </returns>
    public bool Load()
    {
        if (!File.Exists(_filePath)) return false;

        var loaded = false;
        try
        {
            var json = File.ReadAllText(_filePath);
            var parsed = JsonSerializer.Deserialize<AppSettings>(json, JsonDefaults.SettingsOptions);
            if (parsed is not null)
            {
                Settings = parsed;
                loaded = true;
            }
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("The settings file could not be read; starting from defaults: {0}", ex.Message);
            Settings = new AppSettings();
        }

        // Nulls need no normalising here: every section and collection refuses one in its own setter, and
        // every *string* in the tree is turned into an empty one by the converter in
        // `JsonDefaults.SettingsOptions`. Patching after loading only ever covered the level somebody
        // remembered — a null one property deeper, `"Speech": { "CustomWords": null }`, walked straight
        // past it and stopped the application from starting just the same.
        if (!loaded)
            PreserveUnreadable();

        return loaded;
    }

    /// <summary>
    /// Puts a settings file that could not be read out of the way of what is about to overwrite it.
    /// </summary>
    /// <remarks>
    /// <para>Failing to read is not the end of it: the first-run steps below save, so within milliseconds
    /// the file is replaced by defaults and whatever was in it is gone. That file holds every profile the
    /// user wrote, their AI tool paths, their manual database connections and the passwords for them —
    /// and "could not be read" is very often "could not be read <em>by this version</em>", or a truncation
    /// with the first ninety per cent of the content still sitting there. Losing that silently, to repair
    /// a fault the user has not even been told about, is the worst of the available outcomes.</para>
    /// <para>Best effort by design: a copy that fails must not stop the application starting, which is
    /// the whole point of treating an unreadable file as a first run. Old copies are pruned because this
    /// runs on every start until the file is readable again — a disk too full to save is also a disk that
    /// would otherwise collect one of these a minute.</para>
    /// </remarks>
    private void PreserveUnreadable()
    {
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            var stem = Path.GetFileNameWithoutExtension(_filePath);
            var pattern = $"{stem}.bad-*{Path.GetExtension(_filePath)}";
            var copy = Path.Combine(directory ?? "",
                $"{stem}.bad-{DateTime.Now:yyyyMMdd-HHmmss}{Path.GetExtension(_filePath)}");

            File.Copy(_filePath, copy, overwrite: true);
            Trace.TraceWarning("The unreadable settings file was kept as '{0}'.", copy);

            // Newest kept, oldest dropped — the interesting one is the first failure, but a name sorts by
            // time and the user is far likelier to look at the most recent. Five is enough to cover a
            // handful of restarts while somebody works out what happened.
            foreach (var stale in Directory.GetFiles(directory is { Length: > 0 } d ? d : ".", pattern)
                         .OrderDescending(StringComparer.Ordinal)
                         .Skip(BadCopiesKept))
            {
                try { File.Delete(stale); } catch { /* it will be tried again next time */ }
            }
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("The unreadable settings file could not be kept: {0}", ex.Message);
        }
    }

    private const int BadCopiesKept = 5;

    /// <summary>
    /// The OpenCode profile's two commands: resume the tile's session, and — when there is none yet —
    /// create it and resume that. See <see cref="OpenCodeSession"/> for why creating one takes a file.
    /// <para>Named constants because <see cref="MigrateOpenCodeProfile"/> has to recognise its own
    /// earlier answer, and comparing against a literal it had copied is how a migration comes to
    /// overwrite something the user wrote.</para>
    /// </summary>
    /// <remarks>Composed through <see cref="OpenCodeSession.IdFor"/> rather than written out, so
    /// opencode's <c>ses</c> prefix lives in exactly one place. Spelling it here as well was a second
    /// copy hiding behind a comment that claimed there was only one — and the two disagreeing is not a
    /// build failure, it is a tile that silently starts a fresh conversation.</remarks>
    private static readonly string OpenCodeResume =
        $"opencode --session {OpenCodeSession.IdFor(TileScript.TileIdToken)}";

    /// <inheritdoc cref="OpenCodeResume"/>
    /// <remarks><c>;</c> rather than <c>&amp;&amp;</c>, so a failed import still gives the resume its
    /// turn: the session may well exist already and the import be the thing that is broken. It is a
    /// separator in PowerShell and every POSIX shell, and <em>not</em> in <c>cmd</c> — which is the
    /// documented limit of running chains there, not a new one.</remarks>
    private static readonly string OpenCodeCreateThenResume =
        $"opencode import \"{TileScript.OpenCodeSessionFileToken}\" ; {OpenCodeResume}";

    /// <summary>
    /// Gives the seeded OpenCode profile its session resume, which seeding alone cannot do: profiles are
    /// only ever added, never overwritten, so everybody who has run this app before already has an
    /// "OpenCode" profile and would never see the new one.
    /// <para>Only when both scripts are still <em>exactly</em> what an earlier version seeded — the old
    /// pair asked <c>opencode --session ${tileId}</c>, which opencode refuses outright because the id
    /// lacks its <c>ses</c> prefix, and fell back to a bare <c>opencode</c>. A profile the user has
    /// touched at all is left alone; this replaces a command that cannot work, not a decision.</para>
    /// </summary>
    private bool MigrateOpenCodeProfile()
    {
        const string oldStartup = "opencode --session ${tileId}";
        const string oldFallback = "opencode";

        // Every match, not the first. Seeding cannot produce two profiles of one name, but a user
        // duplicating one can — and migrating one of a pair while leaving its twin on a command that has
        // never worked is the kind of half-done that gets reported as "resume works sometimes".
        var stale = Settings.ShellProfiles.Where(p =>
            p.Name.Equals("OpenCode", StringComparison.OrdinalIgnoreCase)
            && p.StartupScript == oldStartup
            && p.FallbackScript == oldFallback).ToList();

        foreach (var profile in stale)
        {
            profile.StartupScript = OpenCodeResume;
            profile.FallbackScript = OpenCodeCreateThenResume;
        }

        return stale.Count > 0;
    }

    /// <summary>
    /// Takes away the seeded "Open Claude" profile, which nothing can run any more.
    /// </summary>
    /// <remarks>
    /// <para>The tool went out of <c>AiToolDetector.KnownTools</c> and the profile went out of
    /// <see cref="SeedDefaultProfiles"/> in the same change — but seeding only ever <em>adds</em>, so
    /// everybody who has run an earlier version still has the profile, and its
    /// <c>RequiredAiToolBinaryName</c> names a tool detection no longer looks for. That combination is
    /// the worst of both: the profile can never be offered on an empty tile again, not even to somebody
    /// who does have <c>openclaude</c> installed, and the only way to be rid of it is to find it in
    /// Settings and delete it by hand. Removing a seeded entry needs a migration exactly as changing
    /// one does — <see cref="MigrateOpenCodeProfile"/> is the precedent.</para>
    /// <para><b>Only when it is untouched</b>, matched on both scripts as well as the name and binary,
    /// for the same reason that migration matches on both: what is being taken away is an answer this
    /// application gave, never one the user wrote. A profile they have edited — pointed at a fork, or a
    /// wrapper of their own on the same binary — is theirs, and stays, dead or not. Every match rather
    /// than the first, because a user can duplicate a profile and leaving one of a pair is the kind of
    /// half-done that gets reported as a profile that comes back.</para>
    /// <para><b>The one cost, stated rather than hidden:</b> the visibility filter only governs the
    /// empty tile's chooser, so a tile already launched on this profile goes on running it. Taking the
    /// profile away means such a tile falls back to its <c>ShellName</c> on the next restart — a plain
    /// shell — which for somebody who genuinely has <c>openclaude</c> installed is a working tile
    /// changed out from under them. It is still the right way round: leaving it costs everybody else a
    /// permanent entry they cannot be offered and can only delete by hand, and the fallback here is a
    /// shell rather than a failure.</para>
    /// </remarks>
    private bool RemoveSeededOpenClaudeProfile()
    {
        const string seededStartup = "openclaude --resume ${tileId}";
        const string seededFallback = "openclaude --session-id ${tileId}";

        var removed = Settings.ShellProfiles.RemoveAll(p =>
            p.Name.Equals("Open Claude", StringComparison.OrdinalIgnoreCase)
            // Static `string.Equals`, because the property is `string?` and has no initialiser. A
            // profile read from a file with the key present is saved by `NullToEmptyStringConverter`;
            // one built in memory, or read from a file written before the key existed, is null — and a
            // dereference here throws inside `MigrateLegacySettings`, which runs while the main window
            // is being built. That failure is the one the settings file's own guards exist to prevent:
            // the application does not start and says nothing about why.
            && string.Equals(p.RequiredAiToolBinaryName, "openclaude", StringComparison.OrdinalIgnoreCase)
            && p.StartupScript == seededStartup
            && p.FallbackScript == seededFallback);

        return removed > 0;
    }

    private void SeedDefaultProfiles()
    {
        var defaults = new List<UserShellProfile>
        {
            new()
            {
                Name = "Claude Code",
                ShellName = "",
                RequiredAiToolBinaryName = "claude",
                StartupScript = "claude --resume ${tileId}",
                FallbackScript = "claude --session-id ${tileId}"
            },
            new()
            {
                Name = "Pi Agent",
                ShellName = "",
                RequiredAiToolBinaryName = "pi",
                StartupScript = "pi --session-id ${tileId}",
                FallbackScript = "pi --session-id ${tileId}"
            },
            new()
            {
                Name = "OpenCode",
                ShellName = "",
                RequiredAiToolBinaryName = "opencode",
                StartupScript = OpenCodeResume,
                FallbackScript = OpenCodeCreateThenResume
            },
            new()
            {
                Name = "Codex",
                ShellName = "",
                RequiredAiToolBinaryName = "codex",
                StartupScript = "codex resume ${tileId}",
                FallbackScript = "codex"
            }
        };

        var dirty = false;
        foreach (var profile in defaults)
        {
            var exists = Settings.ShellProfiles
                .Any(p => p.Name.Equals(profile.Name, StringComparison.OrdinalIgnoreCase));
            if (exists)
                continue;
            Settings.ShellProfiles.Add(profile);
            dirty = true;
        }

        if (dirty) Save();
    }

    /// <summary>
    /// Writes the settings out now.
    /// <para>Serialised against itself, because two writers really do meet: the debounce timer fires on
    /// a thread-pool thread while the window closing calls this directly on the UI thread. Two
    /// <c>WriteAllText</c> on one path is a sharing violation — caught on the timer's side, and
    /// <em>unhandled</em> on the closing side, which is the worst moment to throw.</para>
    /// </summary>
    public void Save()
    {
        var json = JsonSerializer.Serialize(Settings, JsonDefaults.SettingsOptions);
        lock (_writeLock)
            File.WriteAllText(_filePath, json);
    }

    private readonly Lock _writeLock = new();

    /// <summary>
    /// Writes the settings out shortly after the last change, rather than on every keystroke.
    /// <para>The write is wrapped because it happens on a thread-pool thread half a second later, with
    /// nobody left to catch anything: an unhandled exception there <em>terminates the process</em>. And
    /// it can throw for ordinary reasons — a settings directory on a network profile or a removed
    /// drive, or simply one deleted between the edit and the write. Losing a settings save is a
    /// nuisance; losing the application, with every terminal in it, is not.</para>
    /// </summary>
    public void DebouncedSave()
    {
        // Locked because settings change from more than one place — a tile's own timer, the settings
        // dialog, the window closing — and swapping the field unguarded can drop a timer nobody
        // disposes or dispose one another thread is about to use.
        lock (_debounceLock)
        {
            _debounceTimer?.Dispose();
            _debounceTimer = new Timer(_ => TrySave(), null, AppDefaults.SettingsDebounceMs, Timeout.Infinite);
        }
    }

    private readonly Lock _debounceLock = new();

    private void TrySave()
    {
        try
        {
            Save();
        }
        catch (Exception ex)
        {
            // Reported rather than swallowed: settings that silently stop persisting look like settings
            // that do not work, and there is nothing else anywhere to say otherwise.
            Trace.TraceWarning("Saving settings to '{0}' failed: {1}", _filePath, ex);
        }
    }

    public void NotifyChanged()
    {
        SettingsChanged?.Invoke();
        DebouncedSave();
    }

}
