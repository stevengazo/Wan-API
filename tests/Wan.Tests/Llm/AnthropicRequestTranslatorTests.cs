using System.Text.Json;
using System.Text.Json.Nodes;
using Wan.Infrastructure.Llm;

namespace Wan.Tests.Llm;

public class AnthropicRequestTranslatorTests
{
    // Forma en que ElevenLabs manda la conversación: saludo del agente primero, luego una tool con su resultado.
    private static JsonObject Request() => JsonNode.Parse("""
        {
          "model": "lo-que-diga-elevenlabs",
          "messages": [
            { "role": "system", "content": "Eres la recepcionista." },
            { "role": "assistant", "content": "Hola, ¿en qué te ayudo?" },
            { "role": "user", "content": [{ "type": "text", "text": "Quiero hablar con ventas" }] },
            { "role": "assistant", "content": null, "tool_calls": [
              { "id": "call_1", "type": "function", "function": { "name": "transfer_call", "arguments": "{\"target\":\"ventas\"}" } },
              { "id": "call_2", "type": "function", "function": { "name": "end_call", "arguments": "" } }
            ] },
            { "role": "tool", "tool_call_id": "call_1", "content": "ok" },
            { "role": "tool", "tool_call_id": "call_2", "content": "ok" }
          ],
          "tools": [
            { "type": "function", "function": { "name": "transfer_call", "description": "Transfiere",
              "parameters": { "type": "object", "properties": { "target": { "type": "string" } }, "required": ["target"] } } }
          ],
          "tool_choice": "none"
        }
        """)!.AsObject();

    private static JsonArray Serialize(object value) => JsonNode.Parse(JsonSerializer.Serialize(value))!.AsArray();

    [Fact]
    public void JoinsSystemMessages() =>
        Assert.Equal("Eres la recepcionista.", AnthropicRequestTranslator.System(Request()));

    [Fact]
    public void StartsWithUserAndGroupsToolResults()
    {
        var messages = Serialize(AnthropicRequestTranslator.Conversation(Request()));

        Assert.Equal(["user", "assistant", "user", "assistant", "user"],
            messages.Select(m => m!["role"]!.GetValue<string>()));
        Assert.Equal(AnthropicRequestTranslator.CallStartedPlaceholder, messages[0]!["content"]![0]!["text"]!.GetValue<string>());

        var toolUses = messages[3]!["content"]!.AsArray();
        Assert.Equal("transfer_call", toolUses[0]!["name"]!.GetValue<string>());
        Assert.Equal("ventas", toolUses[0]!["input"]!["target"]!.GetValue<string>());
        Assert.Empty(toolUses[1]!["input"]!.AsObject());

        var results = messages[4]!["content"]!.AsArray();
        Assert.Equal(["call_1", "call_2"], results.Select(r => r!["tool_use_id"]!.GetValue<string>()));
    }

    [Fact]
    public void TranslatesToolDefinitions()
    {
        var tool = Serialize(AnthropicRequestTranslator.Tools(Request()))[0]!;

        Assert.Equal("transfer_call", tool["name"]!.GetValue<string>());
        Assert.Equal("string", tool["input_schema"]!["properties"]!["target"]!["type"]!.GetValue<string>());
        Assert.Equal("target", tool["input_schema"]!["required"]![0]!.GetValue<string>());
    }

    [Fact]
    public void DetectsToolChoiceNone() => Assert.True(AnthropicRequestTranslator.ToolsDisabled(Request()));
}
