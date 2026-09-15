using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using mTiles.Views;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// A tile drag asks the surfaces under the pointer innermost first, and a surface that does not answer
/// leaves the drag to the one around it.
/// </summary>
/// <remarks>That order used to be the platform's event bubbling and is now a loop of our own, so it is
/// the one thing that can quietly stop working: a workspace's surface that answers false and ends the
/// walk anyway leaves a window-level tile impossible to drop anywhere inside a workspace.</remarks>
public class TileDragSessionWalkTests
{
    private static void OnUiThread(Action body)
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(TileDragSessionWalkTests).Assembly);
        session.Dispatch(() => { body(); return Task.FromResult(true); }, CancellationToken.None)
            .GetAwaiter().GetResult();
    }

    /// <summary>A surface that answers as it is told and writes down that it was asked.</summary>
    private sealed class RecordingSurface(string name, bool answers, List<string> asked) : Border, ITileDragSurface
    {
        public bool DragOver(Visual relativeTo, Point point) => Record("over");

        public bool Drop(Visual relativeTo, Point point) => Record("drop");

        private bool Record(string question)
        {
            asked.Add($"{question}:{name}");
            return answers;
        }
    }

    /// <summary>An outer surface holding an inner one holding what the pointer hits; answers the hit.</summary>
    private static Border NestedSurfaces(bool innerAnswers, bool outerAnswers, List<string> asked)
    {
        var hit = new Border();
        var inner = new RecordingSurface("inner", innerAnswers, asked) { Child = hit };
        _ = new RecordingSurface("outer", outerAnswers, asked) { Child = inner };
        return hit;
    }

    private static readonly Point Anywhere = new(10, 10);

    [Fact]
    public void An_inner_surface_that_does_not_answer_leaves_the_drag_to_the_outer_one() => OnUiThread(() =>
    {
        var asked = new List<string>();
        var hit = NestedSurfaces(innerAnswers: false, outerAnswers: true, asked);

        TileDragSession.Over(hit, hit, Anywhere);
        TileDragSession.Drop(hit, hit, Anywhere);

        Assert.Equal(["over:inner", "over:outer", "drop:inner", "drop:outer"], asked);
    });

    [Fact]
    public void An_inner_surface_that_answers_ends_the_walk() => OnUiThread(() =>
    {
        var asked = new List<string>();
        var hit = NestedSurfaces(innerAnswers: true, outerAnswers: true, asked);

        TileDragSession.Over(hit, hit, Anywhere);
        TileDragSession.Drop(hit, hit, Anywhere);

        Assert.Equal(["over:inner", "drop:inner"], asked);
    });
}
