using System.Text.Json.Nodes;
using Mapache.Domain.Entities;
using Mapache.Infrastructure.Persistence;
using Mapache.Infrastructure.Security;
using Mapache.Infrastructure.Transfers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mapache.Infrastructure.Llm;

/// <summary>Proveedor, modelo e instrucciones que aplican a una conversación.</summary>
public sealed record LlmSelection(LlmProvider Provider, string Model, string? SystemPrompt);

/// <summary>
/// Custom LLM de ElevenLabs: recibe la petición en formato OpenAI y la resuelve con el proveedor
/// activo en la configuración general.
/// </summary>
public sealed class ChatCompletionRouter(
    MapacheDbContext db,
    ISecretProtector protector,
    IOptionsMonitor<LlmOptions> options,
    OpenAiCompatibleChatClient openAiCompatible,
    AnthropicChatClient anthropic,
    ILogger<ChatCompletionRouter> logger)
{
    public async Task StreamAsync(JsonObject request, Stream output, CancellationToken ct)
    {
        var settings = await db.LlmSettings.AsNoTracking().SingleOrDefaultAsync(ct) ?? new LlmSettings();
        var provider = settings.ActiveProvider;
        var stored = await db.LlmProviderSettings.AsNoTracking().SingleOrDefaultAsync(p => p.Provider == provider, ct);
        var defaults = options.CurrentValue.For(provider);

        if (stored?.ApiKeyEncrypted is not { } encryptedKey)
        {
            throw new LlmException($"El proveedor {provider} no tiene API key configurada");
        }

        var model = string.IsNullOrWhiteSpace(stored.Model) ? defaults.DefaultModel : stored.Model;
        var selection = new LlmSelection(provider, model, settings.SystemPrompt);
        var apiKey = protector.Unprotect(encryptedKey);

        var directory = await db.DirectoryEntries.AsNoTracking().Where(d => d.Enabled).ToListAsync(ct);
        Prepare(request, selection, DirectoryPrompt.Build(directory));
        logger.LogInformation("Custom LLM: {Provider} {Model}", provider, model);

        if (provider == LlmProvider.Anthropic)
        {
            await anthropic.StreamAsync(apiKey, model, request, output, ct);
        }
        else
        {
            await openAiCompatible.StreamAsync(defaults.BaseUrl, apiKey, request, output, ct);
        }
    }

    /// <summary>
    /// Deja la petición lista para el proveedor: el modelo de Mapache, las instrucciones generales en lugar
    /// de las del agente, el directorio de transferencias y sin <c>elevenlabs_extra_body</c>, que los
    /// proveedores rechazarían.
    /// </summary>
    public static void Prepare(JsonObject request, LlmSelection selection, string? directory = null)
    {
        request.Remove("elevenlabs_extra_body");
        request["model"] = selection.Model;
        request["stream"] = true;

        if (request["messages"] is not JsonArray messages)
        {
            return;
        }

        int insertAt;
        if (!string.IsNullOrWhiteSpace(selection.SystemPrompt))
        {
            foreach (var system in messages.OfType<JsonObject>().Where(m => m["role"]?.GetValue<string>() is "system" or "developer").ToList())
            {
                messages.Remove(system);
            }

            messages.Insert(0, new JsonObject { ["role"] = "system", ["content"] = selection.SystemPrompt });
            insertAt = 1;
        }
        else
        {
            // Sin instrucciones propias quedan las del agente: el directorio va justo después de ellas.
            insertAt = messages.TakeWhile(m => m?["role"]?.GetValue<string>() is "system" or "developer").Count();
        }

        if (!string.IsNullOrWhiteSpace(directory))
        {
            messages.Insert(insertAt, new JsonObject { ["role"] = "system", ["content"] = directory });
        }
    }
}
