using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Wan.Domain.Entities;
using Wan.Infrastructure.Persistence;
using Wan.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Wan.Infrastructure.Tools;

/// <summary>Se conecta a servidores MCP externos para leer sus herramientas y ejecutarlas.</summary>
public sealed partial class McpConnector(ISecretProtector protector, TimeProvider time, ILoggerFactory loggers)
{
    public const int MaxResultChars = 4000;

    /// <summary>Lee las herramientas del servidor y las guarda, conservando cuáles estaban deshabilitadas.</summary>
    public async Task SyncAsync(McpServerConnection server, CancellationToken ct)
    {
        try
        {
            await using var client = await ConnectAsync(server, ct);
            var disabled = server.Tools.Where(t => !t.Enabled).Select(t => t.Name).ToHashSet();
            var tools = await client.ListToolsAsync(cancellationToken: ct);
            server.Tools = tools.Select(t => new McpToolInfo
            {
                Name = t.Name,
                Description = t.Description,
                InputSchema = t.JsonSchema.GetRawText(),
                Enabled = !disabled.Contains(t.Name),
            }).ToList();
            server.LastError = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            server.LastError = Truncate($"No se pudo conectar: {ex.Message}", 1000);
        }

        server.LastSyncedAt = time.GetUtcNow();
    }

    public async Task<string> CallAsync(McpServerConnection server, string toolName, JsonObject arguments, CancellationToken ct)
    {
        await using var client = await ConnectAsync(server, ct);
        var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(arguments.ToJsonString())!
            .ToDictionary(a => a.Key, a => (object?)a.Value);
        var result = await client.CallToolAsync(toolName, args, cancellationToken: ct);
        return Describe(result);
    }

    /// <summary>Texto de la respuesta para el bot: los bloques de texto, o el contenido estructurado.</summary>
    public static string Describe(CallToolResult result)
    {
        var text = new StringBuilder();
        foreach (var block in result.Content.OfType<TextContentBlock>())
        {
            text.AppendLine(block.Text);
        }

        if (text.Length == 0 && result.StructuredContent is { } structured)
        {
            text.Append(structured.GetRawText());
        }

        var body = Truncate(text.ToString().Trim(), MaxResultChars);
        return result.IsError == true ? $"Error de la herramienta: {body}" : body.Length == 0 ? "(sin contenido)" : body;
    }

    /// <summary>
    /// Nombre de la herramienta para el modelo: <c>mcp_&lt;servidor&gt;_&lt;herramienta&gt;</c>, con solo los caracteres
    /// que aceptan los proveedores y como máximo 64.
    /// </summary>
    public static string ExposedName(string serverName, string toolName)
    {
        var name = $"mcp_{Slug(serverName)}_{Slug(toolName)}";
        return name.Length <= 64 ? name : name[..64];
    }

    private async Task<McpClient> ConnectAsync(McpServerConnection server, CancellationToken ct)
    {
        var headers = server.HeadersEncrypted is null
            ? new Dictionary<string, string>()
            : JsonSerializer.Deserialize<Dictionary<string, string>>(protector.Unprotect(server.HeadersEncrypted)) ?? [];
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(server.Url),
            Name = server.Name,
            AdditionalHeaders = headers,
            ConnectionTimeout = TimeSpan.FromSeconds(10),
        }, loggers);
        return await McpClient.CreateAsync(transport, loggerFactory: loggers, cancellationToken: ct);
    }

    private static string Slug(string text)
    {
        var slug = NonAlphanumeric().Replace(Knowledge.DocumentText.Normalize(text), "_").Trim('_');
        return slug.Length == 0 ? "x" : slug;
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonAlphanumeric();
}

/// <summary>Herramientas habilitadas de los servidores MCP activos, con la lista guardada en la última sincronización.</summary>
public sealed class McpToolSource(WanDbContext db, McpConnector connector) : IBotToolSource
{
    public async Task<IReadOnlyList<BotTool>> GetToolsAsync(BotCallContext call, CancellationToken ct)
    {
        var servers = await db.McpServers.AsNoTracking().Where(s => s.Enabled).ToListAsync(ct);
        return servers
            .SelectMany(server => server.Tools.Where(t => t.Enabled).Select(tool => new BotTool(
                McpConnector.ExposedName(server.Name, tool.Name),
                $"{tool.Description} (vía {server.Name})".Trim(),
                JsonNode.Parse(tool.InputSchema) as JsonObject ?? new JsonObject { ["type"] = "object" },
                (args, token) => connector.CallAsync(server, tool.Name, args, token))))
            .ToList();
    }
}
