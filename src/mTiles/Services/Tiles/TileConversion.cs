using mTiles.Models;

namespace mTiles.Services.Tiles;

/// <summary>
/// What a user loses by turning a tile into another kind, said in one sentence.
/// </summary>
/// <remarks>
/// <para>A judgement rather than a mechanism, which is why it is pure and lives on its own: what
/// survives a conversion is different for every kind — a shell and its whole tree of child processes
/// die, a note's file does not — and one sentence for all of them would be a lie in six directions.
/// The table it states is the safety analysis in <c>docs/TILE-KIND-CHANGE.md</c> §2.</para>
/// <para>A kind this file has never heard of gets the general sentence rather than an exception: the
/// registry is open, so a kind registered by later code has to be convertible — only without a promise
/// about what it leaves behind.</para>
/// </remarks>
public static class TileConversion
{
    /// <summary>Whether converting away from this kind ends something that cannot be brought back.</summary>
    /// <remarks>The live shell or agent process, and nothing else: every other kind leaves its work on
    /// disk, where a tile pointing at it again would find it. It is the first half of the sentence
    /// <see cref="Warning"/> puts to the user rather than a second table beside it — stated twice, a
    /// kind added to one of them would be promised something the other never checked.</remarks>
    public static bool DestroysWork(string? kindId) =>
        kindId is TileKindIds.Terminal or TileKindIds.Agent;

    /// <summary>The question to put before a tile of <paramref name="currentKindId"/> becomes a
    /// <paramref name="targetDisplayName"/>.</summary>
    public static string Warning(string? currentKindId, string targetDisplayName) =>
        $"Change this tile to {targetDisplayName}? {Consequence(currentKindId)}";

    /// <summary>What is ended, and then what is left behind — either half may be all there is to say.
    /// </summary>
    private static string Consequence(string? kindId) =>
        string.Join(' ', new[] { DestroysWork(kindId) ? Ended : null, WhatSurvives(kindId) }
            .Where(sentence => sentence is { Length: > 0 }));

    private static string? WhatSurvives(string? kindId) => kindId switch
    {
        TileKindIds.Terminal => null,
        TileKindIds.Agent => "The conversation stays with the agent; the tile will stop opening it.",
        TileKindIds.Note => Kept("notes"),
        TileKindIds.Todo => Kept("todos"),
        TileKindIds.Goal => "The run will be paused, and its record stays in .mtiles/goals/.",
        TileKindIds.Git or TileKindIds.Database or TileKindIds.Usage =>
            "Nothing from this tile will be lost.",
        _ => "Whatever this tile is holding will be replaced.",
    };

    private const string Ended = "The shell and everything running in it will be ended.";

    private static string Kept(string folder) =>
        $"The file stays in .mtiles/{folder}/; the tile will stop pointing at it.";
}
