namespace Mapache.Domain.Entities;

/// <summary>Agente de ElevenLabs que atiende las llamadas (la voz). Hay una sola fila.</summary>
public class VoiceSettings
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;

    /// <summary>API key de ElevenLabs cifrada; nunca se expone por la API.</summary>
    public string? ApiKeyEncrypted { get; set; }

    public string? AgentId { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
