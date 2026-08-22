namespace mTiles.Models;

/// <summary>Dictating into a tile from a phone or another browser on the network.</summary>
public sealed class PhoneSettings
{
    /// <summary>
    /// Whether the bridge keeps listening once the QR panel is closed.
    /// </summary>
    /// <remarks>
    /// Off by default, and that default is the security posture rather than a preference. Every other
    /// server in this application listens on loopback only; this one has to accept connections from the
    /// network to be of any use, so it runs when the user has asked for it and not merely because the
    /// application is open. With it off the panel still works — showing the QR code starts the bridge and
    /// closing it stops it again, once no phone is still paired.
    /// </remarks>
    public bool Enabled { get; set; }

    /// <summary>
    /// The port the bridge prefers. Zero means "whichever one is free".
    /// </summary>
    /// <remarks>
    /// A preference, not a demand — the bridge falls back to a free port when this one cannot be bound.
    /// That is not defensive: on Windows the kernel reserves blocks of ports for Hyper-V, WSL and Docker
    /// at boot, and a port inside one can never be bound however free it looks. 18091 landed inside such
    /// a block on the first machine this ran on. Nobody types this number anywhere — the QR code carries
    /// it — so defending it at the cost of the feature would be the wrong way round.
    /// </remarks>
    public int Port { get; set; } = 18091;

    /// <summary>
    /// The address a phone last reached this machine at, keyed by the kind of session it happened in.
    /// </summary>
    /// <remarks>
    /// Keyed, rather than a single remembered winner, because one machine is used both ways: sitting at
    /// it, the LAN address is the answer; connected to it over Remote Desktop, only a tunnel can reach the
    /// phone in the user's hand. The machine is identical in both cases, so a single pin would have each
    /// day's answer overwrite the other's, and the ranking would be wrong every time the user switched.
    /// Keys are <c>SessionLocation</c> names; an unrecognised key is simply never matched.
    /// </remarks>
    public Dictionary<string, string> PinnedHosts
    {
        get => _pinnedHosts;
        set => _pinnedHosts = value ?? [];
    }
    private Dictionary<string, string> _pinnedHosts = [];

    /// <summary>Whether transcription from a phone presses Enter, independently of the local setting.</summary>
    /// <remarks>
    /// Its own switch because the gesture is not the same one. At the keyboard the user is looking at the
    /// terminal and can see what landed there before pressing Enter themselves; holding a phone they are
    /// often not looking at the screen at all — which is exactly why pressing Enter for them is worth
    /// offering, and exactly why it is not the default. Running a command in a terminal from a device the
    /// user is not watching is not something to opt anybody into; the switch exists so they can choose it
    /// knowingly, for the gesture where it helps.
    /// <para>An earlier version of this note argued that submitting was "the useful default there" while
    /// leaving the default off. The note was wrong, not the default.</para>
    /// </remarks>
    public bool AutoSubmitEnter { get; set; }
}
