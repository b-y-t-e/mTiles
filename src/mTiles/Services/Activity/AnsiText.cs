using System.Text;

namespace mTiles.Services.Activity;

/// <summary>
/// Takes the escape sequences out of a chunk of terminal output, leaving what a reader would see.
/// </summary>
/// <remarks>
/// <para>Pure, and deliberately not a parser. It has one job — make the child's own words matchable —
/// so it recognises the shapes that carry no text (CSI, OSC, and the two-character escapes) and passes
/// everything else through. It does not track a cursor, so it cannot tell you where on the screen a
/// word ended up; <see cref="RecentOutputSource"/> says why that is enough and where it is not.</para>
/// <para><b>The terminal already has a real parser and this is not a second one.</b> Terminal.Avalonia
/// owns the emulation; what happens here is text extraction for pattern matching, and the day it needs
/// a cursor is the day this should be replaced by asking the control for its buffer rather than grown
/// into a parser nobody maintains.</para>
/// </remarks>
public static class AnsiText
{
    private const char Esc = '\x1b';
    private const char Bel = '\x07';

    /// <summary>Strips escape sequences and normalises what is left to text and newlines.</summary>
    public static string Strip(ReadOnlySpan<char> input)
    {
        var output = new StringBuilder(input.Length);
        var i = 0;

        while (i < input.Length)
        {
            var c = input[i];

            if (c != Esc)
            {
                // A carriage return is how a TUI overwrites the line it is on. Kept as a break rather
                // than dropped: without it a status bar redrawn in place runs into the line before it
                // and produces matches for words that were never adjacent.
                if (c is '\r' or '\n') { AppendBreak(output); i++; continue; }
                if (c < ' ' && c != '\t') { i++; continue; }
                output.Append(c);
                i++;
                continue;
            }

            i++;
            if (i >= input.Length) break;

            switch (input[i])
            {
                // CSI: ESC [ params final. The final byte is the first in @-~.
                case '[':
                    i++;
                    while (i < input.Length && input[i] is < '@' or > '~') i++;
                    if (i < input.Length) i++;
                    break;

                // OSC and the other string sequences: run to BEL or ST (ESC \).
                case ']':
                case 'P':
                case 'X':
                case '^':
                case '_':
                    i++;
                    while (i < input.Length && input[i] != Bel
                           && !(input[i] == Esc && i + 1 < input.Length && input[i + 1] == '\\'))
                        i++;
                    if (i < input.Length && input[i] == Esc) i++;
                    if (i < input.Length) i++;
                    break;

                // Everything else is ESC, any number of intermediate bytes, and one final byte. Mostly
                // that is two characters — ESC =, ESC 7 — but a character-set designation is three
                // (ESC ( B), and treating it as two left the "B" behind as text. Which is exactly the
                // shape a TUI emits on startup, so it landed at the front of the window every time.
                default:
                    while (i < input.Length && input[i] is >= ' ' and <= '/') i++;
                    if (i < input.Length) i++;
                    break;
            }
        }

        return output.ToString();
    }

    private static void AppendBreak(StringBuilder output)
    {
        if (output.Length > 0 && output[^1] != '\n') output.Append('\n');
    }
}
