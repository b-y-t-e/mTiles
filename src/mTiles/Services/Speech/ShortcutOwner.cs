namespace mTiles.Services.Speech;

/// <summary>
/// Whatever already holds a shortcut, and whether the user can have it back.
/// </summary>
/// <param name="Name">What to call it on screen, as a noun phrase a sentence can start with —
/// <c>KRunner</c>, <c>The Start menu</c>.</param>
/// <param name="CanBeFreed">Whether there is somewhere to go and unbind it.</param>
/// <remarks>
/// <b>The second half is not decoration, it is the difference between advice and a wild goose chase.</b>
/// A shortcut KRunner holds is one line in the user's system settings; the Start menu on Windows is the
/// shell's and is not going anywhere, so telling somebody to take it back there would send
/// them looking for a screen that does not exist. The two therefore get different sentences, which is
/// also why this is a record rather than the string it started as: the answer had grown a second
/// question and was still being passed around as a name.
/// </remarks>
internal readonly record struct ShortcutOwner(string Name, bool CanBeFreed);
