using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Mapache.Infrastructure.Llm;

/// <summary>
/// OpenAI y Gemini (su API compatible) ya hablan el formato que pide ElevenLabs: se reenvía la petición
/// con la API key y el modelo de Mapache, y la respuesta SSE se copia tal cual.
/// </summary>
public sealed class OpenAiCompatibleChatClient(HttpClient http, ILogger<OpenAiCompatibleChatClient> logger)
{
    public async Task StreamAsync(string baseUrl, string apiKey, JsonObject request, Stream output, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/chat/completions")
        {
            Content = new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            logger.LogWarning("El LLM respondió {Status}: {Body}", (int)response.StatusCode, body);
            throw new LlmException($"El proveedor respondió {(int)response.StatusCode}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
            await output.FlushAsync(ct);
        }
    }
}
