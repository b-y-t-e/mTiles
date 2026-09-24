using mTiles.Models;
using mTiles.Services;
using mTiles.Services.Agents;
using mTiles.ViewModels;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The base of every test that drives a <see cref="GoalTileViewModel"/> with the AI replaced: it owns
/// the tile's static seams and puts every one of them back in <see cref="Dispose"/>.
/// </summary>
/// <remarks>
/// <para>The defaults are the ones a loop test wants and a real machine would not give it: one agent
/// that is always "installed", a working tree that is different on every read (the loop stops when an
/// attempt changed nothing, so a stub answering the same string twice ends every run after one
/// attempt), no baseline, and no git behind an <c>@</c> ref. A test that is about one of those replaces
/// that one seam and leaves the rest.</para>
/// <para>A subclass carries <c>[Collection(GoalSeamCollection.Name)]</c>: the seams are process-wide.
/// </para>
/// </remarks>
public abstract class GoalTileFixture : IDisposable
{
    /// <summary>What a clarification round comes back with when the tool has nothing left to ask.</summary>
    private protected const string NoMoreQuestions = "```json\n{\"needsClarification\":false}\n```";

    /// <summary>A structured review that found nothing and says the goal is met.</summary>
    private protected const string CleanReview = "```json\n{\"goalMet\":true,\"findings\":[]}\n```";

    /// <summary>A structured review carrying one warning and nothing else.</summary>
    private protected const string WarningReview =
        "```json\n{\"goalMet\":false,\"findings\":[{\"severity\":\"warning\"," +
        "\"title\":\"stale scope\",\"file\":\"a.cs\"}]}\n```";

    /// <summary>A structured review carrying one error, which is what closes the commit offer.</summary>
    private protected const string ErrorReview =
        "```json\n{\"goalMet\":false,\"findings\":[{\"severity\":\"error\"," +
        "\"title\":\"null deref\",\"file\":\"a.cs\"}]}\n```";

    /// <summary>Clarify asks, the answer ends the questions, a plan, an implementation: every answer a
    /// run needs before its first review.</summary>
    private protected static readonly string[] UpToTheReview =
        ["Which files?", NoMoreQuestions, "The plan", "Implemented it"];

    private readonly TempDirectory _temp = new("mtiles-goal");
    private SettingsService? _settings;

    private protected GoalTileFixture()
    {
        var reads = 0;
        WorktreeReader.Factory = (_, _) =>
            Task.FromResult<string?>($"diff --git a/x b/x\n+ line {Interlocked.Increment(ref reads)}");
        GoalBaseline.Factory = (_, _) => Task.FromResult(GoalBaselineResult.None);
        GoalAgents.Factory = _ => [FakeChoice("Fake Tool")];
        GoalScopeRef.Factory = (_, _) => Task.FromResult<GoalReadBase?>(null);
    }

    public virtual void Dispose()
    {
        GoalTileViewModel.AiRunnerFactory = null;
        WorktreeReader.Factory = null;
        WorktreeReader.ReadObserved = null;
        WorktreeReader.BaseObserved = null;
        GoalBaseline.Factory = null;
        GoalAgents.Factory = null;
        GoalScopeRef.Factory = null;
        GoalImageStore.Factory = null;
        _temp.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>The workspace directory the tile works in.</summary>
    private protected string Dir => _temp.Path;

    private protected SettingsService Settings => _settings ??= new SettingsService(_temp["settings.json"]);

    /// <summary>An agent that is never started (the runner seam stands in front) but whose "binary" is a
    /// file that certainly exists, because the tile refuses to run a phase on an agent it cannot find.
    /// </summary>
    private protected sealed class FakeAgent : StubAgent;

    private protected static GoalAgentChoice FakeChoice(string name, string? id = null) => new(
        new AiAgentInstance { Id = id ?? name, AgentId = "stub", Name = name },
        new FakeAgent(),
        typeof(GoalTileFixture).Assembly.Location);

    /// <summary>A tile on the fixture's workspace, with a confirmation that always says yes.</summary>
    private protected GoalTileViewModel NewTile() =>
        new(Dir, Settings) { ConfirmAction = _ => Task.FromResult(true) };

    /// <summary>The tile reopened from its own file, as a restart does. Disposes <paramref name="vm"/>.
    /// </summary>
    private protected GoalTileViewModel Reopen(GoalTileViewModel vm)
    {
        var path = vm.FilePath;
        vm.Dispose();
        return Open(path);
    }

    /// <summary>A tile opened from a goal file.</summary>
    private protected GoalTileViewModel Open(string path) =>
        new(path, Dir, Settings) { ConfirmAction = _ => Task.FromResult(true) };

    /// <summary>What the tile wrote down. Disposes <paramref name="vm"/>, which is what flushes it.</summary>
    private protected static GoalTileState Saved(GoalTileViewModel vm)
    {
        var path = vm.FilePath;
        vm.Dispose();
        var state = new GoalStatePersistence().Load(path);
        Assert.NotNull(state);
        return state!;
    }

    /// <summary>Answers each prompt in turn, repeating the last answer once the list runs out.</summary>
    private protected static void AnswerWith(params string[] answers) =>
        Script(answers.Select(a => (AiOutput)a).ToArray());

    /// <summary>Answers each prompt in turn, repeating the last, and hands back every prompt asked.
    /// </summary>
    private protected static List<string> Script(params AiOutput[] answers)
    {
        var prompts = new List<string>();
        GoalTileViewModel.AiRunnerFactory = (_, prompt, _, _) =>
        {
            lock (prompts)
            {
                var answer = answers[Math.Min(prompts.Count, answers.Length - 1)];
                prompts.Add(prompt);
                return Task.FromResult(answer);
            }
        };
        return prompts;
    }

    /// <summary><see cref="Script"/> as plain strings.</summary>
    private protected static List<string> Script(params string[] answers) =>
        Script(answers.Select(a => (AiOutput)a).ToArray());

    /// <summary><see cref="UpToTheReview"/> followed by <paramref name="then"/>.</summary>
    private protected static string[] ThroughTheReview(params string[] then) => [.. UpToTheReview, .. then];

    /// <summary>A baseline the run can commit against.</summary>
    private protected static void WithBaseline() =>
        GoalBaseline.Factory = (_, _) =>
            Task.FromResult(new GoalBaselineResult("refs/mtiles/goals/test", false));

    /// <summary>A baseline that answers <c>ref-1</c>, <c>ref-2</c>, … and honours its token as the real
    /// one does; the answer is how many snapshots have been taken.</summary>
    private protected static Func<int> CountingBaseline()
    {
        var captures = 0;
        GoalBaseline.Factory = (_, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new GoalBaselineResult($"ref-{++captures}", NoRepository: false));
        };
        return () => captures;
    }

    /// <summary>The same tree at every read: an attempt that changed nothing.</summary>
    private protected static void TreeNeverMoves() =>
        WorktreeReader.Factory = (_, _) => Task.FromResult<string?>("diff --git a/x b/x");

    /// <summary>Goal, then an answer to the questions, then "ok" to the plan.</summary>
    private protected static async Task RunToSummary(GoalTileViewModel vm, string goal = "a goal")
    {
        await Send(vm, goal);
        await Send(vm, "all of it");
        await Send(vm, "ok");
    }

    /// <summary>Types <paramref name="text"/> into the composer and sends it.</summary>
    private protected static Task Send(GoalTileViewModel vm, string text)
    {
        vm.InputText = text;
        return vm.SubmitCommand.ExecuteAsync(null);
    }

    /// <summary>Writes a file into the workspace, making the directories on the way.</summary>
    private protected void WriteFile(string relative, string content = "// x")
    {
        var full = Path.Combine(Dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    /// <summary>Waits until <paramref name="condition"/> holds, re-checked whenever the tile raises a
    /// change, and fails after <paramref name="within"/>.</summary>
    private protected static async Task WhenTrue(GoalTileViewModel vm, Func<bool> condition, TimeSpan within)
    {
        if (condition()) return;
        var seen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Check(object? s, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (condition()) seen.TrySetResult();
        }
        vm.PropertyChanged += Check;
        try
        {
            if (condition()) return;
            await seen.Task.WaitAsync(within);
        }
        finally
        {
            vm.PropertyChanged -= Check;
        }
    }
}
