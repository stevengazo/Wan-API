using System.Text;
using System.Text.Json.Nodes;
using Mapache.Infrastructure.Llm;

namespace Mapache.Tests.Llm;

public class ChatCompletionChunkWriterTests
{
    [Fact]
    public async Task WritesOpenAiStreamingFormat()
    {
        using var output = new MemoryStream();
        var writer = new ChatCompletionChunkWriter(output, "claude-opus-5", TimeProvider.System);

        await writer.WriteContentAsync("Hola", CancellationToken.None);
        await writer.WriteToolCallStartAsync(0, "toolu_1", "end_call", CancellationToken.None);
        await writer.WriteToolArgumentsAsync(0, "{}", CancellationToken.None);
        await writer.WriteFinishAsync("tool_calls", CancellationToken.None);

        var events = Encoding.UTF8.GetString(output.ToArray())
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(e => e["data: ".Length..])
            .ToList();

        Assert.Equal("[DONE]", events[^1]);
        var chunks = events[..^1].Select(e => JsonNode.Parse(e)!).ToList();

        Assert.All(chunks, c => Assert.Equal("chat.completion.chunk", c["object"]!.GetValue<string>()));
        Assert.Single(chunks.Select(c => c["id"]!.GetValue<string>()).Distinct());

        var first = chunks[0]!["choices"]![0]!["delta"]!;
        Assert.Equal("assistant", first["role"]!.GetValue<string>());
        Assert.Equal("Hola", first["content"]!.GetValue<string>());
        Assert.Null(chunks[1]!["choices"]![0]!["delta"]!["role"]);

        var toolCall = chunks[1]!["choices"]![0]!["delta"]!["tool_calls"]![0]!;
        Assert.Equal("end_call", toolCall["function"]!["name"]!.GetValue<string>());
        Assert.Equal("tool_calls", chunks[^1]!["choices"]![0]!["finish_reason"]!.GetValue<string>());
    }
}
