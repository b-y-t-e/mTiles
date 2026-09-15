using System.Diagnostics;
using Avalonia;
using Avalonia.Platform;

namespace mTiles.Services;

/// <summary>
/// How the window is drawn on Windows: which renderer, which composition path and which GPU — with an
/// environment override for each, so the three can be tried against each other without a rebuild.
/// </summary>
/// <remarks>
/// <para>Avalonia's defaults are ANGLE (Direct3D 11) through WinUI composition on whichever adapter
/// DXGI lists first. On a hybrid laptop that is not necessarily the GPU driving the display, and a frame
/// rendered on one adapter and scanned out by another is copied across on every frame — which is felt as
/// the whole interface lagging behind the pointer, and is invisible on Linux, where the compositor
/// renders on the display's own GPU.</para>
/// <para><c>MTILES_RENDERING</c>: <c>angle</c>, <c>wgl</c>, <c>vulkan</c>, <c>software</c>.
/// <c>MTILES_COMPOSITION</c>: <c>winui</c>, <c>dcomp</c>, <c>lowlatency</c>, <c>redirection</c>.
/// <c>MTILES_GPU</c>: part of an adapter's name (<c>amd</c>, <c>nvidia</c>) or its index. Anything not
/// recognised is ignored and the default stands; every choice is written to the log.</para>
/// <para><b>Two of the three only apply under ANGLE, and saying so is the whole point of
/// <see cref="ExplainGpuSelection"/>.</b> Avalonia 12.1.2 documents
/// <c>GraphicsAdapterSelectionCallback</c> as called "only for AngleEgl rendering mode when DirectX 11
/// is used", and <c>Win32CompositionMode</c> likewise only has an effect there. So
/// <c>MTILES_RENDERING=vulkan</c> together with <c>MTILES_GPU=amd</c> renders on the default adapter and
/// the callback never runs — and with the adapter list written from inside that callback, the log would
/// carry not one line about GPUs. That is exactly the combination somebody reaches for to rule out a
/// lag coming from the wrong card, so the mismatch is stated in the log <em>before</em> any frame is
/// drawn rather than left to be inferred from a silence.</para>
/// </remarks>
internal static class Win32Rendering
{
    public static Win32PlatformOptions Options()
    {
        var options = new Win32PlatformOptions();

        var rendering = Parse(Environment.GetEnvironmentVariable("MTILES_RENDERING"), RenderingModes);
        if (rendering is { } mode)
            options.RenderingMode = [mode, Win32RenderingMode.Software];

        if (Parse(Environment.GetEnvironmentVariable("MTILES_COMPOSITION"), CompositionModes) is { } composition)
            options.CompositionMode = [composition, Win32CompositionMode.RedirectionSurface];

        // Only when asked for: without it Avalonia picks the adapter itself, and a machine nobody is
        // experimenting on must render exactly as it did before this class existed.
        var gpu = Environment.GetEnvironmentVariable("MTILES_GPU");
        if (!string.IsNullOrWhiteSpace(gpu))
            options.GraphicsAdapterSelectionCallback = adapters => SelectAdapter(adapters, gpu);

        Trace.TraceInformation(
            "Win32 rendering: {0} via {1}",
            string.Join(",", options.RenderingMode),
            string.Join(",", options.CompositionMode ?? []));
        Trace.TraceInformation(ExplainGpuSelection(gpu, options.RenderingMode[0]));
        return options;
    }

    /// <summary>What the machine will actually do with <c>MTILES_GPU</c>, said in one line.</summary>
    /// <remarks>The adapter list itself can only be written from inside the callback, and the callback
    /// runs under ANGLE alone — so where it will not run, this line is the only account there is.</remarks>
    internal static string ExplainGpuSelection(string? gpu, Win32RenderingMode rendering)
    {
        var asked = !string.IsNullOrWhiteSpace(gpu);
        if (rendering is not Win32RenderingMode.AngleEgl)
            return asked
                ? $"GPU: MTILES_GPU={gpu} is ignored under {rendering} — the adapter is chosen only for " +
                  "AngleEgl on Direct3D 11, as is MTILES_COMPOSITION. Drop MTILES_RENDERING to use it."
                : $"GPU: not selectable under {rendering} — the default adapter is used.";

        return asked
            ? $"GPU: MTILES_GPU={gpu}; the adapters are listed below as ANGLE asks for one."
            : "GPU: no MTILES_GPU set; Avalonia chooses the adapter itself.";
    }

    private static readonly Dictionary<string, Win32RenderingMode> RenderingModes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["angle"] = Win32RenderingMode.AngleEgl,
        ["wgl"] = Win32RenderingMode.Wgl,
        ["vulkan"] = Win32RenderingMode.Vulkan,
        ["software"] = Win32RenderingMode.Software,
    };

    private static readonly Dictionary<string, Win32CompositionMode> CompositionModes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["winui"] = Win32CompositionMode.WinUIComposition,
        ["dcomp"] = Win32CompositionMode.DirectComposition,
        ["lowlatency"] = Win32CompositionMode.LowLatencyDxgiSwapChain,
        ["redirection"] = Win32CompositionMode.RedirectionSurface,
    };

    private static T? Parse<T>(string? value, Dictionary<string, T> table) where T : struct =>
        value is not null && table.TryGetValue(value.Trim(), out var mode) ? mode : null;

    /// <summary>The adapter to render on: the one <c>MTILES_GPU</c> names, or the first when it names none.</summary>
    private static int SelectAdapter(IReadOnlyList<PlatformGraphicsDeviceAdapterDescription> adapters, string? wanted)
    {
        var names = adapters.Select(adapter => adapter.Description).ToList();
        var choice = ChooseAdapter(names, wanted);

        if (choice.Problem is { } problem)
            Trace.TraceWarning("GPU: {0}", problem);
        for (var i = 0; i < names.Count; i++)
            Trace.TraceInformation("GPU adapter {0}{1}: {2}", i, i == choice.Index ? " (used)" : "", names[i]);
        return choice.Index;
    }

    /// <summary>Which adapter <paramref name="wanted"/> names, and why the first is used when it names none.</summary>
    /// <remarks><b>A number is an index and nothing else.</b> Read as a name once out of range, <c>1</c> on a
    /// one-adapter machine would pick whichever card has a 1 in its model number — an answer to a question
    /// nobody asked, in exactly the experiment meant to rule out the wrong card.</remarks>
    internal static AdapterChoice ChooseAdapter(IReadOnlyList<string> adapters, string? wanted)
    {
        if (string.IsNullOrWhiteSpace(wanted)) return new AdapterChoice(0);
        var asked = wanted.Trim();

        if (int.TryParse(asked, out var index))
            return index >= 0 && index < adapters.Count
                ? new AdapterChoice(index)
                : new AdapterChoice(0, $"MTILES_GPU={asked} is not an adapter index (0..{adapters.Count - 1}); the first is used.");

        for (var i = 0; i < adapters.Count; i++)
            if (adapters[i].Contains(asked, StringComparison.OrdinalIgnoreCase))
                return new AdapterChoice(i);

        return new AdapterChoice(0, $"MTILES_GPU={asked} matches no adapter's name; the first is used.");
    }

    /// <param name="Index">The adapter rendered on.</param>
    /// <param name="Problem">Why that is not the one asked for, or null when it is.</param>
    internal readonly record struct AdapterChoice(int Index, string? Problem = null);
}
