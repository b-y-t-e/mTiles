using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using mTiles.Models;
using mTiles.Services.Agents;
using mTiles.Services.Shells;
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
        SeedAgentInstances();
    }

    /// <summary>
    /// Gives every agent this build knows the one instance it starts life with.
    /// </summary>
    /// <remarks>
    /// <para>Adds what is missing rather than replacing the list, and matches on
    /// <see cref="AiAgentInstance.AgentId"/>: an agent added by a later version has to arrive on a
    /// settings file that already has the others, and an instance the user renamed or repointed is
    /// theirs. Nothing is ever removed here — an instance naming an agent this build does not have is a
    /// row that finds nothing, and deleting it would let an older build settle the question for a newer
    /// one, which is the trap <c>TolerantAiBehaviourConverter</c> exists to avoid.</para>
    /// <para>Seeded whether or not the agent is installed: an instance is configuration, and a row that
    /// appeared only once somebody had installed something would be a list changing for reasons they
    /// cannot see. Availability decides what can be <em>chosen</em>, not what exists.</para>
    /// </remarks>
    private void SeedAgentInstances()
    {
        var known = Settings.AiAgentInstances
            .Select(instance => instance.AgentId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = AiAgentCatalog.SeedInstances()
            .Where(seed => !known.Contains(seed.AgentId))
            .ToList();
        if (missing.Count == 0) return;

        Settings.AiAgentInstances.AddRange(missing);
        Save();
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

        changed |= DropCustomShell();
        changed |= ReportUnknownDefaultShell();

        if (changed)
            Save();
    }

    /// <summary>
    /// Says out loud that a hand-nominated shell has been dropped, and stops carrying the keys.
    /// </summary>
    /// <remarks>
    /// The only migration here that cannot honour the answer it reads. A shell is a class now — it has
    /// to know how to quote, how to run one command and how to unset a variable — so a path to an
    /// arbitrary binary has nowhere to go, and the user lands on the default shell instead. That is a
    /// setting disappearing, so it is a warning rather than a note: this is where "my terminal opens in
    /// the wrong shell since the update" is answered. The path is quoted back because it is what the
    /// user has to re-nominate, and the arguments with it, since together they are the whole of what was
    /// set.
    /// </remarks>
    private bool DropCustomShell()
    {
        if (string.IsNullOrWhiteSpace(Settings.LegacyCustomShellPath)
            && string.IsNullOrWhiteSpace(Settings.LegacyCustomShellArgs))
            return false;

        Trace.TraceWarning(
            "The custom shell setting has been removed and your terminals now start in '{0}'. It named "
            + "'{1}' {2}, which this version cannot run: a shell is one of the known kinds (PowerShell, "
            + "Git Bash, bash, zsh, fish) because the application has to know how to quote for it and "
            + "how to run a single command in it.",
            ShellTerminalCatalog.ResolveDefault(Settings).Shell.DisplayName,
            Settings.LegacyCustomShellPath,
            string.IsNullOrWhiteSpace(Settings.LegacyCustomShellArgs)
                ? "with no arguments"
                : $"with arguments '{Settings.LegacyCustomShellArgs}'");

        Settings.LegacyCustomShellPath = null;   // read once; the next save drops both keys
        Settings.LegacyCustomShellArgs = null;
        return true;
    }

    /// <summary>
    /// Says out loud, once, that the default shell named in settings is one this version does not know
    /// — and leaves the name where it is.
    /// </summary>
    /// <remarks>
    /// <para>The other half of <see cref="DropCustomShell"/>, and the same loss by a different route. A
    /// shell was not only nominated by path: on Unix the old detection offered whatever <c>$SHELL</c>
    /// pointed at, of any kind at all, so <c>DefaultShellName</c> can name <c>nu</c>, <c>ksh</c> or
    /// <c>dash</c> — and on Windows it can name <c>CMD</c>. <c>ShellTerminalCatalog.Find</c> answers
    /// null to each of those and the call site quietly falls back, which is a setting disappearing
    /// without a word.</para>
    /// <para><b>Reported, not dropped.</b> Unlike the custom shell — a path there is nothing in this
    /// application that could ever run — a *name* this build does not know is also what a shell added
    /// by a newer version looks like from an installation Velopack has rolled back, and what a settings
    /// file carried over from another machine looks like. Clearing it would let the older build decide
    /// for the newer one for good, since going back would no longer find the setting. Saying it once
    /// costs nothing and loses nothing: <see cref="AppSettings.ReportedUnknownShellName"/> is what
    /// keeps the warning from returning on every launch.</para>
    /// </remarks>
    private bool ReportUnknownDefaultShell()
    {
        var named = Settings.DefaultShellName;
        if (string.IsNullOrWhiteSpace(named) || ShellTerminalCatalog.Find(named) is not null)
        {
            // A name that is known again — the newer build is back, or the shell now exists here — must
            // be reported afresh if it is ever unknown once more.
            if (Settings.ReportedUnknownShellName.Length == 0) return false;
            Settings.ReportedUnknownShellName = "";
            return true;
        }

        if (Settings.ReportedUnknownShellName.Equals(named, StringComparison.OrdinalIgnoreCase))
            return false;

        Settings.ReportedUnknownShellName = named;

        Trace.TraceWarning(
            "The default shell setting named '{0}', which this version does not know, so your terminals "
            + "now start in '{1}'. A shell is one of the known kinds (PowerShell, Git Bash, bash, zsh, "
            + "fish) because the application has to know how to quote for it and how to run a single "
            + "command in it.",
            named, ShellTerminalCatalog.ResolveDefault(Settings).Shell.DisplayName);

        return true;
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
            // The copy carries whatever secrets the unreadable file did, so it is narrowed like the
            // original — File.Copy does not carry the mode across on every filesystem.
            PrivateFile.Protect(copy);
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
    /// Writes the settings out now.
    /// <para>Serialised against itself, because two writers really do meet: the debounce timer fires on
    /// a thread-pool thread while the window closing calls this directly on the UI thread. Two
    /// <c>WriteAllText</c> on one path is a sharing violation — caught on the timer's side, and
    /// <em>unhandled</em> on the closing side, which is the worst moment to throw.</para>
    /// </summary>
    public void Save()
    {
        var json = JsonSerializer.Serialize(Settings, JsonDefaults.SettingsOptions);
        // Owner-only: on Unix this file holds the API keys and database passwords as plain text,
        // because there is no DPAPI to encrypt them with there.
        lock (_writeLock)
            PrivateFile.WriteAllText(_filePath, json);
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

    /// <summary>
    /// Puts a whole settings object in place of the current one — what importing a file does.
    /// </summary>
    /// <remarks>
    /// <para>A replacement rather than a merge, because that is what an imported file <em>is</em>:
    /// merging would leave the machine holding a mixture nobody chose and no way of saying which half
    /// came from where.</para>
    /// <para><b>The secrets on this machine are kept</b>, and that is the one exception the merge rule
    /// makes. An export carries no keys and no passwords (<see cref="SettingsPortability"/>), so an
    /// import that took the file literally would silently empty the working key of every provider the
    /// user already had — a file meant to add configuration removing the only part of it that cannot be
    /// typed back from memory.</para>
    /// <para>Everything else reads <see cref="Settings"/> through this object rather than holding the
    /// object it returned, so a notification is all it takes for the application to be running on the
    /// new one.</para>
    /// </remarks>
    public void Replace(AppSettings settings)
    {
        KeepExistingSecrets(settings);
        Settings = settings;
        // The same two steps the constructor runs, for the same reason: an imported file can be older
        // than this build, or hand-written. Without them an agent this build has and the file does not
        // would have no instance at all — missing from the Agent tile's chooser and from the Goal tile's
        // list, with nothing on screen saying why, until the application was restarted.
        MigrateLegacySettings();
        SeedAgentInstances();
        NotifyChanged();
    }

    /// <summary>Carries this machine's keys across an import that brought none.</summary>
    /// <remarks>Matched by instance id, which is what survives an export: a provider instance or a
    /// manual database connection the imported file also has keeps the secret already configured for it,
    /// and one it does not have arrives empty for the user to fill in. Every field
    /// <see cref="SettingsPortability"/> blanks is restored here — a file exported from this machine and
    /// imported back into it must leave the machine as it was.</remarks>
    private void KeepExistingSecrets(AppSettings incoming)
    {
        foreach (var provider in incoming.AiProviderInstances.Where(p => p.ApiKey.Length == 0))
        {
            if (Settings.AiProviderInstances.FirstOrDefault(existing => existing.Id == provider.Id)
                is { ApiKey.Length: > 0 } known)
                provider.ApiKey = known.ApiKey;
        }

        foreach (var connection in incoming.Database.ManualConnections.Where(c => c.Password.Length == 0))
        {
            if (Settings.Database.ManualConnections.FirstOrDefault(existing => existing.Id == connection.Id)
                is { Password.Length: > 0 } known)
                connection.Password = known.Password;
        }

        if (incoming.Database.SqlServer.Password.Length == 0)
            incoming.Database.SqlServer.Password = Settings.Database.SqlServer.Password;
        if (incoming.Database.PostgreSql.Password.Length == 0)
            incoming.Database.PostgreSql.Password = Settings.Database.PostgreSql.Password;
    }

    public void NotifyChanged()
    {
        SettingsChanged?.Invoke();
        DebouncedSave();
    }

}
