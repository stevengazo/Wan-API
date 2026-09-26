using System.Text.Json.Nodes;
using Mapache.Domain.Entities;
using Mapache.Infrastructure.Llm;

namespace Mapache.Tests.Llm;

public class ChatCompletionRouterTests
{
    private static JsonObject Request() => JsonNode.Parse("""
        {
          "model": "gpt-4o",
          "stream": true,
          "elevenlabs_extra_body": { "extension_id": "abc" },
          "messages": [
            { "role": "system", "content": "Prompt del agente" },
            { "role": "user", "content": "Hola" }
          ]
        }
        """)!.AsObject();

    [Fact]
    public void ReplacesModelAndRemovesExtraBody()
    {
        var request = Request();
        ChatCompletionRouter.Prepare(request, new LlmSelection(LlmProvider.Gemini, "gemini-2.5-flash", null));

        Assert.Equal("gemini-2.5-flash", request["model"]!.GetValue<string>());
        Assert.False(request.ContainsKey("elevenlabs_extra_body"));
        Assert.Equal("Prompt del agente", request["messages"]![0]!["content"]!.GetValue<string>());
    }

    [Fact]
    public void AccountPromptReplacesAgentPrompt()
    {
        var request = Request();
        ChatCompletionRouter.Prepare(request, new LlmSelection(LlmProvider.OpenAi, "gpt-4.1-mini", "Prompt de la cuenta"));

        var messages = request["messages"]!.AsArray();
        Assert.Equal(2, messages.Count);
        Assert.Equal("Prompt de la cuenta", messages[0]!["content"]!.GetValue<string>());
        Assert.Equal("user", messages[1]!["role"]!.GetValue<string>());
    }

    [Fact]
    public void DirectoryGoesAfterTheAgentPrompt()
    {
        var request = Request();
        ChatCompletionRouter.Prepare(request, new LlmSelection(LlmProvider.OpenAi, "gpt-4.1-mini", null), "Directorio");

        var messages = request["messages"]!.AsArray();
        Assert.Equal(["Prompt del agente", "Directorio", "Hola"], messages.Select(m => m!["content"]!.GetValue<string>()));
    }

    [Fact]
    public void DirectoryGoesAfterTheAccountPrompt()
    {
        var request = Request();
        ChatCompletionRouter.Prepare(request, new LlmSelection(LlmProvider.OpenAi, "gpt-4.1-mini", "Prompt general"), "Directorio");

        var messages = request["messages"]!.AsArray();
        Assert.Equal(["Prompt general", "Directorio", "Hola"], messages.Select(m => m!["content"]!.GetValue<string>()));
    }
}
