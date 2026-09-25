using Mapache.Infrastructure.Tools;
using ModelContextProtocol.Protocol;

namespace Mapache.Tests.Llm;

public class McpConnectorTests
{
    [Theory]
    [InlineData("GitHub", "search_issues", "mcp_github_search_issues")]
    [InlineData("Sistema Contable ñandú", "Buscar-Factura", "mcp_sistema_contable_nandu_buscar_factura")]
    public void ExposedNamesAreSafeForTheProviders(string server, string tool, string expected) =>
        Assert.Equal(expected, McpConnector.ExposedName(server, tool));

    [Fact]
    public void ExposedNamesAreCappedAt64() =>
        Assert.Equal(64, McpConnector.ExposedName(new string('a', 50), new string('b', 50)).Length);

    [Fact]
    public void DescribesTextAndErrors()
    {
        var ok = new CallToolResult { Content = [new TextContentBlock { Text = "Pedido 123: enviado" }] };
        var error = new CallToolResult { Content = [new TextContentBlock { Text = "no existe" }], IsError = true };

        Assert.Equal("Pedido 123: enviado", McpConnector.Describe(ok));
        Assert.Equal("Error de la herramienta: no existe", McpConnector.Describe(error));
    }
}
