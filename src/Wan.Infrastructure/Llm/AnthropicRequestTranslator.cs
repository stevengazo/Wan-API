using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic.Models.Beta.Messages;

namespace Wan.Infrastructure.Llm;

/// <summary>Traduce una petición de chat completions (formato OpenAI, la que manda ElevenLabs) a la Messages API.</summary>
public static class AnthropicRequestTranslator
{
    /// <summary>
    /// ElevenLabs guarda el saludo inicial del agente como primer mensaje del asistente; la Messages API
    /// necesita que la conversación empiece con el usuario.
    /// </summary>
    public const string CallStartedPlaceholder = "(La llamada acaba de comenzar.)";

    public static string System(JsonObject request)
    {
        var system = new StringBuilder();
        foreach (var message in Messages(request))
        {
            if (RoleOf(message) is "system" or "developer" && Text(message["content"]) is { Length: > 0 } text)
            {
                system.AppendLine(text);
            }
        }

        return system.ToString().Trim();
    }

    public static List<BetaMessageParam> Conversation(JsonObject request)
    {
        // Mensajes seguidos del mismo rol se juntan: los resultados de tools llegan uno por mensaje "tool"
        // y la Messages API los espera todos en un solo mensaje del usuario.
        var turns = new List<(bool IsUser, List<BetaContentBlockParam> Blocks)>();

        void Add(bool isUser, BetaContentBlockParam block)
        {
            if (turns.Count == 0 || turns[^1].IsUser != isUser)
            {
                turns.Add((isUser, []));
            }

            turns[^1].Blocks.Add(block);
        }

        foreach (var message in Messages(request))
        {
            switch (RoleOf(message))
            {
                case "user":
                    if (Text(message["content"]) is { Length: > 0 } userText)
                    {
                        Add(true, new BetaTextBlockParam { Text = userText });
                    }

                    break;

                case "assistant":
                    if (Text(message["content"]) is { Length: > 0 } assistantText)
                    {
                        Add(false, new BetaTextBlockParam { Text = assistantText });
                    }

                    foreach (var call in message["tool_calls"]?.AsArray() ?? [])
                    {
                        Add(false, new BetaToolUseBlockParam
                        {
                            ID = call?["id"]?.GetValue<string>() ?? "",
                            Name = call?["function"]?["name"]?.GetValue<string>() ?? "",
                            Input = Arguments(call?["function"]?["arguments"]?.GetValue<string>()),
                        });
                    }

                    break;

                case "tool":
                    Add(true, new BetaToolResultBlockParam
                    {
                        ToolUseID = message["tool_call_id"]?.GetValue<string>() ?? "",
                        Content = Text(message["content"]) ?? "",
                    });
                    break;
            }
        }

        if (turns.Count == 0 || !turns[0].IsUser)
        {
            turns.Insert(0, (true, [new BetaTextBlockParam { Text = CallStartedPlaceholder }]));
        }

        return turns
            .Select(t => new BetaMessageParam { Role = t.IsUser ? Role.User : Role.Assistant, Content = t.Blocks })
            .ToList();
    }

    public static List<BetaToolUnion> Tools(JsonObject request)
    {
        var tools = new List<BetaToolUnion>();
        foreach (var tool in request["tools"]?.AsArray() ?? [])
        {
            var function = tool?["function"];
            if (function?["name"]?.GetValue<string>() is not { } name)
            {
                continue;
            }

            var parameters = function["parameters"] as JsonObject;
            tools.Add(new BetaTool
            {
                Name = name,
                Description = function["description"]?.GetValue<string>(),
                InputSchema = new()
                {
                    Properties = parameters?["properties"] is JsonObject properties
                        ? properties.ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value))
                        : new Dictionary<string, JsonElement>(),
                    Required = parameters?["required"]?.AsArray().Select(r => r!.GetValue<string>()).ToList(),
                },
            });
        }

        return tools;
    }

    public static bool ToolsDisabled(JsonObject request) =>
        request["tool_choice"] is JsonValue choice && choice.GetValueKind() == JsonValueKind.String && choice.GetValue<string>() == "none";

    private static IEnumerable<JsonObject> Messages(JsonObject request) =>
        request["messages"]?.AsArray().OfType<JsonObject>() ?? [];

    private static string? RoleOf(JsonObject message) => message["role"]?.GetValue<string>();

    /// <summary><c>content</c> puede ser un string o una lista de partes <c>{type: "text", text}</c>.</summary>
    private static string? Text(JsonNode? content) => content switch
    {
        JsonValue value when value.GetValueKind() == JsonValueKind.String => value.GetValue<string>(),
        JsonArray parts => string.Concat(parts.Select(p => p?["text"]?.GetValue<string>())),
        _ => null,
    };

    private static Dictionary<string, JsonElement> Arguments(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
