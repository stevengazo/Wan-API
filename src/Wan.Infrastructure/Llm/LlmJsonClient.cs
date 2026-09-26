using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using Wan.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace Wan.Infrastructure.Llm;

/// <summary>
/// Pide al proveedor activo una respuesta en JSON que cumpla un esquema. Para tareas del panel
/// (no de la llamada): una sola respuesta, sin streaming.
/// </summary>
public sealed class LlmJsonClient(HttpClient http, LlmResolver resolver, ILogger<LlmJsonClient> logger)
{
    /// <param name="schema">JSON Schema con <c>additionalProperties: false</c> y todas las propiedades requeridas.</param>
    public async Task<JsonElement> CompleteAsync(string system, string user, string schemaName, JsonObject schema, CancellationToken ct)
    {
        var target = await resolver.ResolveAsync(ct);
        var text = target.Provider == LlmProvider.Anthropic
            ? await AnthropicAsync(target, system, user, schema, ct)
            : await OpenAiCompatibleAsync(target, system, user, schemaName, schema, ct);

        try
        {
            return JsonDocument.Parse(text).RootElement.Clone();
        }
        catch (JsonException ex)
        {
            logger.LogWarning("La IA devolvió JSON inválido: {Text}", text.Length > 500 ? text[..500] : text);
            throw new LlmException("La IA devolvió una respuesta que no se pudo leer; intenta de nuevo", ex);
        }
    }

    /// <summary>OpenAI y Gemini (API compatible): <c>response_format</c> con el esquema en modo estricto.</summary>
    private async Task<string> OpenAiCompatibleAsync(LlmTarget target, string system, string user, string schemaName, JsonObject schema, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = target.Model,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = system },
                new JsonObject { ["role"] = "user", ["content"] = user }),
            ["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject { ["name"] = schemaName, ["strict"] = true, ["schema"] = schema.DeepClone() },
            },
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{target.BaseUrl.TrimEnd('/')}/chat/completions")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", target.ApiKey);

        using var response = await http.SendAsync(request, ct);
        var content = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("El LLM respondió {Status}: {Body}", (int)response.StatusCode, content);
            throw new LlmException($"El proveedor respondió {(int)response.StatusCode}");
        }

        var message = JsonNode.Parse(content)?["choices"]?[0]?["message"];
        if (message?["refusal"]?.GetValue<string>() is { Length: > 0 } refusal)
        {
            throw new LlmException($"La IA declinó: {refusal}");
        }

        return message?["content"]?.GetValue<string>() ?? throw new LlmException("La IA no devolvió contenido");
    }

    /// <summary>Claude: salida estructurada (<c>output_config.format</c>) con el SDK oficial.</summary>
    private static async Task<string> AnthropicAsync(LlmTarget target, string system, string user, JsonObject schema, CancellationToken ct)
    {
        var client = new AnthropicClient { ApiKey = target.ApiKey };
        var parameters = new MessageCreateParams
        {
            Model = target.Model,
            MaxTokens = 16000,
            System = system,
            Messages = [new() { Role = Role.User, Content = user }],
            OutputConfig = new OutputConfig
            {
                Format = new JsonOutputFormat { Schema = schema.ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value)) },
            },
        };

        // Armar un formulario no pide razonar mucho: esfuerzo bajo para que el panel responda rápido. Haiku no lo acepta.
        if (!target.Model.Contains("haiku", StringComparison.Ordinal))
        {
            parameters = parameters with { OutputConfig = parameters.OutputConfig! with { Effort = Effort.Low } };
        }

        Message response;
        try
        {
            response = await client.Messages.Create(parameters, ct);
        }
        catch (AnthropicApiException ex)
        {
            throw new LlmException($"Claude respondió {ex.StatusCode}", ex);
        }

        if (response.StopReason == "refusal")
        {
            throw new LlmException("Claude declinó generar esta respuesta");
        }

        return string.Concat(response.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text));
    }
}
