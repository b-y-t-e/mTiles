using Avalonia;
using Avalonia.Media.Fonts;

namespace mTiles.Services;

/// <summary>
/// The typeface this application brings with it, so it reads the same on a machine that has none.
/// </summary>
/// <remarks>
/// <para><b>Compiled in, not installed.</b> Six faces of JetBrains Mono are Avalonia resources inside
/// the executable and are registered here as a font collection; nothing is written to the user's
/// system, nothing has to be installed, and there is no difference between Windows and Linux to keep
/// working. Installing a font is the alternative, and it is worse in every direction: it needs a
/// per-platform installer step, it asks for a privilege this application does not otherwise want, and
/// it leaves something behind after an uninstall.</para>
/// <para><b>The key is why the family is spelled with a prefix.</b> A collection is registered under a
/// URI, and <c>fonts:JetBrainsMono#JetBrains Mono</c> is how a font family names it — the same shape
/// Avalonia's own Inter package uses, which is the precedent this follows rather than invents. A bare
/// "JetBrains Mono" would find the font only on a machine where it happens to be installed, which is
/// the whole thing being avoided.</para>
/// <para>The licence files do <b>not</b> live beside the fonts: the collection loads every asset in
/// its directory as a typeface, so a <c>.txt</c> in there is a font that fails to parse. They are in
/// <c>licenses/JetBrainsMono/</c>, and the OFL text itself travels in
/// <c>THIRD-PARTY-NOTICES.md</c>, which ships.</para>
/// </remarks>
public static class AppFonts
{
    /// <summary>The prefix a font family uses to name the embedded collection.</summary>
    public const string Key = "fonts:JetBrainsMono";

    /// <summary>The family as it must be written to reach the embedded copy.</summary>
    public const string JetBrainsMono = $"{Key}#JetBrains Mono";

    /// <summary>Where the faces live inside the assembly.</summary>
    public const string Assets = "avares://mTiles/Assets/Fonts/JetBrainsMono";

    /// <summary>Registers the embedded faces with the font manager.</summary>
    /// <remarks>Called from <c>Program.BuildAvaloniaApp</c> and from the test application, so what the
    /// tests resolve is what ships. Fonts can only be registered while the application is being built,
    /// which is why this is an <see cref="AppBuilder"/> step and not something a view can ask for.
    /// </remarks>
    public static AppBuilder WithJetBrainsMono(this AppBuilder builder) =>
        builder.ConfigureFonts(fonts => fonts.AddFontCollection(
            new EmbeddedFontCollection(new Uri(Key, UriKind.Absolute), new Uri(Assets, UriKind.Absolute))));
}
