namespace Mapache.Api.Webhooks;

public sealed class AgentWebhookOptions
{
    public const string SectionName = "AgentWebhooks";

    /// <summary>
    /// Bearer token que ElevenLabs envía en cada llamada a un server tool (configurado como secreto en el tool).
    /// </summary>
    public string ToolToken { get; set; } = "";

    /// <summary>Secreto HMAC de los webhooks post-llamada de ElevenLabs.</summary>
    public string PostCallSecret { get; set; } = "";
}
