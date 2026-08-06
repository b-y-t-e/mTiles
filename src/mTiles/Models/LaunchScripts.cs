namespace mTiles.Models;

/// <summary>
/// What a tile's profile says to run: the command to start with, and the one to try when it fails.
/// <para>A type rather than two loose strings because the pair decides which of two launch paths the
/// tile takes, and that rule lived in three places at once — two of which disagreed about whether a
/// script of nothing but spaces counts. It decided whether a tile ran its command or silently started
/// a bare shell.</para>
/// <para>Build it with <see cref="FromProfile"/>.</para>
/// </summary>
public sealed record LaunchScripts
{
    /// <summary>Command run first, or null when the profile has none. Blank is absent, normalised here
    /// rather than at the call sites so no way of building one — <c>with</c> included — can store a
    /// script of spaces that later reads as present.</summary>
    public string? Startup { get => _startup; init => _startup = Present(value); }

    /// <inheritdoc cref="Startup"/>
    public string? Fallback { get => _fallback; init => _fallback = Present(value); }

    private readonly string? _startup;
    private readonly string? _fallback;

    /// <summary>
    /// Whether this profile runs commands (<c>shell -c "…"</c>) rather than starting a shell
    /// interactively — which is exactly the question "does it name something to fall back to".
    /// <para>Derived, not stored. It was a third constructor parameter, and every caller passed the
    /// same expression; leaving it settable meant the type could hold combinations no profile can
    /// produce, and the tests that covered them were testing the type against itself.</para>
    /// </summary>
    public bool RunsCommandChain => Fallback is not null;

    /// <summary>Reads a profile's two scripts.</summary>
    public static LaunchScripts FromProfile(string? startup, string? fallback) =>
        new() { Startup = startup, Fallback = fallback };

    /// <summary>Nothing to run at all: what a tile without a profile launches.</summary>
    public static readonly LaunchScripts None = new();

    /// <summary>A script of nothing but whitespace is no script — the same test the chain applies when
    /// it builds its commands, so a blank one cannot open a chain with nothing in it.</summary>
    private static string? Present(string? script) => string.IsNullOrWhiteSpace(script) ? null : script;
}
