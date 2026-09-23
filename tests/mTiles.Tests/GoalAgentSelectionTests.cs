using mTiles.Models;
using mTiles.Services;
using mTiles.Services.Agents;
using mTiles.ViewModels;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// Which agent a Goal tile runs each phase on, and what happens when the one it names is gone.
/// </summary>
/// <remarks>
/// The whole point of the review being a separate choice is that the work and the judgement of it can
/// come from two different models. That only means anything if the review phase actually reaches the
/// second one, which is what the first test here asserts — and if a goal whose agent has disappeared is
/// <em>not</em> quietly moved onto whatever else is installed, which is what the last one does.
/// </remarks>
[Collection(GoalSeamCollection.Name)]
public class GoalAgentSelectionTests : GoalTileFixture
{
    private static readonly GoalAgentChoice Worker = FakeChoice("Worker", "worker");
    private static readonly GoalAgentChoice Reviewer = FakeChoice("Reviewer", "reviewer");
    private static readonly GoalAgentChoice Planner = FakeChoice("Planner", "planner");

    /// <summary>
    /// Working the goal out and planning go to the planning agent, the review to the review agent, and
    /// only the work itself to the execution agent.
    /// </summary>
    /// <remarks>Nothing in the transcript tells the models apart, so a slot that is never reached — or a
    /// planner that also implemented, behind the worktree <c>GoalBaseline</c> photographs once — is
    /// invisible without this.</remarks>
    [Fact]
    public void Each_phase_runs_on_the_agent_chosen_for_it()
    {
        Ui.Run(async () =>
        {
            GoalAgents.Factory = _ => [Worker, Reviewer, Planner];

            var asked = new List<(string Agent, string Answer)>();
            string[] answers = [.. UpToTheReview, "VERDICT: PASS"];
            var next = 0;

            GoalTileViewModel.AiRunnerFactory = (choice, _, _, _) =>
            {
                var answer = answers[Math.Min(next++, answers.Length - 1)];
                asked.Add((choice.Label, answer));
                return Task.FromResult<AiOutput>(answer);
            };

            using var vm = NewTile();
            vm.PlanningAgentInstanceId = Planner.InstanceId;
            vm.ReviewAgentInstanceId = Reviewer.InstanceId;

            await RunToSummary(vm);

            Assert.Equal(
                [("Planner", "Which files?"), ("Planner", NoMoreQuestions), ("Planner", "The plan"),
                 ("Worker", "Implemented it"), ("Reviewer", "VERDICT: PASS")],
                asked.Select(a => (a.Agent, a.Answer)));
        });
    }

    /// <summary>An empty planning choice means the agent doing the work, everywhere.</summary>
    [Fact]
    public void Planning_falls_back_to_the_execution_agent_when_none_is_chosen()
    {
        Ui.Run(() =>
        {
            GoalAgents.Factory = _ => [Worker, Planner];

            using var vm = NewTile();

            Assert.Equal(Worker.InstanceId, vm.PlanningAgent?.InstanceId);

            vm.PlanningAgentInstanceId = Planner.InstanceId;
            Assert.Equal(Planner.InstanceId, vm.PlanningAgent?.InstanceId);

        });
    }

    /// <summary>
    /// The planning agent survives the goal file.
    /// </summary>
    /// <remarks>It is a once-per-goal setting on a tile that is meant to be closed and come back, so a
    /// choice that is not written down is one the user makes again every morning — and silently, since
    /// the run that follows looks exactly the same.</remarks>
    [Fact]
    public void A_planning_agent_is_written_to_the_goal_file_and_read_back()
    {
        Ui.Run(() =>
        {
            GoalAgents.Factory = _ => [Worker, Planner];

            var path = Path.Combine(Dir, "planned-goal.json");
            File.WriteAllText(path, """
                {"OriginalGoal":"a goal","ExecutionAgentInstanceId":"worker","CurrentPhase":"Goal"}
                """);

            using (var vm = Open(path))
                vm.PlanningAgentInstanceId = Planner.InstanceId;

            using var reopened = Open(path);

            Assert.Equal(Planner.InstanceId, reopened.PlanningAgentInstanceId);
            Assert.Equal(Planner.InstanceId, reopened.PlanningAgent?.InstanceId);
            Assert.Equal(Worker.InstanceId, reopened.ExecutionAgent?.InstanceId);
        });
    }

    /// <summary>An empty review choice means the agent doing the work, everywhere.</summary>
    [Fact]
    public void Reviewing_falls_back_to_the_execution_agent_when_none_is_chosen()
    {
        Ui.Run(() =>
        {
            GoalAgents.Factory = _ => [Worker, Reviewer];

            using var vm = NewTile();

            Assert.Equal(Worker.InstanceId, vm.ExecutionAgent?.InstanceId);
            Assert.Equal(Worker.InstanceId, vm.ReviewAgent?.InstanceId);

            vm.ReviewAgentInstanceId = Reviewer.InstanceId;
            Assert.Equal(Reviewer.InstanceId, vm.ReviewAgent?.InstanceId);

        });
    }

    /// <summary>
    /// A goal file from before agents had instances still names an agent.
    /// </summary>
    /// <remarks>Read for ever rather than migrated once: a goal file travels with a branch, so one
    /// written on another machine or on an older branch will keep arriving with a tool name in it.
    /// </remarks>
    [Fact]
    public void A_goal_saved_with_a_tool_name_reopens_on_the_matching_agent()
    {
        Ui.Run(() =>
        {
            GoalAgents.Factory = _ => [Worker, Reviewer];

            var path = Path.Combine(Dir, "old-goal.json");
            File.WriteAllText(path, """
                {"OriginalGoal":"a goal","SelectedToolName":"Reviewer","CurrentPhase":"Goal"}
                """);

            using var vm = Open(path);

            Assert.Equal(Reviewer.InstanceId, vm.ExecutionAgent?.InstanceId);
        });
    }

    /// <summary>
    /// A goal whose agent has gone is not moved onto another one behind the user's back.
    /// </summary>
    /// <remarks>The old tool list substituted the first installed tool and said so once, in a transcript
    /// nobody rereads — so a goal planned against one model was carried out by another. The choice now
    /// stands, empty-handed, and the run says which agent it is waiting for.</remarks>
    [Fact]
    public void A_goal_whose_agent_is_gone_keeps_naming_it()
    {
        Ui.Run(() =>
        {
            GoalAgents.Factory = _ => [Worker, Reviewer];

            var path = Path.Combine(Dir, "goal.json");
            File.WriteAllText(path, """
                {"OriginalGoal":"a goal","ExecutionAgentInstanceId":"deleted","CurrentPhase":"Goal"}
                """);

            using var vm = Open(path);

            Assert.Equal("deleted", vm.ExecutionAgentInstanceId);
            Assert.Null(vm.ExecutionAgent);
        });
    }

    /// <summary>
    /// A reviewer that is no longer here is not quietly replaced by the agent being reviewed.
    /// </summary>
    /// <remarks>An instance deleted in Settings, or one whose provider stopped being compatible, leaves
    /// the id behind in the goal file. Falling back to the execution agent there is the substitution the
    /// second choice exists to prevent, and it is invisible: the review runs, passes, and nothing in the
    /// transcript says the work was marked by whoever did it.</remarks>
    [Fact]
    public void A_review_agent_that_is_gone_does_not_fall_back_to_the_execution_agent()
    {
        Ui.Run(() =>
        {
            GoalAgents.Factory = _ => [Worker];

            var path = Path.Combine(Dir, "reviewed-goal.json");
            File.WriteAllText(path, """
                {"OriginalGoal":"a goal","ExecutionAgentInstanceId":"worker",
                 "ReviewAgentInstanceId":"deleted","CurrentPhase":"Goal"}
                """);

            using var vm = Open(path);

            Assert.Equal(Worker.InstanceId, vm.ExecutionAgent?.InstanceId);
            Assert.Equal("deleted", vm.ReviewAgentInstanceId);
            Assert.Null(vm.ReviewAgent);
        });
    }

    /// <summary>An agent with no gate weaker than bypass, the way opencode and pi answer.</summary>
    private sealed class NoWeakGateAgent : StubAgent
    {
        public override IReadOnlyList<AiBehaviour> SupportedBehaviours(
            AiAgentInstance instance, AiUsage usage) =>
            [AiBehaviour.BypassPermissions, AiBehaviour.ToolDefault];
    }

    /// <summary>
    /// The strip offers only the modes the chosen agent actually has.
    /// </summary>
    /// <remarks>Offering "auto" for an agent with no such gate stored a mode <c>AiProcessRunner.Fit</c>
    /// rounds away to <see cref="AiBehaviour.ToolDefault"/>: the run went out asking for permission a
    /// headless run has nobody to give, while the strip said it would not ask. The floor is meant to be
    /// reached by a stored value, never by a word somebody was offered.</remarks>
    [Fact]
    public void The_permission_strip_offers_only_what_the_agent_has()
    {
        Ui.Run(() =>
        {
            var narrow = new GoalAgentChoice(
                new AiAgentInstance { Id = "narrow", AgentId = "stub", Name = "Narrow" },
                new NoWeakGateAgent(),
                typeof(GoalAgentSelectionTests).Assembly.Location);
            GoalAgents.Factory = _ => [narrow];

            using var vm = NewTile();

            Assert.DoesNotContain(AiBehaviours.Label(AiBehaviour.Auto), vm.AvailablePermissionModes);
            Assert.Contains(AiBehaviours.Label(AiBehaviour.BypassPermissions), vm.AvailablePermissionModes);
            Assert.Contains(AiBehaviours.Label(AiBehaviour.ToolDefault), vm.AvailablePermissionModes);

            // And the word shown is one of the rows: the default setting is "auto", which this agent
            // does not have, so a chooser that kept it would sit blank on a mode no run would use.
            Assert.Contains(vm.PermissionModeLabel, vm.AvailablePermissionModes);
        });
    }

    /// <summary>
    /// A combo box clearing its own selection does not take the tile down with it.
    /// </summary>
    /// <remarks>
    /// <para>Both id properties are the target of a two-way <c>SelectedValue</c> binding, and emptying a
    /// combo box's <c>ItemsSource</c> — which looking for agents again does every time — makes Avalonia
    /// write <c>null</c> back into the bound property. That is not a hypothetical: it crashed the tile
    /// with a <see cref="NullReferenceException"/> out of <c>GoalAgents.WithId</c>, reached through the
    /// change notification re-reading the permission strip while the list was still empty.</para>
    /// <para>Written as a plain assignment rather than through a real combo box on purpose: what is
    /// being pinned is that the property tolerates a null from <em>anywhere</em>, and a test that needed
    /// a view would not run wherever the binding is not the only way in — the goal file's own restore
    /// writes here too.</para>
    /// </remarks>
    [Fact]
    public void A_null_written_by_a_binding_reads_back_as_no_agent()
    {
        Ui.Run(() =>
        {
            GoalAgents.Factory = _ => [Worker, Reviewer];

            using var vm = NewTile();
            vm.ReviewAgentInstanceId = Reviewer.InstanceId;

            vm.ExecutionAgentInstanceId = null!;
            vm.ReviewAgentInstanceId = null!;

            Assert.Equal("", vm.ExecutionAgentInstanceId);
            Assert.Equal("", vm.ReviewAgentInstanceId);

            // The two properties the notification cascade reads on the way out. Reading them at all is
            // the assertion — each one dereferenced the id before this.
            Assert.Null(vm.ExecutionAgent);
            Assert.Null(vm.ReviewAgent);
            Assert.NotEmpty(vm.AvailablePermissionModes);

        });
    }
}
