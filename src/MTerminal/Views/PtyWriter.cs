using System.Reflection;
using System.Text;
using Avalonia.VisualTree;
using Iciclecreek.Terminal;

namespace MTerminal.Views;

public static class PtyWriter
{
    private static readonly FieldInfo? PtyField =
        typeof(TerminalView).GetField("_ptyConnection", BindingFlags.NonPublic | BindingFlags.Instance);

    public static void Write(TerminalControl terminal, string data)
    {
        var tv = terminal.GetVisualDescendants().OfType<TerminalView>().FirstOrDefault();
        if (tv == null) return;

        var pty = PtyField?.GetValue(tv);
        if (pty == null) return;

        var stream = pty.GetType().GetProperty("WriterStream")?.GetValue(pty) as Stream;
        if (stream == null) return;

        var bytes = Encoding.UTF8.GetBytes(data);
        stream.Write(bytes);
        stream.Flush();
    }

    public static void AttachStartupScript(TerminalControl terminal, string script, string tileId)
    {
        terminal.ShellReady += OnShellReady;
        void OnShellReady(object? s, EventArgs args)
        {
            terminal.ShellReady -= OnShellReady;
            foreach (var line in script.TrimEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries))
                Write(terminal, line.TrimEnd('\r').Replace("${tileId}", tileId) + "\r");
        }
    }
}
