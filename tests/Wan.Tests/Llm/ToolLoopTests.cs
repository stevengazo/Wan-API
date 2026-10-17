using System.Text;
using System.Text.Json.Nodes;
using Wan.Infrastructure.Llm;
using Wan.Infrastructure.Tools;

namespace Wan.Tests.Llm;

public class ToolLoopTests
{
    private static string Chunk(object delta, string? finish = null, string eol = "\n") =>
        "data: " + new JsonObject
        {
            ["choices"] = new JsonArray(new JsonObject
            {
                ["index"] = 0,
                ["delta"] = System.Text.Json.JsonSerializer.SerializeToNode(delta),
                ["finish_reason"] = finish,
            }),
        }.ToJsonString() + eol + eol;

    [Fact]
    public async Task TextPassesThroughAndToolCallsAreHeld()
    {
        using var output = new MemoryStream();
        var interceptor = new SseToolInterceptor(output);
        var stream = Chunk(new { content = "Déjame revisar" })
            + Chunk(new { tool_calls = new[] { new { index = 0, id = "call_1", type = "function", function = new { name = "consultar_pedido", arguments = "{\"num" } } } })
            + Chunk(new { tool_calls = new[] { new { index = 0, function = new { arguments = "ero\":\"123\"}" } } } })
            + Chunk(new { }, "tool_calls")
            + "data: [DONE]\n\n";

        // Trozos que cortan eventos a la mitad, como llegan de la red.
        var bytes = Encoding.UTF8.GetBytes(stream);
        for (var i = 0; i < bytes.Length; i += 37)
        {
            await interceptor.WriteAsync(bytes.AsMemory(i, Math.Min(37, bytes.Length - i)));
        }

        var passed = Encoding.UTF8.GetString(output.ToArray());
        // El JSON del fragmento escapa la tilde; lo que importa es que el texto haya pasado.
        Assert.Contains("jame revisar", passed);
        Assert.DoesNotContain("consultar_pedido", passed);
        Assert.DoesNotContain("[DONE]", passed);

        var call = Assert.Single(interceptor.ToolCalls);
        Assert.Equal(("call_1", "consultar_pedido", "{\"numero\":\"123\"}"), (call.Id, call.Name, call.Arguments.ToString()));
        Assert.Equal("Déjame revisar", interceptor.Text);

        await interceptor.ReleaseHeldAsync(CancellationToken.None);
        Assert.EndsWith("data: [DONE]\n\n", Encoding.UTF8.GetString(output.ToArray()));
    }

    [Fact]
    public async Task HandlesCrlfEventTerminators()
    {
        using var output = new MemoryStream();
        var interceptor = new SseToolInterceptor(output);
        // Un proveedor "OpenAI-compatible" que cierra los eventos con CRLF (\r\n\r\n) en vez de LF.
        var stream = Chunk(new { content = "Hola" }, eol: "\r\n")
            + Chunk(new { tool_calls = new[] { new { index = 0, id = "call_1", type = "function", function = new { name = "consultar_pedido", arguments = "{}" } } } }, eol: "\r\n")
            + Chunk(new { }, "tool_calls", eol: "\r\n")
            + "data: [DONE]\r\n\r\n";

        var bytes = Encoding.UTF8.GetBytes(stream);
        for (var i = 0; i < bytes.Length; i += 37)
        {
            await interceptor.WriteAsync(bytes.AsMemory(i, Math.Min(37, bytes.Length - i)));
        }

        var passed = Encoding.UTF8.GetString(output.ToArray());
        // El texto debe fluir en vivo; la tool call y el [DONE] quedan retenidos, igual que con LF.
        Assert.Contains("Hola", passed);
        Assert.DoesNotContain("consultar_pedido", passed);
        Assert.DoesNotContain("[DONE]", passed);

        var call = Assert.Single(interceptor.ToolCalls);
        Assert.Equal(("call_1", "consultar_pedido"), (call.Id, call.Name));
        Assert.Equal("Hola", interceptor.Text);
    }

    [Fact]
    public void AgentToolsWinOverWanToolsWithTheSameName()
    {
        var request = JsonNode.Parse("""{"tools":[{"type":"function","function":{"name":"end_call"}}]}""")!.AsObject();
        var tools = new Dictionary<string, BotTool>
        {
            ["end_call"] = new("end_call", "x", [], (_, _) => Task.FromResult("")),
            ["consultar_pedido"] = new("consultar_pedido", "Consulta", ToolSchema.Object(("numero", "string", "Número", true)), (_, _) => Task.FromResult("")),
        };

        var added = BotToolRegistry.AddTo(request, tools);

        Assert.Equal(["consultar_pedido"], added.Keys);
        Assert.Equal(2, request["tools"]!.AsArray().Count);
        Assert.Equal("numero", request["tools"]![1]!["function"]!["parameters"]!["required"]![0]!.GetValue<string>());
    }

    [Fact]
    public void RendersUrlAndBodyTemplates()
    {
        var args = JsonNode.Parse("""{"numero":"A 12/3","cantidad":2,"urgente":true}""")!.AsObject();

        Assert.Equal("https://erp/pedidos/A%2012%2F3?x=", HttpToolRunner.RenderUrl("https://erp/pedidos/{{numero}}?x={{falta}}", args));
        Assert.Equal("""{"n":"A 12/3","c":2,"u":true,"f":null}""",
            HttpToolRunner.RenderBody("""{"n":{{numero}},"c":{{ cantidad }},"u":{{urgente}},"f":{{falta}}}""", args));
    }

    [Fact]
    public void DescribesResponsesForTheBot()
    {
        Assert.Equal("""{"estado":"enviado"}""", HttpToolRunner.Describe(200, "{ \"estado\": \"enviado\" }"));
        Assert.StartsWith("Error: el sistema externo respondió 404.", HttpToolRunner.Describe(404, "no existe"));
        Assert.EndsWith("(respuesta recortada)", HttpToolRunner.Describe(200, new string('x', 5000)));
    }
}
