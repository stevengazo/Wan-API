using System.Text.Json.Nodes;
using Mapache.Domain.Entities;
using Mapache.Infrastructure.Knowledge;
using Mapache.Infrastructure.Persistence;
using Mapache.Infrastructure.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Mapache.Infrastructure.Llm;

/// <summary>Proveedor, modelo e instrucciones que aplican a una conversación.</summary>
public sealed record LlmSelection(LlmProvider Provider, string Model, string? SystemPrompt);

/// <summary>
/// Custom LLM de ElevenLabs: recibe la petición en formato OpenAI y la resuelve con el proveedor
/// activo en la configuración general.
/// </summary>
public sealed class ChatCompletionRouter(
    MapacheDbContext db,
    LlmResolver resolver,
    KnowledgeService knowledge,
    BotToolRegistry toolRegistry,
    OpenAiCompatibleChatClient openAiCompatible,
    AnthropicChatClient anthropic,
    TimeProvider time,
    ILogger<ChatCompletionRouter> logger)
{
    /// <summary>Rondas de herramientas de Mapache por turno; evita que el modelo quede en un bucle.</summary>
    public const int MaxToolRounds = 4;

    public async Task StreamAsync(JsonObject request, Stream output, CancellationToken ct)
    {
        var target = await resolver.ResolveAsync(ct);
        var selection = new LlmSelection(target.Provider, target.Model, target.SystemPrompt);

        var directory = await db.DirectoryEntries.AsNoTracking().Where(d => d.Enabled).ToListAsync(ct);
        var forms = await db.FormTemplates.AsNoTracking().Where(f => f.Enabled).ToListAsync(ct);
        var callSettings = await db.CallSettings.AsNoTracking().SingleOrDefaultAsync(ct);
        // Búsqueda previa en la base con lo último que dijo quien llama: el texto completo responde en
        // milisegundos, así que no se nota en la latencia del turno.
        var hits = LastUserMessage(request) is { Length: > 2 } question
            ? await knowledge.SearchAsync(question, limit: 3, ct: ct)
            : [];
        var context = string.Join("\n\n", new[] { BotContext.Build(request, directory, forms, callSettings), KnowledgeService.Format(hits) }
            .Where(p => !string.IsNullOrWhiteSpace(p)));
        Prepare(request, selection, context.Length == 0 ? null : context);
        var tools = BotToolRegistry.AddTo(request, await toolRegistry.LoadAsync(ct));
        logger.LogInformation("Custom LLM: {Provider} {Model} con {Tools} herramientas de Mapache", target.Provider, target.Model, tools.Count);

        for (var round = 0; ; round++)
        {
            var interceptor = new SseToolInterceptor(output);
            if (target.Provider == LlmProvider.Anthropic)
            {
                await anthropic.StreamAsync(target.ApiKey, target.Model, request, interceptor, ct);
            }
            else
            {
                await openAiCompatible.StreamAsync(target.BaseUrl, target.ApiKey, request, interceptor, ct);
            }

            var calls = interceptor.ToolCalls;
            if (!calls.Any(c => tools.ContainsKey(c.Name)))
            {
                // Nada para Mapache: el texto ya salió y lo retenido (tools de ElevenLabs, fin) se entrega tal cual.
                await interceptor.ReleaseHeldAsync(ct);
                return;
            }

            if (round == MaxToolRounds)
            {
                logger.LogWarning("El modelo sigue pidiendo herramientas tras {Rounds} rondas; se corta el turno", round);
                var writer = new ChatCompletionChunkWriter(output, target.Model, time);
                await writer.WriteContentAsync("Disculpa, no pude completar la consulta. ¿Te puedo ayudar con algo más?", ct);
                await writer.WriteFinishAsync("stop", ct);
                return;
            }

            await AppendToolRoundAsync(request, interceptor, tools, ct);
        }
    }

    /// <summary>
    /// Agrega a la conversación lo que pidió el modelo y el resultado de cada herramienta de Mapache, para la
    /// siguiente ronda. Las de ElevenLabs que vinieron mezcladas no se ejecutan: se le avisa al modelo.
    /// </summary>
    private async Task AppendToolRoundAsync(
        JsonObject request, SseToolInterceptor interceptor, IReadOnlyDictionary<string, BotTool> tools, CancellationToken ct)
    {
        var messages = request["messages"]!.AsArray();
        var calls = interceptor.ToolCalls;
        messages.Add(new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = interceptor.Text.Length == 0 ? null : interceptor.Text,
            ["tool_calls"] = new JsonArray(calls.Select(c => (JsonNode)new JsonObject
            {
                ["id"] = c.Id,
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.Arguments.ToString() },
            }).ToArray()),
        });

        foreach (var call in calls)
        {
            string result;
            if (!tools.TryGetValue(call.Name, out var tool))
            {
                result = "No se ejecutó en esta ronda. Si la sigues necesitando, vuelve a llamarla.";
            }
            else
            {
                var started = time.GetTimestamp();
                try
                {
                    var arguments = call.Arguments.Length == 0 ? [] : JsonNode.Parse(call.Arguments.ToString()) as JsonObject ?? [];
                    result = await tool.ExecuteAsync(arguments, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "La herramienta {Tool} falló", call.Name);
                    result = $"Error al ejecutar la herramienta: {ex.Message}";
                }

                logger.LogInformation("Herramienta {Tool} en {Ms} ms", call.Name, time.GetElapsedTime(started).TotalMilliseconds);
            }

            messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = call.Id, ["content"] = result });
        }
    }

    /// <summary>Texto del último mensaje de quien llama, para buscar en la base de conocimiento.</summary>
    public static string? LastUserMessage(JsonObject request) =>
        (request["messages"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .LastOrDefault(m => m["role"]?.GetValue<string>() == "user")?["content"] switch
        {
            JsonValue value when value.GetValueKind() == System.Text.Json.JsonValueKind.String => value.GetValue<string>(),
            JsonArray parts => string.Concat(parts.Select(p => p?["text"]?.GetValue<string>())),
            _ => null,
        };

    /// <summary>
    /// Deja la petición lista para el proveedor: el modelo de Mapache, las instrucciones generales en lugar
    /// de las del agente, el contexto de Mapache (<see cref="BotContext"/>) y sin <c>elevenlabs_extra_body</c>,
    /// que los proveedores rechazarían.
    /// </summary>
    public static void Prepare(JsonObject request, LlmSelection selection, string? context = null)
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
            // Sin instrucciones propias quedan las del agente: el contexto va justo después de ellas.
            insertAt = messages.TakeWhile(m => m?["role"]?.GetValue<string>() is "system" or "developer").Count();
        }

        if (!string.IsNullOrWhiteSpace(context))
        {
            messages.Insert(insertAt, new JsonObject { ["role"] = "system", ["content"] = context });
        }
    }
}
