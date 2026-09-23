namespace mTiles.Services.Agents.Sessions;

/// <summary>
/// Whether an agent that was asked to resume a conversation actually did.
/// </summary>
/// <remarks>
/// <para><b>Why this exists.</b> A cold resume can fail, and on two of the six CLIs it failed in silence:
/// the tile showed the whole transcript while the agent behind it had none of it, and the first sign was an
/// answer that made no sense. That silence was the one thing standing between this application and
/// restarting an idle agent on its own — a restart nobody can describe the cost of is not one to perform
/// for somebody. Measured 2026-09-17, live, both silences turn out to be detectable before the first
/// message is sent, so they are said out loud here instead.</para>
/// <list type="bullet">
/// <item><b>pi 0.84.4</b> keys a session by the exact working directory and its agent directory. Asked for
/// an id it cannot find there, it creates an empty session under that id, writes
/// <c>Warning: No project session found with id …</c> to stderr, and answers <c>get_state</c> with
/// <c>messageCount: 0</c> — which is the signal read here, because it is structured and the host already
/// asks for <c>get_state</c> the moment the process starts.</item>
/// <item><b>agy 1.2.3</b>, asked for a conversation it does not have, writes <c>warning: conversation "…" not
/// found</c> to stderr, starts a new one and exits 0; its <c>init</c> line — written at start-up, before any
/// prompt — then carries a <c>conversation_id</c> that is not the one requested. That comparison is the
/// signal, for the same reason.</item>
/// <item><b>Grok 1.0.34</b> needs nothing here: it advertises <c>loadSession</c>, and an id it cannot find is a
/// JSON-RPC error (<c>-32603 Path not found</c>) that <c>AcpAgentSession</c> already turns into a notice.
/// </item>
/// </list>
/// <para>Pure, so the two rules are argued in a table test rather than rediscovered from a tile whose agent
/// has forgotten everything.</para>
/// </remarks>
public static class ResumeCheck
{
    /// <summary>What the tile says when a resume did not happen. The transcript stays; the agent's memory
    /// of it does not, and the sentence has to say which of the two is gone.</summary>
    public static string Lost(string agentName) =>
        $"{agentName} did not find this conversation and started without its history. " +
        "Everything above is still here, but the agent does not remember it.";

    /// <summary>pi: whether a resume of a conversation that has history came back empty.</summary>
    /// <param name="requested">The session id pi was started with, or empty for a new conversation.</param>
    /// <param name="hasHistory">Whether anything was said in this conversation before. A conversation nobody
    /// spoke in has no session file — pi writes one only at the first message — so its resume always comes
    /// back empty, and that is not a loss.</param>
    /// <param name="messageCount">What <c>get_state</c> says pi holds, or null when it did not say.</param>
    public static bool PiLost(string? requested, bool hasHistory, long? messageCount) =>
        !string.IsNullOrEmpty(requested) && hasHistory && messageCount == 0;

    /// <summary>agy: whether the conversation agy reports is not the one it was asked to resume.</summary>
    /// <param name="requested">The id passed as <c>--conversation</c>, or empty for a new conversation.</param>
    /// <param name="reported">The <c>conversation_id</c> on agy's <c>init</c> line.</param>
    /// <remarks>Ordinal: these are ids agy generated and hands back verbatim, and an id that comes back in a
    /// different case is not one this application has ever seen it do.</remarks>
    public static bool AgyLost(string? requested, string? reported) =>
        !string.IsNullOrEmpty(requested) && !string.IsNullOrEmpty(reported)
        && !string.Equals(requested, reported, StringComparison.Ordinal);
}
