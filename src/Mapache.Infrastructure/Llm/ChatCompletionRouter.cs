using System.Text.Json.Nodes;
using Mapache.Domain.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mapache.Infrastructure.Llm;

/// <summary>Proveedor, modelo e instrucciones que aplican a una conversación.</summary>
public sealed record LlmSelection(LlmProvider Provider, string Model, string? SystemPrompt);

/// <summary>
/// Custom LLM de ElevenLabs: recibe la petición en formato OpenAI y la resuelve con el proveedor
/// que tenga configurada la cuenta SIP de la llamada.
/// </summary>
public sealed class ChatCompletionRouter(
    IOptionsMonitor<LlmOptions> options,
    OpenAiCompatibleChatClient openAiCompatible,
    AnthropicChatClient anthropic,
    ILogger<ChatCompletionRouter> logger)
{
    public LlmSelection Resolve(Extension? extension)
    {
        var provider = extension?.LlmProvider ?? options.CurrentValue.DefaultProvider;
        var model = string.IsNullOrWhiteSpace(extension?.LlmModel)
            ? options.CurrentValue.For(provider).DefaultModel
            : extension.LlmModel;
        return new LlmSelection(provider, model, extension?.SystemPrompt);
    }

    public async Task StreamAsync(LlmSelection selection, JsonObject request, Stream output, CancellationToken ct)
    {
        var provider = options.CurrentValue.For(selection.Provider);
        if (!provider.IsConfigured)
        {
            throw new LlmException($"El proveedor {selection.Provider} no tiene API key o modelo por defecto configurados");
        }

        Prepare(request, selection);
        logger.LogInformation("Custom LLM: {Provider} {Model}", selection.Provider, selection.Model);

        if (selection.Provider == LlmProvider.Anthropic)
        {
            await anthropic.StreamAsync(provider, selection.Model, request, output, ct);
        }
        else
        {
            await openAiCompatible.StreamAsync(provider, request, output, ct);
        }
    }

    /// <summary>
    /// Deja la petición lista para el proveedor: el modelo de Mapache, las instrucciones de la cuenta en lugar
    /// de las del agente y sin <c>elevenlabs_extra_body</c>, que los proveedores rechazarían.
    /// </summary>
    public static void Prepare(JsonObject request, LlmSelection selection)
    {
        request.Remove("elevenlabs_extra_body");
        request["model"] = selection.Model;
        request["stream"] = true;

        if (string.IsNullOrWhiteSpace(selection.SystemPrompt) || request["messages"] is not JsonArray messages)
        {
            return;
        }

        foreach (var system in messages.OfType<JsonObject>().Where(m => m["role"]?.GetValue<string>() is "system" or "developer").ToList())
        {
            messages.Remove(system);
        }

        messages.Insert(0, new JsonObject { ["role"] = "system", ["content"] = selection.SystemPrompt });
    }
}
