using System.Text.Json.Nodes;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta;
using Anthropic.Models.Beta.Messages;
using Microsoft.Extensions.Logging;

namespace Mapache.Infrastructure.Llm;

/// <summary>Claude por la Messages API; la respuesta se reescribe al formato SSE de OpenAI que espera ElevenLabs.</summary>
public sealed class AnthropicChatClient(TimeProvider time, ILogger<AnthropicChatClient> logger)
{
    /// <summary>Lo que dice el bot si Claude declina responder y no hay modelo de respaldo que lo haga.</summary>
    public const string RefusalReply = "Disculpa, con eso no te puedo ayudar. ¿Hay algo más en lo que te pueda ayudar?";

    // Modelos con clasificadores que pueden declinar: el servidor reintenta con el modelo de respaldo que
    // corresponda a la categoría del rechazo (fallbacks "default").
    private static readonly string[] ModelsWithRefusalFallback = ["claude-opus-5", "claude-fable-5-1"];

    private AnthropicClient? _client;
    private string? _clientKey;

    public async Task StreamAsync(LlmProviderOptions provider, string model, JsonObject request, Stream output, CancellationToken ct)
    {
        var parameters = new MessageCreateParams
        {
            Model = model,
            MaxTokens = 16000,
            System = AnthropicRequestTranslator.System(request),
            Messages = AnthropicRequestTranslator.Conversation(request),
        };

        var tools = AnthropicRequestTranslator.Tools(request);
        if (tools.Count > 0)
        {
            parameters = parameters with { Tools = tools };
            if (AnthropicRequestTranslator.ToolsDisabled(request))
            {
                parameters = parameters with { ToolChoice = new BetaToolChoiceNone() };
            }
        }

        // En una llamada cada segundo de silencio se nota: esfuerzo bajo para responder rápido.
        // Haiku no acepta el parámetro.
        if (!model.Contains("haiku", StringComparison.Ordinal))
        {
            parameters = parameters with { OutputConfig = new BetaOutputConfig { Effort = Effort.Low } };
        }

        if (ModelsWithRefusalFallback.Contains(model))
        {
            parameters = parameters with
            {
                Betas = [AnthropicBeta.ServerSideFallback2026_07_01],
                Fallbacks = new Default(),
            };
        }

        var writer = new ChatCompletionChunkWriter(output, model, time);
        var toolIndexByBlock = new Dictionary<long, int>();
        var toolsWithArguments = new HashSet<int>();
        var finishReason = "stop";

        try
        {
            await foreach (var streamEvent in Client(provider).Beta.Messages.CreateStreaming(parameters, ct))
            {
                if (streamEvent.TryPickContentBlockStart(out var start) && start.ContentBlock.TryPickBetaToolUse(out var toolUse))
                {
                    var index = toolIndexByBlock.Count;
                    toolIndexByBlock[start.Index] = index;
                    await writer.WriteToolCallStartAsync(index, toolUse.ID, toolUse.Name, ct);
                }
                else if (streamEvent.TryPickContentBlockDelta(out var delta))
                {
                    if (delta.Delta.TryPickText(out var text))
                    {
                        await writer.WriteContentAsync(text.Text, ct);
                    }
                    else if (delta.Delta.TryPickInputJson(out var json) && toolIndexByBlock.TryGetValue(delta.Index, out var index))
                    {
                        toolsWithArguments.Add(index);
                        await writer.WriteToolArgumentsAsync(index, json.PartialJson, ct);
                    }
                }
                else if (streamEvent.TryPickContentBlockStop(out var stop)
                    && toolIndexByBlock.TryGetValue(stop.Index, out var stoppedTool)
                    && toolsWithArguments.Add(stoppedTool))
                {
                    // Un tool sin parámetros no manda deltas; ElevenLabs necesita igual un JSON válido.
                    await writer.WriteToolArgumentsAsync(stoppedTool, "{}", ct);
                }
                else if (streamEvent.TryPickDelta(out var messageDelta) && messageDelta.Delta.StopReason is { } stopReason)
                {
                    finishReason = stopReason.Raw() switch
                    {
                        "tool_use" => "tool_calls",
                        "max_tokens" => "length",
                        _ => "stop",
                    };

                    if (stopReason.Raw() == "refusal")
                    {
                        logger.LogWarning("Claude declinó responder con {Model}", model);
                        await writer.WriteContentAsync(RefusalReply, ct);
                    }
                }
            }
        }
        catch (AnthropicApiException ex)
        {
            throw new LlmException($"Claude respondió {ex.StatusCode}", ex);
        }

        await writer.WriteFinishAsync(finishReason, ct);
    }

    private AnthropicClient Client(LlmProviderOptions provider)
    {
        // Se recrea solo si cambia la key (IOptionsMonitor la puede recargar sin reiniciar).
        if (_client is null || _clientKey != provider.ApiKey)
        {
            _client = new AnthropicClient { ApiKey = provider.ApiKey };
            _clientKey = provider.ApiKey;
        }

        return _client;
    }
}
