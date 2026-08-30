using System.Text.Json;
using Avalonia.Headless;
using mTiles.Models;
using mTiles.Services;
using mTiles.ViewModels;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// What happens to an image pasted into a Goal tile: the marker it leaves behind, which prompts are
/// told about it, and what survives a goal being replaced or a session being reloaded.
/// </summary>
/// <remarks>
/// Argued here rather than in the view model, where none of it can be run without a window and a
/// clipboard. Everything below is about the run's own bookkeeping — the view's part is turning a
/// clipboard bitmap into bytes, which has nothing to decide.
/// </remarks>
public class GoalImageAttachmentTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "mtiles-images-" + Guid.NewGuid().ToString("N"));

    public GoalImageAttachmentTests() => Directory.CreateDirectory(_dir);

    /// <summary>Both seams are static, so they are put back whatever the test did with them.</summary>
    /// <remarks>A stub left standing is the next test in this assembly writing its images through
    /// somebody else's lambda, or refusing to — a failure that appears in a file nobody edited.</remarks>
    public void Dispose()
    {
        GoalImageStore.Factory = null;
        GoalTileViewModel.AiRunnerFactory = null;

        try { Directory.Delete(_dir, recursive: true); } catch { /* a temp directory */ }
    }

    /// <summary>
    /// Runs the body on the headless UI thread, as the loop tests do.
    /// </summary>
    /// <remarks>The view model dispatches every message it adds, so a test driving it from the test
    /// thread would wait on a dispatcher nobody is pumping.</remarks>
    private static void OnUiThread(Func<Task> body)
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(
            typeof(GoalImageAttachmentTests).Assembly);

        session.Dispatch(async () => { await body(); return true; }, CancellationToken.None)
            .GetAwaiter().GetResult();
    }

    private GoalTileViewModel NewTile() =>
        new(_dir, new SettingsService(Path.Combine(_dir, "settings.json")))
        {
            ConfirmAction = _ => Task.FromResult(true),
        };

    private static GoalWorkflowEngine WithImages(string goal, params string[] paths)
    {
        var engine = new GoalWorkflowEngine();
        foreach (var path in paths) engine.AttachImage(path);
        engine.StartNewGoal(goal);
        return engine;
    }

    [Fact]
    public void An_image_leaves_a_numbered_marker_where_the_user_was_typing()
    {
        var engine = new GoalWorkflowEngine();

        Assert.Equal("[Image #1]", engine.AttachImage(@"C:\shots\one.png"));
        Assert.Equal("[Image #2]", engine.AttachImage(@"C:\shots\two.png"));
    }

    [Fact]
    public void A_new_goal_keeps_the_images_its_own_text_refers_to()
    {
        // The paste happens *before* the goal is started — the user pastes into the composer and then
        // presses Send — so clearing outright would strip every image out of the goal being started.
        var engine = WithImages("compare [Image #2] against the old one", @"C:\a.png", @"C:\b.png");

        Assert.Equal([2], engine.AttachedImages.Select(image => image.Index));
        Assert.Equal(@"C:\b.png", engine.AttachedImages.Single().Path);
    }

    [Fact]
    public void A_marker_left_in_the_composer_goes_when_its_image_does()
    {
        // The + button and a detected goal both replace the goal without touching the composer, so a
        // marker pasted a moment earlier would otherwise be sent naming a file no prompt mentions.
        var text = GoalImageMarker.DropMarkersExcept("before [Image #1] after [Image #2] end", [2]);

        Assert.Equal("before after [Image #2] end", text);
    }

    [Fact]
    public void Dropping_a_marker_leaves_text_that_holds_no_image_alone()
    {
        Assert.Equal("nothing to drop", GoalImageMarker.DropMarkersExcept("nothing to drop", []));
        Assert.Equal("", GoalImageMarker.DropMarkersExcept("", [1]));
    }

    [Fact]
    public void A_marker_is_never_handed_out_twice()
    {
        // The kept image is #2, so counting would number the next paste 2 as well and two different
        // files would answer to one marker.
        var engine = WithImages("just [Image #2]", @"C:\a.png", @"C:\b.png");

        Assert.Equal("[Image #3]", engine.AttachImage(@"C:\c.png"));
    }

    [Theory]
    [InlineData("clarify")]
    [InlineData("plan")]
    [InlineData("implement")]
    [InlineData("review")]
    public void Every_prompt_that_carries_the_goal_says_where_its_images_are(string phase)
    {
        var engine = WithImages("what is wrong with [Image #1]", @"C:\shots\one.png");

        var prompt = phase switch
        {
            "clarify" => engine.BuildClarifyPrompt(),
            "plan" => engine.BuildPlanPrompt(),
            "implement" => engine.BuildImplementPrompt(gitDiff: null),
            _ => engine.BuildReviewPrompt(gitDiff: null),
        };

        // The marker and the path together: the marker alone is something the tool cannot open, and a
        // bare path does not say which sentence the picture belongs to.
        Assert.Contains("Attached images", prompt);
        Assert.Contains(@"[Image #1] C:\shots\one.png", prompt);
    }

    [Fact]
    public void A_goal_without_images_says_nothing_about_them()
    {
        // The block is fixed overhead in four prompts that are already fitted to a command line, so a
        // goal that pasted nothing must not pay for the heading, the fence and the instruction.
        var prompt = new GoalWorkflowEngine().BuildPlanPrompt();

        Assert.DoesNotContain("Attached images", prompt);
    }

    [Fact]
    public void The_images_come_back_with_a_reloaded_session()
    {
        var engine = WithImages("fix [Image #1]", @"C:\shots\one.png");

        var reloaded = new GoalWorkflowEngine();
        reloaded.LoadFrom(engine.ToState([], "claude-instance", ""));

        // Through the file, not just through the object: a resumed run whose markers no longer resolve
        // sends the tool to open a path the prompt has stopped naming.
        var written = JsonSerializer.Serialize(engine.ToState([], "claude-instance", ""), JsonDefaults.Options);
        var read = JsonSerializer.Deserialize<GoalTileState>(written, JsonDefaults.Options)!;

        Assert.Equal(@"C:\shots\one.png", reloaded.AttachedImages.Single().Path);
        Assert.Equal(1, read.AttachedImages.Single().Index);
        Assert.Equal(@"C:\shots\one.png", read.AttachedImages.Single().Path);
    }

    [Fact]
    public void A_null_in_the_saved_list_is_dropped_rather_than_thrown_over()
    {
        // The same rule every collection in the state file follows: one null out of a hand-edited file
        // must not be punished more harshly than a file of corrupt bytes, which is at least set aside.
        var state = JsonSerializer.Deserialize<GoalTileState>(
            """{"AttachedImages":[null,{"Index":1,"Path":null}]}""", JsonDefaults.Options)!;

        Assert.Equal("", Assert.Single(state.AttachedImages).Path);
    }

    // ── A marker the user was in the middle of deleting ─

    /// <summary>
    /// A marker taken apart with backspace stops naming its image, so the image is not carried.
    /// </summary>
    /// <remarks>
    /// The case a user actually produces: three images pasted, the third half rubbed out, Send pressed.
    /// The match is on the whole marker — the brackets included — so <c>[Image #4</c> is not
    /// <c>[Image #4]</c> and nothing keeps image 4 in the run. What must not happen is the opposite: a
    /// path in the prompt for a picture the goal no longer mentions.
    /// </remarks>
    [Theory]
    [InlineData("[Image #2] [Image #3] [Image #4")]      // closing bracket gone
    [InlineData("[Image #2] [Image #3] [Image #")]       // and the number with it
    [InlineData("[Image #2] [Image #3] [Image 4]")]      // the hash gone
    [InlineData("[Image #2] [Image #3] Image #4]")]      // the opening bracket gone
    public void A_half_deleted_marker_does_not_carry_its_image(string goal)
    {
        var engine = new GoalWorkflowEngine();
        engine.AttachImage(@"C:/shots/two.png");       // #1 — renumbered below
        engine.AttachImage(@"C:/shots/three.png");
        engine.AttachImage(@"C:/shots/four.png");

        // The markers the goal above uses are #2 #3 #4, so the run starts from a list numbered to match.
        engine.AttachedImages.Clear();
        engine.AttachedImages.AddRange([
            new GoalImageAttachment { Index = 2, Path = @"C:/shots/two.png" },
            new GoalImageAttachment { Index = 3, Path = @"C:/shots/three.png" },
            new GoalImageAttachment { Index = 4, Path = @"C:/shots/four.png" },
        ]);

        engine.StartNewGoal(goal);

        Assert.Equal([2, 3], engine.AttachedImages.Select(i => i.Index).ToList());

        var prompt = engine.BuildClarifyPrompt();

        Assert.Contains(@"C:/shots/three.png", prompt);
        Assert.DoesNotContain(@"C:/shots/four.png", prompt);
    }

    /// <summary>
    /// A number freed by a broken marker is handed out again, and that is harmless.
    /// </summary>
    /// <remarks>
    /// <para>Dropping image 4 leaves the list at 2 and 3, so the next paste is numbered 4 once more —
    /// while the wreckage of the old one, <c>[Image #4</c>, is still sitting in the text. The two do not
    /// collide: the wreckage is not a marker, because every place that reads one matches the closing
    /// bracket too, so nothing resolves it and the number now belongs to the new picture alone.</para>
    /// <para>Pinned rather than left to inspection: the alternative — never reusing a number — was
    /// tried here and is the wrong rule, since <c>AttachImage</c> counts from the highest still in the
    /// list and a run that drops its last image would otherwise climb for ever.</para>
    /// </remarks>
    [Fact]
    public void A_number_freed_by_a_broken_marker_belongs_to_the_next_image()
    {
        var engine = new GoalWorkflowEngine();
        engine.AttachedImages.AddRange([
            new GoalImageAttachment { Index = 2, Path = "a.png" },
            new GoalImageAttachment { Index = 3, Path = "b.png" },
            new GoalImageAttachment { Index = 4, Path = "old.png" },
        ]);

        engine.StartNewGoal("[Image #2] [Image #3] [Image #4");

        Assert.Equal("[Image #4]", engine.AttachImage("new.png"));

        // The number came back; the file it used to name did not.
        Assert.DoesNotContain("old.png", engine.AttachedImages.Select(i => i.Path));
        Assert.Contains("new.png", engine.AttachedImages.Select(i => i.Path));
    }

    // ── A save that fails ───────────────────────────────

    /// <summary>
    /// A picture that could not be written leaves no marker behind.
    /// </summary>
    /// <remarks>
    /// The rule the view model calls the trap, and the reason it is one: a marker whose file was never
    /// written is a marker the tool is told to open, so the run spends an attempt on a picture that does
    /// not exist. Said in the transcript instead, where it is something the user can act on.
    /// </remarks>
    [Fact]
    public void A_save_that_fails_inserts_no_marker()
    {
        OnUiThread(async () =>
        {
            GoalImageStore.Factory = _ => throw new IOException("the disk is full");

            using var vm = NewTile();
            vm.InputText = "make it green";
            vm.InputCaretIndex = vm.InputText.Length;

            await vm.AttachImageCommand.ExecuteAsync(new byte[] { 1, 2, 3 });

            Assert.Equal("make it green", vm.InputText);
            Assert.DoesNotContain("[Image #", vm.InputText, StringComparison.Ordinal);
            Assert.Contains(vm.Messages, m => m.Text.Contains("could not be saved", StringComparison.Ordinal));
        });
    }

    /// <summary>A picture that was written leaves one, where the caret was.</summary>
    [Fact]
    public void A_save_that_works_inserts_the_marker_at_the_caret()
    {
        OnUiThread(async () =>
        {
            GoalImageStore.Factory = _ => @"C:\shots\one.png";

            using var vm = NewTile();
            vm.InputText = "make  green";
            vm.InputCaretIndex = 5;   // between the two spaces

            await vm.AttachImageCommand.ExecuteAsync(new byte[] { 1, 2, 3 });

            Assert.Equal("make [Image #1]  green", vm.InputText);
        });
    }
}