namespace Mapache.Domain.Entities;

/// <summary>Quién pone la voz del bot: reconoce lo que dice quien llama, habla y maneja los turnos.</summary>
public enum VoiceProvider
{
    /// <summary>Agente de ElevenLabs Conversational AI con el Custom LLM de Mapache.</summary>
    ElevenLabs,

    /// <summary>OpenAI Realtime: modelo de voz a voz de OpenAI con las instrucciones y herramientas de Mapache.</summary>
    OpenAiRealtime,

    /// <summary>Deepgram Voice Agent: voz de Deepgram con el Custom LLM de Mapache.</summary>
    Deepgram,
}

/// <summary>Proveedor de voz de las llamadas y credenciales de cada uno. Hay una sola fila.</summary>
public class VoiceSettings
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;

    public VoiceProvider Provider { get; set; } = VoiceProvider.ElevenLabs;

    /// <summary>API key de ElevenLabs cifrada; nunca se expone por la API.</summary>
    public string? ApiKeyEncrypted { get; set; }

    /// <summary>Agente de ElevenLabs.</summary>
    public string? AgentId { get; set; }

    /// <summary>API key de OpenAI para Realtime, cifrada; vacía = la de OpenAI en Configuración → IA.</summary>
    public string? OpenAiApiKeyEncrypted { get; set; }

    public string? OpenAiModel { get; set; }

    public string? OpenAiVoice { get; set; }

    /// <summary>Modelo que transcribe a quien llama (para el panel en vivo); vacío = sin transcripción.</summary>
    public string? OpenAiTranscriptionModel { get; set; }

    public string? DeepgramApiKeyEncrypted { get; set; }

    /// <summary>Idioma del agente de Deepgram (es, en...).</summary>
    public string? DeepgramLanguage { get; set; }

    public string? DeepgramListenModel { get; set; }

    public string? DeepgramSpeakModel { get; set; }

    /// <summary>Saludo inicial para OpenAI y Deepgram; en ElevenLabs es el primer mensaje del agente.</summary>
    public string? Greeting { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
