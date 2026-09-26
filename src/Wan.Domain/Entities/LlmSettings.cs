namespace Wan.Domain.Entities;

/// <summary>Configuración general del LLM del bot, igual para todas las cuentas SIP. Hay una sola fila.</summary>
public class LlmSettings
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;

    /// <summary>Proveedor que responde en las llamadas.</summary>
    public LlmProvider ActiveProvider { get; set; } = LlmProvider.OpenAi;

    /// <summary>Instrucciones del bot; vacío = las del agente de ElevenLabs.</summary>
    public string? SystemPrompt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Credencial y modelo de un proveedor. Se guardan los tres aunque solo uno esté activo.</summary>
public class LlmProviderSettings
{
    public LlmProvider Provider { get; set; }

    /// <summary>API key cifrada con Data Protection; nunca se expone por la API.</summary>
    public string? ApiKeyEncrypted { get; set; }

    /// <summary>Vacío = el modelo por defecto de <c>Llm:{Proveedor}:DefaultModel</c>.</summary>
    public string? Model { get; set; }
}
