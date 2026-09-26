namespace Wan.Domain.Entities;

/// <summary>Proveedor del LLM que responde en las llamadas (ElevenLabs sigue haciendo la voz).</summary>
public enum LlmProvider
{
    OpenAi,
    Gemini,
    Anthropic,
}
