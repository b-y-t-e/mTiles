namespace mTiles.Models;

/// <summary>
/// The shape of an HTTP API: what a provider serves, and what an agent knows how to speak.
/// </summary>
/// <remarks>
/// <para>An agent and a provider are compatible exactly when these two lists intersect. <b>Four
/// members and not two</b>, and the split inside OpenAI is the one that earns its place: without it
/// codex and Ollama would be reported compatible — both "OpenAI" — and would not work, because codex
/// speaks <see cref="OpenAiResponses"/> and a local server serves <see cref="OpenAiChatCompletions"/>.
/// A pairing the UI offers and the launch then fails is worse than one it never offered.</para>
/// <para>When a local server grows an Anthropic-compatible endpoint this becomes one added member and
/// nothing else, which is the whole reason the axis is the wire format rather than the vendor.</para>
/// </remarks>
public enum ApiFlavor
{
    /// <summary><c>/v1/chat/completions</c> — OpenAI, OpenRouter, z.ai, LM Studio, Ollama. Spoken by
    /// opencode and pi.</summary>
    OpenAiChatCompletions,

    /// <summary><c>/v1/responses</c> — OpenAI, and OpenRouter for some models. Spoken by codex, and by
    /// nothing else here.</summary>
    OpenAiResponses,

    /// <summary><c>/v1/messages</c> — Anthropic, OpenRouter, and z.ai at <c>/api/anthropic</c>. Spoken
    /// by Claude Code.</summary>
    Anthropic,

    /// <summary><c>/api/tags</c> and <c>/api/ps</c> — Ollama's own. Not a chat API: it is what
    /// discovery and model listing ask, and what says which model is <em>loaded</em> right now.
    /// </summary>
    OllamaNative,
}
