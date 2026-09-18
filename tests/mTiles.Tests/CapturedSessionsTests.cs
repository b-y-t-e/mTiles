using Xunit;
using mTiles.Services.Agents;

namespace mTiles.Tests;

/// <summary>
/// The register of which open tile holds which conversation, and the two rules a tile moving between
/// conversations depends on.
/// </summary>
public class CapturedSessionsTests
{
    [Fact]
    public void A_tile_that_moves_gives_up_the_conversation_it_left()
    {
        // A /clear takes tile A from X to Y; tile B then /resumes X, and has to be able to take it.
        var (tileA, tileB) = (Guid.NewGuid().ToString(), Guid.NewGuid().ToString());
        var (left, movedTo) = (Guid.NewGuid().ToString(), Guid.NewGuid().ToString());
        try
        {
            CapturedSessions.Claim(left, tileA);

            Assert.True(CapturedSessions.TryMoveTo(movedTo, tileA));

            Assert.True(CapturedSessions.TryClaim(left, tileB));
            Assert.False(CapturedSessions.TryClaim(movedTo, tileB));
        }
        finally
        {
            CapturedSessions.ReleaseAllOf(tileA);
            CapturedSessions.ReleaseAllOf(tileB);
        }
    }

    [Fact]
    public void A_tile_cannot_move_onto_a_conversation_another_tile_holds_and_keeps_its_own()
    {
        var (tileA, tileB) = (Guid.NewGuid().ToString(), Guid.NewGuid().ToString());
        var (own, theirs) = (Guid.NewGuid().ToString(), Guid.NewGuid().ToString());
        try
        {
            CapturedSessions.Claim(own, tileA);
            CapturedSessions.Claim(theirs, tileB);

            Assert.False(CapturedSessions.TryMoveTo(theirs, tileA));

            Assert.True(CapturedSessions.IsHeldByAnother(own, tileB));
            Assert.True(CapturedSessions.IsHeldByAnother(theirs, tileA));
        }
        finally
        {
            CapturedSessions.ReleaseAllOf(tileA);
            CapturedSessions.ReleaseAllOf(tileB);
        }
    }

    [Fact]
    public void Asking_whether_a_conversation_is_held_takes_nothing()
    {
        // The watcher asks this of a candidate before reading it; a read that then fails must not leave
        // the conversation held by a tile that never took it.
        var (tileA, tileB) = (Guid.NewGuid().ToString(), Guid.NewGuid().ToString());
        var candidate = Guid.NewGuid().ToString();
        try
        {
            Assert.False(CapturedSessions.IsHeldByAnother(candidate, tileA));

            Assert.True(CapturedSessions.TryClaim(candidate, tileB));
            Assert.False(CapturedSessions.IsHeldByAnother(candidate, tileB));
        }
        finally
        {
            CapturedSessions.ReleaseAllOf(tileA);
            CapturedSessions.ReleaseAllOf(tileB);
        }
    }
}
