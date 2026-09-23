using mTiles.AgentSessions;
using mTiles.AgentSessions.Events;
using mTiles.Models;

namespace mTiles.ViewModels.AgentConversation;

/// <summary>An image waiting in the composer, and the marker that says where in the message it stands.</summary>
/// <param name="Index">The number in its marker. Kept while the message is being written — so removing
/// <c>#2</c> does not rename <c>#3</c> under the user's caret — and renumbered from one when it is sent.</param>
public sealed record ComposerImage(int Index, ImageAttachment Image) : INumberedImage
{
    public string Marker => ImageMarkers.For(Index);
}
