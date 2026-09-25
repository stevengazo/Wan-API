using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mapache.Api.Auth;
using Mapache.Domain.Entities;
using Mapache.Infrastructure.Persistence;
using Mapache.Infrastructure.Security;
using Mapache.Infrastructure.Tools;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Mapache.Api.Controllers;

public sealed record McpToolDto(string Name, string ExposedName, string? Description, bool Enabled);

/// <summary>Los valores de los headers no salen de la API: solo sus nombres.</summary>
public sealed record McpServerDto(
    Guid Id,
    string Name,
    string Url,
    IReadOnlyList<string> HeaderNames,
    bool Enabled,
    IReadOnlyList<McpToolDto> Tools,
    string? LastError,
    DateTimeOffset? LastSyncedAt);

/// <summary><c>Headers</c> nulo conserva los guardados. <c>DisabledTools</c>: herramientas que el bot no puede usar.</summary>
public sealed record SaveMcpServerRequest(
    [Required(ErrorMessage = Messages.Required), StringLength(64, ErrorMessage = Messages.MaxLength)] string Name,
    [Required(ErrorMessage = Messages.Required), StringLength(2000, ErrorMessage = Messages.MaxLength)] string Url,
    Dictionary<string, string>? Headers,
    IReadOnlyList<string>? DisabledTools,
    bool Enabled = true);

public sealed record TestMcpToolRequest(JsonObject Arguments);

[ApiController]
[Authorize(Roles = Roles.Admin)]
public sealed class McpServersController(
    MapacheDbContext db,
    ISecretProtector protector,
    McpConnector connector,
    TimeProvider time) : ControllerBase
{
    [HttpGet("api/mcp-servers")]
    public async Task<IReadOnlyList<McpServerDto>> List(CancellationToken ct) =>
        (await db.McpServers.AsNoTracking().OrderBy(s => s.Name).ToListAsync(ct)).Select(ToDto).ToList();

    /// <summary>Guarda y se conecta en el momento para leer las herramientas; si falla, queda guardado con el error.</summary>
    [HttpPost("api/mcp-servers")]
    public async Task<ActionResult<McpServerDto>> Create(SaveMcpServerRequest request, CancellationToken ct)
    {
        if (Validate(request) is { } error)
        {
            return error;
        }

        var server = new McpServerConnection { Name = "", Url = "", CreatedAt = time.GetUtcNow() };
        Apply(server, request);
        await connector.SyncAsync(server, ct);
        ApplyDisabledTools(server, request.DisabledTools);
        db.McpServers.Add(server);
        await db.SaveChangesAsync(ct);
        return ToDto(server);
    }

    [HttpPut("api/mcp-servers/{id:guid}")]
    public async Task<ActionResult<McpServerDto>> Update(Guid id, SaveMcpServerRequest request, CancellationToken ct)
    {
        var server = await db.McpServers.FindAsync([id], ct);
        if (server is null)
        {
            return NotFound();
        }

        if (Validate(request) is { } error)
        {
            return error;
        }

        var reconnect = server.Url != request.Url.Trim() || request.Headers is not null;
        Apply(server, request);
        if (reconnect)
        {
            await connector.SyncAsync(server, ct);
        }

        ApplyDisabledTools(server, request.DisabledTools);
        await db.SaveChangesAsync(ct);
        return ToDto(server);
    }

    /// <summary>Vuelve a leer las herramientas (si el servidor agregó o cambió alguna).</summary>
    [HttpPost("api/mcp-servers/{id:guid}/sync")]
    public async Task<ActionResult<McpServerDto>> Sync(Guid id, CancellationToken ct)
    {
        var server = await db.McpServers.FindAsync([id], ct);
        if (server is null)
        {
            return NotFound();
        }

        await connector.SyncAsync(server, ct);
        // Lista nueva: la columna jsonb se compara por referencia.
        server.Tools = [.. server.Tools];
        await db.SaveChangesAsync(ct);
        return ToDto(server);
    }

    /// <summary>Llama a una herramienta del servidor con valores de prueba y devuelve lo que recibiría el bot.</summary>
    [HttpPost("api/mcp-servers/{id:guid}/tools/{tool}/test")]
    public async Task<ActionResult<object>> TestTool(Guid id, string tool, TestMcpToolRequest request, CancellationToken ct)
    {
        var server = await db.McpServers.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, ct);
        if (server is null || server.Tools.All(t => t.Name != tool))
        {
            return NotFound();
        }

        try
        {
            return new { result = await connector.CallAsync(server, tool, request.Arguments, ct) };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new { result = $"Error: {ex.Message}" };
        }
    }

    [HttpDelete("api/mcp-servers/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct) =>
        await db.McpServers.Where(s => s.Id == id).ExecuteDeleteAsync(ct) == 0 ? NotFound() : NoContent();

    private ActionResult? Validate(SaveMcpServerRequest request)
    {
        if (!Uri.TryCreate(request.Url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
        {
            ModelState.AddModelError(nameof(SaveMcpServerRequest.Url), "Debe ser una URL http(s)");
        }

        return ModelState.IsValid ? null : ValidationProblem(ModelState);
    }

    private void Apply(McpServerConnection server, SaveMcpServerRequest request)
    {
        server.Name = request.Name.Trim();
        server.Url = request.Url.Trim();
        server.Enabled = request.Enabled;
        if (request.Headers is not null)
        {
            var headers = request.Headers.Where(h => !string.IsNullOrWhiteSpace(h.Key)).ToDictionary(h => h.Key.Trim(), h => h.Value);
            server.HeadersEncrypted = headers.Count == 0 ? null : protector.Protect(JsonSerializer.Serialize(headers));
        }
    }

    /// <summary>Se aplica después de sincronizar, cuando ya se conocen las herramientas del servidor.</summary>
    private static void ApplyDisabledTools(McpServerConnection server, IReadOnlyList<string>? disabledTools)
    {
        if (disabledTools is null)
        {
            return;
        }

        var disabled = disabledTools.ToHashSet();
        // Lista nueva: la columna jsonb se compara por referencia.
        server.Tools = server.Tools.Select(t => new McpToolInfo
        {
            Name = t.Name,
            Description = t.Description,
            InputSchema = t.InputSchema,
            Enabled = !disabled.Contains(t.Name),
        }).ToList();
    }

    private McpServerDto ToDto(McpServerConnection s) => new(
        s.Id,
        s.Name,
        s.Url,
        s.HeadersEncrypted is null ? [] : JsonSerializer.Deserialize<Dictionary<string, string>>(protector.Unprotect(s.HeadersEncrypted))!.Keys.ToList(),
        s.Enabled,
        s.Tools.Select(t => new McpToolDto(t.Name, McpConnector.ExposedName(s.Name, t.Name), t.Description, t.Enabled)).ToList(),
        s.LastError,
        s.LastSyncedAt);
}
