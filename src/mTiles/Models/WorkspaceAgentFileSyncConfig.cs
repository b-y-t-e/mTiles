namespace mTiles.Models;

/// <summary>
/// This workspace's own answer about mirroring <c>CLAUDE.md</c> and <c>AGENTS.md</c>.
/// </summary>
/// <remarks>
/// Absence of the file this is stored in means "never asked" — <see cref="WizardAnswered"/> false and
/// <see cref="Enabled"/> false — which is what lets the wizard tell "the user said no" apart from "nobody
/// has said anything yet".
/// </remarks>
public sealed class WorkspaceAgentFileSyncConfig
{
    public bool Enabled { get; set; }
    public bool WizardAnswered { get; set; }

    /// <summary>Which of the two files the user named as the current one when they were asked, or null
    /// when there was nothing to pick (identical content, a decline, or only one file existed). Kept so
    /// the answer outlives the run that asked it: a start that was cut off by an unload must not settle
    /// the same disagreement by mtime when the workspace is opened again.</summary>
    public string? AuthoritativeFileName { get; set; }
}
