using System.Diagnostics;
using System.Text.Json;
using mTiles.Models;
using mTiles.Services.Agents;

namespace mTiles.Services;

/// <summary>What the wizard is being asked to decide, and what it needs to show.</summary>
public sealed record AgentFileSyncWizardRequest(
    string WorkspaceDirectory,
    AgentFileSyncWizardMode Mode,
    AgentFileSyncFileInfo? Claude,
    AgentFileSyncFileInfo? Agents);

public sealed record AgentFileSyncFileInfo(string FileName, long SizeBytes, DateTime LastWriteTimeUtc);

/// <summary>The wizard's answer.</summary>
/// <param name="Enable">False for "not now" — persisted as a decline, never asked again automatically.</param>
/// <param name="AuthoritativeFileName">Which file to trust when the two disagreed, or null when there
/// was nothing to pick (identical content, or only one file existed).</param>
public sealed record AgentFileSyncWizardResult(bool Enable, string? AuthoritativeFileName);

/// <summary>
/// One per application: decides when a workspace's CLAUDE.md/AGENTS.md sync should be offered, holds
/// the live <see cref="AgentFileSyncEngine"/> for every workspace currently loaded, and reacts to the
/// global switch in Settings.
/// </summary>
/// <remarks>
/// <para>Built once in <c>App.axaml.cs</c> beside <see cref="Database.DatabaseServiceManager"/> — this
/// is workspace-level state, not tile state, so it does not live on <c>TileContext</c> the way
/// <see cref="WorkspaceAgentFiles"/> does.</para>
/// <para>The per-workspace config file is the only source of "did we already ask" — it is read directly
/// off disk rather than cached, because the context-menu toggle can change it for a workspace that has
/// no loaded <c>WorkspaceViewModel</c> at all.</para>
/// </remarks>
public sealed class AgentFileSyncCoordinator : IDisposable
{
    private readonly SettingsService _settings;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, AgentFileSyncEngine> _engines =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Runs one decision per workspace at a time and says whether that workspace is still the
    /// one that was loaded when the decision began. Every entry point here is fire-and-forget, so
    /// without it two callers read the same unanswered config and put up two dialogs.</summary>
    private readonly WorkspaceWorkGate _work = new();

    /// <summary>What each loaded workspace's tile tree held the last time it was evaluated. Kept so the
    /// global switch being turned back on can ask the question the switch itself was suppressing: the
    /// tile tree belongs to the UI thread and Settings hands this nothing, so without a remembered list
    /// a workspace that was never asked would wait for its layout to change or for a re-open.</summary>
    private readonly Dictionary<string, IReadOnlyList<IAiAgent>> _agentsSeen =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Held for the length of one wizard, so only one is ever on screen. See
    /// <see cref="AskAsync"/>.</summary>
    private readonly SemaphoreSlim _wizardTurn = new(1, 1);

    private bool _disposed;

    /// <summary>What the global switch said the last time this looked. Kept because
    /// <c>SettingsChanged</c> is raised for every property on that dialog and this feature reads one of
    /// them: without it, every keystroke in an unrelated field costs a full evaluation per loaded
    /// workspace. Null until the first notification, so the first one is always acted on rather than
    /// compared against a value nobody here has been told about. Written and read under
    /// <see cref="_gate"/>.</summary>
    private bool? _globallyEnabled;

    /// <summary>Workspaces whose question came up before there was anywhere to ask it, with the agents
    /// they held at that moment. The window is built after the last workspace has been restored, so the
    /// first-open question — the main path of the feature — lands here on every launch, and the only
    /// other thing that would ask again is a change to the tile tree. Replayed the moment
    /// <see cref="ShowWizard"/> is wired.</summary>
    private readonly Dictionary<string, IReadOnlyList<IAiAgent>> _deferredQuestions =
        new(StringComparer.OrdinalIgnoreCase);

    private Func<AgentFileSyncWizardRequest, Task<AgentFileSyncWizardResult?>>? _showWizard;

    /// <summary>Wired once, from the shell, to show the actual dialog. Left null before that — a
    /// question that comes up meanwhile is held rather than answered, since "nobody could be asked" is
    /// not a decline.</summary>
    /// <remarks>Called from a thread-pool thread — the decision that leads to it is disk work and does
    /// not run on the UI thread — so whatever is wired here gets itself onto the UI thread, and waits
    /// for the window to be showing: a dialog wants a visible owner.</remarks>
    public Func<AgentFileSyncWizardRequest, Task<AgentFileSyncWizardResult?>>? ShowWizard
    {
        get { lock (_gate) return _showWizard; }
        set
        {
            // Written and read under the same lock as the held questions, and that pairing is the whole
            // point: an evaluation that has just found no wizard has to be able to record its question
            // before this setter can decide there is nothing to replay, or the question is held for a
            // wizard that is already wired and nothing brings it back.
            List<KeyValuePair<string, IReadOnlyList<IAiAgent>>> deferred;
            lock (_gate)
            {
                _showWizard = value;
                if (value is null || _disposed || _deferredQuestions.Count == 0) return;
                deferred = _deferredQuestions.ToList();
                _deferredQuestions.Clear();
            }

            foreach (var (workspaceDir, agents) in deferred)
                _ = EvaluateWorkspaceAsync(workspaceDir, agents);
        }
    }

    /// <summary>The wizard to ask with, or, when there is none yet, null, with this workspace's
    /// question held so that wiring one replays it. One critical section, because the two halves racing
    /// is a question held for ever.</summary>
    private Func<AgentFileSyncWizardRequest, Task<AgentFileSyncWizardResult?>>? WizardOrHold(
        string workspaceDir, IReadOnlyList<IAiAgent> agents, long generation)
    {
        lock (_gate)
        {
            if (_showWizard is { } show) return show;
            if (!_disposed && _work.IsCurrent(workspaceDir, generation))
                _deferredQuestions[workspaceDir] = agents;
            return null;
        }
    }

    public AgentFileSyncCoordinator(SettingsService settings)
    {
        _settings = settings;
        _settings.SettingsChanged += OnSettingsChanged;
    }

    private void OnSettingsChanged()
    {
        // The only thing on that dialog this feature reads is the global switch, and this is raised
        // from every property on it — per keystroke in a text field. Answering each of those with a
        // full evaluation per loaded workspace is a config read and both instruction files read for
        // every character typed, so the question asked first is whether the one setting that matters
        // has actually moved.
        List<KeyValuePair<string, IReadOnlyList<IAiAgent>>> loaded;
        var globallyEnabled = _settings.Settings.AgentFileSyncEnabled;
        lock (_gate)
        {
            if (globallyEnabled == _globallyEnabled) return;
            _globallyEnabled = globallyEnabled;
            loaded = TrackedWorkspaces();
        }

        // A full evaluation rather than only starting and stopping engines: while the global switch was
        // off the policy answered "nothing to ask" and no answer was written down, so turning it back
        // on is exactly the moment those workspaces become askable. It is cheap for the ones that have
        // already answered — the policy stops at the recorded answer before reading either file.
        foreach (var (workspaceDir, agents) in loaded)
            _ = Task.Run(() => _work.RunAsync(workspaceDir,
                generation => EvaluateOnceAsync(workspaceDir, agents, generation)));
    }

    /// <summary>Called when a workspace opens and whenever its tile tree changes — cheap once the
    /// wizard has already been answered.</summary>
    public Task EvaluateWorkspaceAsync(string workspaceDir, IEnumerable<IAiAgent> agentsPresent)
    {
        // The tile tree belongs to the caller's thread, so it is read here; everything after this is
        // disk — the config, both instruction files — and this is reached from every layout change, a
        // dragged splitter included, so it does not belong on the UI thread.
        var agents = agentsPresent.ToList();
        lock (_gate) _agentsSeen[workspaceDir] = agents;
        return Task.Run(() => _work.RunAsync(workspaceDir,
            generation => EvaluateOnceAsync(workspaceDir, agents, generation)));
    }

    private async Task EvaluateOnceAsync(string workspaceDir, IReadOnlyList<IAiAgent> agents,
        long generation)
    {
        var config = AgentFileSyncConfigStore.Load(workspaceDir);
        var claudePath = Path.Combine(workspaceDir, AgentFileSyncEngine.ClaudeFileName);
        var agentsPath = Path.Combine(workspaceDir, AgentFileSyncEngine.AgentsFileName);
        var claudeExists = File.Exists(claudePath);
        var agentsExists = File.Exists(agentsPath);

        // A CLAUDE.md an earlier build left holding the one-line @AGENTS.md import carries none of the
        // user's own words, so it is not one of two versions to pick between: the question is the plain
        // yes/no, and the file is taken out — and immediately written back as a copy — only if the
        // answer is yes. Deleting it before that would leave a user who declines with no file Claude
        // Code reads at all.
        //
        // Asked lazily, and for the reason contentsDiffer is a delegate: this runs on every tile-tree
        // change, a dragged splitter included, and the policy stops at a recorded answer before either
        // file is opened. Answered at most once per evaluation.
        bool? shimOnDisk = null;
        bool ShimStillOnDisk() =>
            shimOnDisk ??= LegacyInstructionShimCleanup.IsPresentIn(workspaceDir);

        var mode = AgentFileSyncPolicy.Decide(
            claudeExists, agentsExists,
            contentsDiffer: () => !ShimStillOnDisk() && !ContentsEqual(claudePath, agentsPath),
            needsClaudeStyle: agents.Any(a => a.InstructionFile.Equals(
                AgentFileSyncEngine.ClaudeFileName, StringComparison.OrdinalIgnoreCase)),
            needsAgentsStyleOnly: agents.Any(a => !a.InstructionFile.Equals(
                AgentFileSyncEngine.ClaudeFileName, StringComparison.OrdinalIgnoreCase)),
            wizardAlreadyAnswered: config.WizardAnswered,
            globallyEnabled: _settings.Settings.AgentFileSyncEnabled);

        if (mode == AgentFileSyncWizardMode.None)
        {
            await ApplyEffectiveStateAsync(workspaceDir, config, generation);
            return;
        }

        // Nowhere to ask yet? The question is held rather than dropped: nothing else would bring it
        // back until the tile tree changes, which for a workspace restored at startup may be never.
        if (WizardOrHold(workspaceDir, agents, generation) is not { } show)
        {
            await ApplyEffectiveStateAsync(workspaceDir, config, generation);
            return;
        }

        var answer = await AskAsync(show, workspaceDir, mode, claudePath, agentsPath);
        if (answer is null)
        {
            // The dialog itself failed. That is not an answer, so nothing is written down and this
            // workspace is offered the choice again next time it is opened.
            await ApplyEffectiveStateAsync(workspaceDir, config, generation);
            return;
        }

        config.WizardAnswered = true;
        config.Enabled = answer.Enable;
        config.AuthoritativeFileName = null;
        string? authoritative = null;
        if (config.Enabled)
        {
            var shimStillOnDisk = ShimStillOnDisk();
            if (shimStillOnDisk) LegacyInstructionShimCleanup.Run(workspaceDir);
            config.AuthoritativeFileName = shimStillOnDisk
                ? AgentFileSyncEngine.AgentsFileName
                : answer.AuthoritativeFileName;
            authoritative = config.AuthoritativeFileName;
        }
        AgentFileSyncConfigStore.Save(workspaceDir, config);

        await ApplyEffectiveStateAsync(workspaceDir, config, generation, authoritative);
    }

    /// <summary>The manual context-menu toggle. Bypasses "already answered" — this is an explicit
    /// action, not an automatic prompt. Works for a workspace that has no loaded
    /// <c>WorkspaceViewModel</c>: everything it needs is the directory and what is already on disk.
    /// </summary>
    public Task SetWorkspaceEnabledAsync(string workspaceDir, bool enabled) =>
        Task.Run(() => _work.RunAsync(workspaceDir,
            generation => SetEnabledOnceAsync(workspaceDir, enabled, generation)));

    private async Task SetEnabledOnceAsync(string workspaceDir, bool enabled, long generation)
    {
        var config = AgentFileSyncConfigStore.Load(workspaceDir);
        string? authoritative = null;

        // Everything in this block exists to bring an engine up — the shim comes out only because the
        // engine is about to take the files over, and the question is asked only because a mirror
        // would have to settle between them — so it runs only when one will: the global switch can be
        // off while the menu is open (its visibility is decided when the menu is built), and taking
        // the shim out ahead of a start that never comes would leave the workspace with no file
        // Claude Code reads at all, and nothing here recreates it. The answer is recorded all the
        // same: the toggle is an explicit decision, and the switch coming back on is exactly the
        // moment it applies.
        if (enabled && _settings.Settings.AgentFileSyncEnabled)
        {
            // Sync is being switched on here, which is the one moment the old one-line @AGENTS.md
            // import may be taken off disk: left there it is simply a CLAUDE.md whose content differs,
            // and answering "CLAUDE.md is the current one" would replace the whole of AGENTS.md with
            // that single line. The file comes back below as a copy of AGENTS.md.
            LegacyInstructionShimCleanup.Run(workspaceDir);

            var claudePath = Path.Combine(workspaceDir, AgentFileSyncEngine.ClaudeFileName);
            var agentsPath = Path.Combine(workspaceDir, AgentFileSyncEngine.AgentsFileName);
            var claudeExists = File.Exists(claudePath);
            var agentsExists = File.Exists(agentsPath);

            if (claudeExists && agentsExists && !ContentsEqual(claudePath, agentsPath))
            {
                var answer = ShowWizard is { } show
                    ? await AskAsync(show, workspaceDir,
                        AgentFileSyncWizardMode.AskEnableAndPickAuthoritative, claudePath, agentsPath)
                    : null;

                // Nobody could be asked which of the two is current — no wizard wired yet, or the
                // dialog itself failed — and switching sync on here overwrites one of them in somebody
                // else's repository. Deciding that for them from the mtimes is the one thing this must
                // not do, so nothing is written and nothing is recorded: the toggle can be pressed
                // again once there is a window to ask in.
                if (answer is null)
                {
                    await ApplyEffectiveStateAsync(workspaceDir, config, generation);
                    return;
                }

                if (!answer.Enable)
                {
                    config.WizardAnswered = true;
                    config.Enabled = false;
                    config.AuthoritativeFileName = null;
                    AgentFileSyncConfigStore.Save(workspaceDir, config);
                    await ApplyEffectiveStateAsync(workspaceDir, config, generation);
                    return;
                }

                authoritative = answer.AuthoritativeFileName;
                config.AuthoritativeFileName = authoritative;
            }
        }

        config.WizardAnswered = true;
        config.Enabled = enabled;
        AgentFileSyncConfigStore.Save(workspaceDir, config);
        await ApplyEffectiveStateAsync(workspaceDir, config, generation, authoritative);
    }

    public bool IsEnabled(string workspaceDir) => AgentFileSyncConfigStore.Load(workspaceDir).Enabled;

    /// <summary>Stops and forgets this workspace's engine — called when the workspace is unloaded.
    /// The persisted config is untouched; a later re-open resumes exactly what was chosen.</summary>
    public void Unload(string workspaceDir)
    {
        AgentFileSyncEngine? engine;
        lock (_gate)
        {
            // Under the same lock as the held questions, so an evaluation that has just decided it may
            // hold one cannot slip it in after this has cleared them: replayed later, it would build an
            // engine for a workspace nobody has open.
            _work.Invalidate(workspaceDir);
            // A question held for a workspace nobody has open any more is one nobody should be shown:
            // it is asked again when the workspace is opened next.
            _deferredQuestions.Remove(workspaceDir);
            _agentsSeen.Remove(workspaceDir);
            _engines.Remove(workspaceDir, out engine);
        }
        engine?.Dispose();
    }

    /// <summary>Stops and forgets every live engine, keeping every persisted answer. For the moment
    /// the whole list of workspaces is replaced: an engine also exists for a workspace nobody ever
    /// opened, because the panel's toggle starts one from the row alone, and only this reaches it.
    /// </summary>
    public void UnloadAll()
    {
        List<string> loaded;
        lock (_gate) loaded = TrackedWorkspaces().Select(entry => entry.Key).ToList();
        foreach (var workspaceDir in loaded)
            Unload(workspaceDir);
    }

    /// <summary>Every workspace this coordinator is currently tracking, with the agents last seen in
    /// it. Expects <see cref="_gate"/> to be held.</summary>
    /// <remarks>The two tables answer for different halves and neither is the whole list, so the
    /// answer is their union: <see cref="_agentsSeen"/> holds a workspace that has been evaluated
    /// whether or not it ever needed an engine, and <see cref="_engines"/> holds one the panel's
    /// toggle started from a row nobody has opened, whose tile tree nothing here has ever seen.
    /// Reading either alone silently drops a workspace from the re-evaluation the global switch owes
    /// it.</remarks>
    private List<KeyValuePair<string, IReadOnlyList<IAiAgent>>> TrackedWorkspaces() =>
        _agentsSeen.Keys.Concat(_engines.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(dir => new KeyValuePair<string, IReadOnlyList<IAiAgent>>(
                dir, _agentsSeen.GetValueOrDefault(dir, [])))
            .ToList();

    /// <summary>Unsubscribes from Settings and stops every live engine — called when the application
    /// shuts down. The persisted per-workspace answers are untouched.</summary>
    public void Dispose()
    {
        _settings.SettingsChanged -= OnSettingsChanged;
        _work.Close();
        lock (_gate)
        {
            _disposed = true;
            _deferredQuestions.Clear();
            _agentsSeen.Clear();
            foreach (var engine in _engines.Values)
                engine.Dispose();
            _engines.Clear();
        }
    }

    private async Task<AgentFileSyncWizardResult?> AskAsync(
        Func<AgentFileSyncWizardRequest, Task<AgentFileSyncWizardResult?>> show, string workspaceDir,
        AgentFileSyncWizardMode mode, string claudePath, string agentsPath)
    {
        // One dialog at a time for the whole application. The per-workspace gate says nothing about two
        // different workspaces, and two of them ask at once routinely: the held questions are replayed
        // together the moment the window wires ShowWizard, and opening a second workspace while the
        // first one's wizard is up does the same. Two modals over one owner window overlap, and the
        // first to close puts the owner's state back while the second is still showing.
        await _wizardTurn.WaitAsync();
        try
        {
            return await AskOnceAsync(show, workspaceDir, mode, claudePath, agentsPath);
        }
        finally
        {
            _wizardTurn.Release();
        }
    }

    private static async Task<AgentFileSyncWizardResult?> AskOnceAsync(
        Func<AgentFileSyncWizardRequest, Task<AgentFileSyncWizardResult?>> show, string workspaceDir,
        AgentFileSyncWizardMode mode, string claudePath, string agentsPath)
    {
        var request = new AgentFileSyncWizardRequest(
            workspaceDir, mode,
            Describe(claudePath, AgentFileSyncEngine.ClaudeFileName),
            Describe(agentsPath, AgentFileSyncEngine.AgentsFileName));
        try
        {
            return await show(request);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Agent file sync wizard failed for '{0}': {1}", workspaceDir, ex.Message);
            return null;
        }
    }

    private static AgentFileSyncFileInfo? Describe(string path, string fileName)
    {
        if (!File.Exists(path)) return null;
        var info = new FileInfo(path);
        return new AgentFileSyncFileInfo(fileName, info.Length, info.LastWriteTimeUtc);
    }

    /// <summary>Starts or stops this workspace's engine to match the answer on disk and the global
    /// switch.</summary>
    /// <param name="authoritativeFileName">Which of the two files the user has just said is the current
    /// one, where they have been asked. It is handed to the engine rather than acted on here: making
    /// the two agree is one rule, and it lives in <see cref="AgentFileSyncEngine"/>, which owns the
    /// reading, the byte comparison and the atomic write already — kept here as well, a change to that
    /// rule would have to be made in two classes and only one of them has tests for it.</param>
    private async Task ApplyEffectiveStateAsync(string workspaceDir, WorkspaceAgentFileSyncConfig config,
        long generation, string? authoritativeFileName = null)
    {
        var wants = config.Enabled && _settings.Settings.AgentFileSyncEnabled;
        AgentFileSyncEngine engine;
        lock (_gate)
        {
            // Unloaded while this decision was being made: building an engine for it now would
            // resurrect a mirror the user has just put down.
            if (_disposed || !_work.IsCurrent(workspaceDir, generation)) return;
            if (!_engines.TryGetValue(workspaceDir, out engine!))
            {
                engine = new AgentFileSyncEngine(workspaceDir);
                _engines[workspaceDir] = engine;
            }
        }

        if (wants)
        {
            // A running engine has already seeded itself from whatever was on disk then, so an answer
            // arriving now — the toggle pressed on a workspace whose mirror is already live — is only
            // honoured by seeding again.
            if (engine.IsRunning && authoritativeFileName is not null) engine.Stop();
            if (!engine.IsRunning)
            {
                // The engine is about to take both files over, and a legacy one-line @AGENTS.md import
                // can still be on disk here — the toggle that enabled sync may have run while the
                // global switch was off, which records the answer and starts nothing. It holds none of
                // the user's words, so it is not one of two versions the seeding settles between: it
                // comes out now, with the seeding told to trust AGENTS.md — the same answer the wizard
                // and the toggle give on the paths where they are the ones taking it out. Asked only
                // here, at a start, because this runs on every tile-tree change and a start is rare.
                if (authoritativeFileName is null &&
                    LegacyInstructionShimCleanup.IsPresentIn(workspaceDir))
                {
                    LegacyInstructionShimCleanup.Run(workspaceDir);
                    authoritativeFileName = AgentFileSyncEngine.AgentsFileName;
                }
                // The choice the wizard recorded outlives the run that asked it: a start cut off by an
                // unload — or one that waited for the global switch — must not settle the same
                // disagreement by mtime when it finally happens, overwriting the very file the user
                // named as the current one. The shim check stays ahead of it: a file holding none of
                // the user's words is not one of two versions, whatever an older answer says.
                authoritativeFileName ??= config.AuthoritativeFileName;
                await engine.StartAsync(authoritativeFileName);
            }
        }
        else if (engine.IsRunning)
        {
            engine.Stop();
        }

        // Dispose or Unload could have run while the watcher was starting, and each only stops the
        // engines it can see. Anything started after that is this call's to take back down.
        lock (_gate)
        {
            if (!_disposed && _work.IsCurrent(workspaceDir, generation)) return;
            // The entry is dropped only while it is still this engine's — Unload has usually taken it
            // already — but the engine is disposed either way: Unload's own Dispose ran before the
            // start that followed it, so nobody else is left holding this watcher.
            if (ReferenceEquals(_engines.GetValueOrDefault(workspaceDir), engine))
                _engines.Remove(workspaceDir);
        }
        engine.Dispose();
    }

    private static bool ContentsEqual(string pathA, string pathB)
    {
        try
        {
            return File.ReadAllBytes(pathA).AsSpan().SequenceEqual(File.ReadAllBytes(pathB));
        }
        catch
        {
            // Unreadable is not "identical" — treat as differing so the wizard offers a real choice
            // rather than silently picking a winner.
            return false;
        }
    }
}
