using Mapache.Domain.Entities;

namespace Mapache.Infrastructure.Llm;

public sealed class LlmOptions
{
    public const string SectionName = "Llm";

    /// <summary>Proveedor para las cuentas que no eligen uno.</summary>
    public LlmProvider DefaultProvider { get; set; } = LlmProvider.OpenAi;

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
    public string ApiKey { get; set; } = "";

    public string DefaultModel { get; set; } = "";

    /// <summary>Solo para los proveedores compatibles con OpenAI.</summary>
    public string BaseUrl { get; set; } = "";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(DefaultModel);
}
