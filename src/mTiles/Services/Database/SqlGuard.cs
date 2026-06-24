namespace mTiles.Services.Database;

public static class SqlGuard
{
    public static void Validate(string sql, bool allowModifications, SqlGuardProfile profile)
    {
        if (string.IsNullOrWhiteSpace(sql))
            throw new ArgumentException("Empty SQL query.");

        var stripped = StripLeadingComments(sql);
        var firstWord = GetFirstWord(stripped);

        if (profile.AlwaysBlockedFirstWords.Contains(firstWord))
            throw new UnauthorizedAccessException(
                $"Statement '{firstWord.ToUpperInvariant()}' is always blocked.");

        if (ContainsAlwaysBlockedKeyword(stripped, profile))
            throw new UnauthorizedAccessException(
                "Statement contains a keyword that is always blocked.");

        if (allowModifications)
            return;

        if (profile.BlockedFirstWords.Contains(firstWord))
            throw new UnauthorizedAccessException(
                $"Statement '{firstWord.ToUpperInvariant()}' is not allowed when modifications are disabled.");

        if (!profile.AllowedFirstWords.Contains(firstWord))
            throw new UnauthorizedAccessException(
                $"Statement '{firstWord.ToUpperInvariant()}' is not allowed when modifications are disabled.");

        var trimmedForSemicolon = stripped.TrimEnd();
        if (trimmedForSemicolon.Length > 0 && trimmedForSemicolon[^1] == ';')
            trimmedForSemicolon = trimmedForSemicolon[..^1];

        if (ContainsSemicolonOutsideStrings(trimmedForSemicolon, profile.SupportsDollarQuoting))
            throw new UnauthorizedAccessException(
                "Multiple statements are not allowed when modifications are disabled.");

        if (ContainsBlockedKeywordOutsideStrings(stripped, profile))
            throw new UnauthorizedAccessException(
                "Statement contains a blocked keyword and is not allowed when modifications are disabled.");

        if (string.Equals(firstWord, "SELECT", StringComparison.OrdinalIgnoreCase))
        {
            if (ContainsWordOutsideStrings(stripped, "INTO", profile.SupportsDollarQuoting))
                throw new UnauthorizedAccessException(
                    "SELECT INTO is not allowed when modifications are disabled.");
        }
    }

    private static bool ContainsAlwaysBlockedKeyword(string sql, SqlGuardProfile profile)
    {
        foreach (var keyword in profile.AlwaysBlockedAnywhere)
        {
            if (ContainsWordOutsideStrings(sql, keyword, profile.SupportsDollarQuoting))
                return true;
        }
        return false;
    }

    private static bool ContainsSemicolonOutsideStrings(string sql, bool supportsDollarQuoting) =>
        ScanOutsideStrings(sql, (s, i) => s[i] == ';', supportsDollarQuoting);

    private static bool ContainsBlockedKeywordOutsideStrings(string sql, SqlGuardProfile profile)
    {
        foreach (var keyword in profile.BlockedAnywhere)
        {
            if (ContainsWordOutsideStrings(sql, keyword, profile.SupportsDollarQuoting))
                return true;
        }
        return false;
    }

    private static bool ContainsWordOutsideStrings(string sql, string word, bool supportsDollarQuoting) =>
        ScanOutsideStrings(sql, (s, i) =>
            i + word.Length <= s.Length
            && (i == 0 || (!char.IsLetterOrDigit(s[i - 1]) && s[i - 1] != '_'))
            && string.Compare(s, i, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) == 0
            && (i + word.Length == s.Length || (!char.IsLetterOrDigit(s[i + word.Length]) && s[i + word.Length] != '_'))
        , supportsDollarQuoting);

    private delegate bool CharPredicate(string sql, int position);

    private static bool ScanOutsideStrings(string sql, CharPredicate match, bool supportsDollarQuoting)
    {
        bool inSingle = false;
        bool inDollar = false;
        string? dollarTag = null;

        for (int i = 0; i < sql.Length; i++)
        {
            char c = sql[i];

            if (inDollar)
            {
                int tagEnd = TryMatchDollarTag(sql, i, dollarTag!);
                if (tagEnd > 0)
                {
                    inDollar = false;
                    dollarTag = null;
                    i = tagEnd - 1;
                }
                continue;
            }

            if (inSingle)
            {
                if (c == '\'' && i + 1 < sql.Length && sql[i + 1] == '\'')
                    i++;
                else if (c == '\'')
                    inSingle = false;
                continue;
            }

            // Skip line comments before checking for string delimiters —
            // a bare ' inside -- comment would otherwise corrupt inSingle state
            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                while (i < sql.Length && sql[i] != '\n') i++;
                continue;
            }

            // Skip block comments before checking for string delimiters —
            // e.g. SELECT /* ' */ 1; DELETE would bypass ContainsSemicolonOutsideStrings
            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < sql.Length && !(sql[i] == '*' && sql[i + 1] == '/'))
                    i++;
                if (i + 1 < sql.Length) i++;
                continue;
            }

            if (c == '\'')
            {
                inSingle = true;
                continue;
            }

            if (supportsDollarQuoting && c == '$')
            {
                string? tag = TryReadDollarTag(sql, i);
                if (tag != null)
                {
                    inDollar = true;
                    dollarTag = tag;
                    i += tag.Length - 1;
                    continue;
                }
            }

            if (match(sql, i))
                return true;
        }

        return false;
    }

    private static string? TryReadDollarTag(string sql, int pos)
    {
        if (pos >= sql.Length || sql[pos] != '$') return null;
        int end = pos + 1;
        while (end < sql.Length && (char.IsLetterOrDigit(sql[end]) || sql[end] == '_'))
            end++;
        if (end < sql.Length && sql[end] == '$')
            return sql[pos..(end + 1)];
        return null;
    }

    private static int TryMatchDollarTag(string sql, int pos, string tag)
    {
        if (pos + tag.Length > sql.Length) return -1;
        if (string.Compare(sql, pos, tag, 0, tag.Length, StringComparison.Ordinal) == 0)
            return pos + tag.Length;
        return -1;
    }

    private static string StripLeadingComments(string sql)
    {
        int i = 0;
        while (i < sql.Length)
        {
            if (char.IsWhiteSpace(sql[i])) { i++; continue; }
            if (i + 1 < sql.Length && sql[i] == '-' && sql[i + 1] == '-')
            {
                while (i < sql.Length && sql[i] != '\n') i++;
                continue;
            }
            if (i + 1 < sql.Length && sql[i] == '/' && sql[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < sql.Length && !(sql[i] == '*' && sql[i + 1] == '/')) i++;
                i += 2;
                continue;
            }
            break;
        }
        return i < sql.Length ? sql[i..] : string.Empty;
    }

    private static string GetFirstWord(string sql)
    {
        int end = 0;
        while (end < sql.Length && !char.IsWhiteSpace(sql[end]) && sql[end] != '(' && sql[end] != ';' && sql[end] != '*')
            end++;
        return sql[..end];
    }
}
