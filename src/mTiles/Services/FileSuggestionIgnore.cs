using System.Text.RegularExpressions;

namespace mTiles.Services;

/// <summary>
/// The <c>.ignore</c> / <c>.rgignore</c> rules a workspace carries, as one question: is this path out?
/// </summary>
/// <remarks>
/// <para>ripgrep's own files, honoured alongside <c>.gitignore</c> as tools of this kind do. They
/// exist for exactly this case — "git tracks it, but do not
/// show it to me" — so a user who has written one has already answered the question this popup asks,
/// and answering it differently is the application overruling them.</para>
/// <para><b>A subset of the gitignore syntax, and a stated one.</b> Comments, blank lines, negation
/// with <c>!</c>, a leading <c>/</c> anchoring to the file's own directory, a trailing <c>/</c> meaning
/// directories only, and the <c>*</c> <c>?</c> <c>**</c> globs. Not supported: character classes
/// (<c>[a-z]</c>) and escapes. The unsupported forms are rare in these files, and a pattern this does
/// not understand is one that matches nothing — so the failure is that something is offered which the
/// user did not want to see, never that a file they were reaching for has silently disappeared.</para>
/// </remarks>
public sealed class FileSuggestionIgnore
{
    /// <summary>The files read, in the order ripgrep reads them.</summary>
    private static readonly string[] FileNames = [".ignore", ".rgignore"];

    private readonly List<Rule> _rules;

    private FileSuggestionIgnore(List<Rule> rules) => _rules = rules;

    /// <summary>Nothing is ignored — what a workspace with neither file gets.</summary>
    public static FileSuggestionIgnore None { get; } = new([]);

    public bool IsEmpty => _rules.Count == 0;

    /// <summary>
    /// Reads whatever of the two files is there, or <see cref="None"/> when neither is.
    /// </summary>
    /// <remarks>Never throws: an unreadable ignore file is a workspace with fewer rules, not a tile
    /// that fails to offer anything.</remarks>
    public static FileSuggestionIgnore Read(string directory)
    {
        var rules = new List<Rule>();

        foreach (var name in FileNames)
        {
            try
            {
                var path = Path.Combine(directory, name);
                if (!File.Exists(path)) continue;

                foreach (var line in File.ReadAllLines(path))
                    if (Rule.Parse(line) is { } rule)
                        rules.Add(rule);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Fewer rules, not no suggestions.
            }
        }

        return rules.Count == 0 ? None : new FileSuggestionIgnore(rules);
    }

    /// <summary>
    /// Whether a path — relative, <c>/</c>-separated, a folder ending in <c>/</c> — is ignored.
    /// </summary>
    /// <remarks>
    /// <b>The last rule that matches wins</b>, which is what makes <c>!</c> mean anything: a negation
    /// only ever un-ignores what an earlier line ignored, so the rules are walked to the end rather
    /// than stopped at the first hit.
    /// </remarks>
    public bool Ignores(string path)
    {
        if (_rules.Count == 0) return false;

        var isDirectory = path.EndsWith('/');
        var trimmed = isDirectory ? path[..^1] : path;

        var ignored = false;
        foreach (var rule in _rules)
            if (rule.Matches(trimmed, isDirectory))
                ignored = !rule.Negated;

        return ignored;
    }

    /// <summary>One line of an ignore file, compiled.</summary>
    private sealed class Rule
    {
        private readonly Regex _pattern;
        private readonly bool _directoriesOnly;
        internal bool Negated { get; }

        private Rule(Regex pattern, bool negated, bool directoriesOnly)
        {
            _pattern = pattern;
            Negated = negated;
            _directoriesOnly = directoriesOnly;
        }

        /// <summary>The rule a line carries, or null for a blank line or a comment.</summary>
        internal static Rule? Parse(string line)
        {
            var text = line.Trim();
            if (text.Length == 0 || text.StartsWith('#')) return null;

            var negated = text.StartsWith('!');
            if (negated) text = text[1..];

            var directoriesOnly = text.EndsWith('/');
            if (directoriesOnly) text = text[..^1];

            // Anchored either by a leading slash or by having one anywhere inside: `docs/notes` is a
            // path from the root, while a bare `notes` is a name to look for at any depth. Gitignore's
            // rule, and the one users write these files expecting.
            // A leading `**/` says "at any depth, this level included", so it is the unanchored form
            // spelled out — git's reading, and the commonest idiom in these files. Left to the general
            // rule it counted as anchored (it holds a separator) and compiled to `^.*/name`, which
            // demands at least one directory: `**/vendor.js` then missed `vendor.js` at the root, the
            // one place people most expect it to bite.
            var anyDepth = text.StartsWith("**/", StringComparison.Ordinal);
            if (anyDepth) text = text[3..];

            var anchored = !anyDepth
                           && (text.StartsWith('/') || text.TrimEnd('/').Contains('/'));
            text = text.TrimStart('/');

            if (text.Length == 0) return null;

            return new Rule(Compile(text, anchored), negated, directoriesOnly);
        }

        /// <summary>
        /// Whether this rule covers the path, or any directory above it.
        /// </summary>
        /// <remarks>
        /// The parents matter: an ignore file saying <c>build/</c> is ignoring everything under it, and
        /// nothing else here walks the tree to notice that.
        /// </remarks>
        internal bool Matches(string path, bool isDirectory)
        {
            if (!_directoriesOnly && _pattern.IsMatch(path)) return true;

            // Every prefix that names a directory, longest first — and the path itself when it is one.
            if (isDirectory && _pattern.IsMatch(path)) return true;

            for (var cut = path.LastIndexOf('/'); cut > 0; cut = path.LastIndexOf('/', cut - 1))
                if (_pattern.IsMatch(path[..cut]))
                    return true;

            return false;
        }

        /// <summary>
        /// Whether these rules care about case.
        /// </summary>
        /// <remarks>
        /// <para>Following the filesystem, which is what both git and ripgrep do: git sets
        /// <c>core.ignorecase</c> on Windows and macOS and leaves it off on Linux, and ripgrep matches
        /// its ignore files the same way.</para>
        /// <para>It was <see cref="RegexOptions.IgnoreCase"/> everywhere, and on Linux that is the one
        /// failure this class promises not to have: an entry of <c>Build/</c> also hid <c>build/</c>,
        /// so a file the user had said nothing about disappeared from the list with nothing on screen
        /// to say why. Erring the other way — offering something they meant to hide — is the direction
        /// stated at the top of this file, and it is the one to keep.</para>
        /// </remarks>
        private static readonly RegexOptions CaseRule =
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                ? RegexOptions.IgnoreCase
                : RegexOptions.None;

        /// <summary>The glob as a regular expression, anchored to the whole string.</summary>
        /// <remarks>
        /// <c>**</c> spans separators, a single <c>*</c> does not, and <c>?</c> is one character that is
        /// not a separator — ripgrep's reading of the syntax, which is git's. An unanchored pattern is
        /// allowed to start after any separator, which is how a bare name matches at any depth.
        /// </remarks>
        private static Regex Compile(string glob, bool anchored)
        {
            var pattern = new System.Text.StringBuilder(anchored ? "^" : @"^(?:.*/)?");

            for (var i = 0; i < glob.Length; i++)
            {
                switch (glob[i])
                {
                    // `/**/` matches *zero* or more directories, so `a/**/b` covers `a/b` as well as
                    // `a/x/y/b`. Compiled as `.*` between two literal slashes it demanded at least one,
                    // and `a/b` fell through — a file the user had ignored quietly staying on the list,
                    // which is the direction of failure this class says it does not take. Git's rule,
                    // and the only place the two stars mean something a single star does not.
                    case '/' when i + 3 < glob.Length && glob.AsSpan(i, 4) is "/**/":
                        pattern.Append("/(?:.*/)?");
                        i += 3;
                        break;
                    case '*' when i + 1 < glob.Length && glob[i + 1] == '*':
                        pattern.Append(".*");
                        i++;
                        break;
                    case '*':
                        pattern.Append("[^/]*");
                        break;
                    case '?':
                        pattern.Append("[^/]");
                        break;
                    default:
                        pattern.Append(Regex.Escape(glob[i].ToString()));
                        break;
                }
            }

            // Anything under a match is under it: `build` covers `build/x/y.cs`.
            pattern.Append("(?:/.*)?$");

            return new Regex(pattern.ToString(), RegexOptions.CultureInvariant | CaseRule);
        }
    }
}
