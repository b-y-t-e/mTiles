using System.Runtime.CompilerServices;

namespace mTiles.Tests;

/// <summary>
/// The repository's own source files, read once per run for every test that scans them.
/// </summary>
/// <remarks>
/// <para>Five tests read every <c>.cs</c> file in the repository and three more read the markup, each
/// from disk on its own — close to four thousand file reads a run for the same few hundred files.</para>
/// <para>The root comes from this file's compile-time path, not <c>AppContext.BaseDirectory</c>: the
/// output directory moves (a build redirected with <c>-o</c> lands outside the repository), while
/// <c>CallerFilePath</c> is the source tree being built, on a developer's machine and a build agent
/// alike.</para>
/// </remarks>
internal static class RepoSources
{
    /// <summary>One file: where it is and what is in it.</summary>
    internal sealed class SourceFile(string path, string text)
    {
        private string[]? _lines;

        public string Path { get; } = path;
        public string Name => System.IO.Path.GetFileName(Path);
        public string Text { get; } = text;

        /// <summary>Split the way <see cref="File.ReadLines(string)"/> splits.</summary>
        public IReadOnlyList<string> Lines => _lines ??= SplitLines(Text);

        private static string[] SplitLines(string text)
        {
            var lines = new List<string>();
            using var reader = new StringReader(text);
            while (reader.ReadLine() is { } line) lines.Add(line);
            return [.. lines];
        }
    }

    /// <summary>The repository root.</summary>
    public static string Root { get; } = FindRoot();

    /// <summary>The application project, <c>src/mTiles</c>.</summary>
    public static string App { get; } = Path.Combine(Root, "src", "mTiles");

    private static readonly Lazy<IReadOnlyList<SourceFile>> AllCSharp = new(() =>
        [.. new[] { "src", "tests" }.SelectMany(d => Read(Path.Combine(Root, d), "*.cs"))]);

    private static readonly Lazy<IReadOnlyList<SourceFile>> AppMarkup = new(() => [.. Read(App, "*.axaml")]);

    /// <summary>Every <c>.cs</c> file under <c>src</c> and <c>tests</c>.</summary>
    public static IReadOnlyList<SourceFile> CSharp => AllCSharp.Value;

    /// <summary>Every <c>.cs</c> file under <c>src</c>.</summary>
    public static IEnumerable<SourceFile> SrcCSharp => Under(Path.Combine(Root, "src"));

    /// <summary>Every <c>.cs</c> file of the application project.</summary>
    public static IEnumerable<SourceFile> AppCSharp => Under(App);

    /// <summary>Every <c>.axaml</c> file of the application project.</summary>
    public static IReadOnlyList<SourceFile> AppAxaml => AppMarkup.Value;

    /// <summary>One file of the application project, by its path relative to <c>src/mTiles</c>.</summary>
    public static SourceFile AppFile(string relative)
    {
        var full = Path.GetFullPath(Path.Combine(App, relative.Replace('/', Path.DirectorySeparatorChar)));
        return AppAxaml.Concat(CSharp).FirstOrDefault(f => string.Equals(f.Path, full, StringComparison.OrdinalIgnoreCase))
               ?? new SourceFile(full, File.ReadAllText(full));
    }

    private static IEnumerable<SourceFile> Under(string directory)
    {
        var prefix = directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return CSharp.Where(f => f.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<SourceFile> Read(string directory, string pattern)
    {
        var obj = $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}";
        var bin = $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}";

        return Directory.EnumerateFiles(directory, pattern, SearchOption.AllDirectories)
            .Where(f => !f.Contains(obj) && !f.Contains(bin))
            .Select(f => Path.GetFullPath(f))
            .Select(f => new SourceFile(f, File.ReadAllText(f)));
    }

    private static string FindRoot([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", ".."));
}
