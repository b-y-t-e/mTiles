using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using mTiles.Services;

namespace mTiles.Views;

public partial class AgentFileSyncWizard : UserControl, OverlayHost.IFocusOnOpen
{
    public AgentFileSyncWizard()
    {
        InitializeComponent();
    }

    private AgentFileSyncWizard(AgentFileSyncWizardRequest request) : this()
    {
        var pickAuthoritative = request.Mode == AgentFileSyncWizardMode.AskEnableAndPickAuthoritative;

        WorkspaceText.Text = request.WorkspaceDirectory;
        ToolTip.SetTip(WorkspaceText, request.WorkspaceDirectory);

        ExplanationText.Text = pickAuthoritative
            ? "Editing either file will copy it over the other, keeping them identical."
            : "Editing either file will copy it to the other, creating it if missing.";

        DeclineButton.Click += (_, _) => OverlayHost.CloseWith(this, new AgentFileSyncWizardResult(false, null));
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                OverlayHost.CloseWith(this, new AgentFileSyncWizardResult(false, null));
        };
        // The modal takes the keyboard when it opens — see FocusOnOpen below.

        if (pickAuthoritative)
        {
            EnableButton.Click += (_, _) =>
            {
                QuestionStep.IsVisible = false;
                PickStep.IsVisible = true;

                Fill(request.Claude, ClaudeName, ClaudeMeta);
                Fill(request.Agents, AgentsName, AgentsMeta);
            };
            UseClaudeButton.Click += (_, _) =>
                OverlayHost.CloseWith(this, new AgentFileSyncWizardResult(true, AgentFileSyncEngine.ClaudeFileName));
            UseAgentsButton.Click += (_, _) =>
                OverlayHost.CloseWith(this, new AgentFileSyncWizardResult(true, AgentFileSyncEngine.AgentsFileName));
        }
        else
        {
            EnableButton.Click += (_, _) => OverlayHost.CloseWith(this, new AgentFileSyncWizardResult(true, null));
        }
    }

    private static void Fill(AgentFileSyncFileInfo? info, TextBlock name, TextBlock meta)
    {
        if (info is null)
        {
            meta.Text = "Not present";
            return;
        }
        name.Text = info.FileName;
        meta.Text = $"{FormatSize(info.SizeBytes)} · modified {info.LastWriteTimeUtc.ToLocalTime():g}";
    }

    private static string FormatSize(long bytes) =>
        bytes < 1024 ? $"{bytes} B" : $"{bytes / 1024.0:0.#} KB";

    /// <summary>"Not now" is the safe default for every answer key: Enter and Escape both decline,
    /// so no single keystroke enables the feature — enabling stays a deliberate Tab or click.</summary>
    public void FocusOnOpen() => DeclineButton.Focus();

    /// <summary>Shows the wizard over <paramref name="owner"/>'s window and answers what the user
    /// chose, or an implicit decline if it was closed without an answer (Escape, the X).</summary>
    public static async Task<AgentFileSyncWizardResult?> ShowAsync(Visual owner, AgentFileSyncWizardRequest request)
    {
        if (OverlayHost.For(owner) is not { } host)
            return new AgentFileSyncWizardResult(false, null);

        return await host.ShowAsync<AgentFileSyncWizardResult>(new AgentFileSyncWizard(request), width: 420)
               ?? new AgentFileSyncWizardResult(false, null);
    }
}
