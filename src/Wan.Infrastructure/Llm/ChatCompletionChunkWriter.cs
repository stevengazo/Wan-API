using System.Text;
using System.Text.Json.Nodes;

namespace Mapache.Infrastructure.Llm;

/// <summary>
/// Escribe una respuesta en streaming con el formato SSE de chat completions de OpenAI, que es el que
/// espera ElevenLabs de un Custom LLM. Solo lo usan los proveedores que no hablan ese formato de fábrica.
/// </summary>
public sealed class ChatCompletionChunkWriter(Stream output, string model, TimeProvider time)
{
    private readonly string _id = $"chatcmpl-{Guid.NewGuid():N}";
    private readonly long _created = time.GetUtcNow().ToUnixTimeSeconds();
    private bool _roleSent;

    public Task WriteContentAsync(string text, CancellationToken ct) =>
        WriteDeltaAsync(new JsonObject { ["content"] = text }, null, ct);

    public Task WriteToolCallStartAsync(int index, string id, string name, CancellationToken ct) =>
        WriteDeltaAsync(new JsonObject
        {
            ["tool_calls"] = new JsonArray(new JsonObject
            {
                ["index"] = index,
                ["id"] = id,
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = name, ["arguments"] = "" },
            }),
        }, null, ct);

    public Task WriteToolArgumentsAsync(int index, string argumentsFragment, CancellationToken ct) =>
        WriteDeltaAsync(new JsonObject
        {
            ["tool_calls"] = new JsonArray(new JsonObject
            {
                ["index"] = index,
                ["function"] = new JsonObject { ["arguments"] = argumentsFragment },
            }),
        }, null, ct);

    /// <summary>Cierra la respuesta: chunk con <c>finish_reason</c> y el <c>[DONE]</c> final.</summary>
    public async Task WriteFinishAsync(string finishReason, CancellationToken ct)
    {
        await WriteDeltaAsync(new JsonObject(), finishReason, ct);
        await WriteRawAsync("data: [DONE]\n\n", ct);
    }

    private async Task WriteDeltaAsync(JsonObject delta, string? finishReason, CancellationToken ct)
    {
        if (!_roleSent)
        {
            delta["role"] = "assistant";
            _roleSent = true;
        }

        var chunk = new JsonObject
        {
            ["id"] = _id,
            ["object"] = "chat.completion.chunk",
            ["created"] = _created,
            ["model"] = model,
            ["choices"] = new JsonArray(new JsonObject
            {
                ["index"] = 0,
                ["delta"] = delta,
                ["finish_reason"] = finishReason,
            }),
        };
        await WriteRawAsync($"data: {chunk.ToJsonString()}\n\n", ct);
    }

    private async Task WriteRawAsync(string text, CancellationToken ct)
    {
        await output.WriteAsync(Encoding.UTF8.GetBytes(text), ct);
        // Cada fragmento tiene que salir ya: ElevenLabs empieza a sintetizar voz con el primer texto.
        await output.FlushAsync(ct);
    }
}
