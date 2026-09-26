using Wan.Domain.Entities;

namespace Wan.Infrastructure.Llm;

/// <summary>
/// Valores fijos por proveedor. Lo que se elige desde el panel (proveedor activo, modelo, API key,
/// instrucciones) vive en la base: <see cref="LlmSettings"/> y <see cref="LlmProviderSettings"/>.
/// </summary>
public sealed class LlmOptions
{
    public const string SectionName = "Llm";

    public LlmProviderOptions OpenAi { get; set; } = new() { BaseUrl = "https://api.openai.com/v1" };

    /// <summary>Gemini expone una API compatible con la de OpenAI; por eso comparte el mismo cliente.</summary>
    public LlmProviderOptions Gemini { get; set; } = new() { BaseUrl = "https://generativelanguage.googleapis.com/v1beta/openai" };

    public LlmProviderOptions Anthropic { get; set; } = new();

    public LlmProviderOptions For(LlmProvider provider) => provider switch
    {
        LlmProvider.OpenAi => OpenAi,
        LlmProvider.Gemini => Gemini,
        LlmProvider.Anthropic => Anthropic,
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
    };
}

public sealed class LlmProviderOptions
{
    /// <summary>Modelo cuando el panel no elige uno.</summary>
    public string DefaultModel { get; set; } = "";

    /// <summary>Solo para los proveedores compatibles con OpenAI.</summary>
    public string BaseUrl { get; set; } = "";
}
