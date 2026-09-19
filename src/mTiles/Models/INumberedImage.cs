namespace mTiles.Models;

/// <summary>An image a composer's text names by its marker — <c>[Image #n]</c> — whatever holds its bytes.</summary>
/// <remarks>What the one image strip both composers draw reads off each chip, so the Agent tile's pasted bytes
/// and the Goal tile's stored files share a chip without either becoming the other.</remarks>
public interface INumberedImage
{
    /// <summary>The number in the image's marker.</summary>
    int Index { get; }
}
