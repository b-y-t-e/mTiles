using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using MTerminal.Models;

namespace MTerminal.ViewModels;

public partial class TerminalPaneViewModel : ObservableObject, IDisposable
{
    public string WorkingDirectory { get; }
    public ShellProfile Shell { get; }

    internal Control? CachedControl { get; set; }
    internal bool IsLaunched { get; set; }

    public TerminalPaneViewModel(string workingDirectory, ShellProfile? shell = null, AppSettings? settings = null)
    {
        WorkingDirectory = workingDirectory;
        Shell = shell ?? ShellProfile.ResolveDefault(settings ?? new AppSettings());
    }

    public void Dispose()
    {
        if (CachedControl is Iciclecreek.Terminal.TerminalControl tc)
        {
            try { tc.Kill(); } catch { }
        }
    }
}
