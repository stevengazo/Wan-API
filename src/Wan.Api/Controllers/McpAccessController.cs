using Mapache.Api.Audit;
using System.ComponentModel.DataAnnotations;
using Mapache.Api.Auth;
using Mapache.Api.Mcp;
using Mapache.Domain.Entities;
using Mapache.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Mapache.Api.Controllers;

public sealed record McpAccessTokenDto(
    Guid Id,
    string Name,
    string Prefix,
    bool AllowAll,
    IReadOnlyList<string> AllowedTools,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastUsedAt);

/// <summary>El token completo solo viaja en esta respuesta; después no se puede recuperar.</summary>
public sealed record CreatedMcpAccessTokenDto(McpAccessTokenDto Token, string Value);

public sealed record CreateMcpAccessTokenRequest(
    [Required(ErrorMessage = Messages.Required), StringLength(128, ErrorMessage = Messages.MaxLength)] string Name,
    bool AllowAll = true,
    IReadOnlyList<string>? AllowedTools = null);

public sealed record UpdateMcpAccessTokenRequest(bool AllowAll, IReadOnlyList<string>? AllowedTools);

/// <summary>Tokens para el servidor MCP de Mapache.</summary>
[ApiController]
[Authorize(Roles = Roles.Admin)]
public sealed class McpAccessController(MapacheDbContext db, TimeProvider time, AuditLog audit) : ControllerBase
{
    [HttpGet("api/mcp-tokens")]
    public async Task<IReadOnlyList<McpAccessTokenDto>> List(CancellationToken ct) =>
        (await db.McpAccessTokens.AsNoTracking().OrderByDescending(t => t.CreatedAt).ToListAsync(ct)).Select(ToDto).ToList();

    /// <summary>Herramientas del servidor MCP de Mapache, para elegir los permisos de cada token.</summary>
    [HttpGet("api/mcp-tokens/tools")]
    public IReadOnlyList<McpToolDescriptor> Tools() => McpToolPermissions.All;

    [HttpPut("api/mcp-tokens/{id:guid}")]
    public async Task<ActionResult<McpAccessTokenDto>> Update(Guid id, UpdateMcpAccessTokenRequest request, CancellationToken ct)
    {
        var token = await db.McpAccessTokens.FindAsync([id], ct);
        if (token is null)
        {
            return NotFound();
        }

        token.AllowAll = request.AllowAll;
        token.AllowedTools = KnownTools(request.AllowedTools);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(AuditActions.McpTokenChanged, token.Name, token.Id.ToString(), token.AllowAll ? "Acceso total" : string.Join(", ", token.AllowedTools));
        return ToDto(token);
    }

    [HttpPost("api/mcp-tokens")]
    public async Task<CreatedMcpAccessTokenDto> Create(CreateMcpAccessTokenRequest request, CancellationToken ct)
    {
        var value = McpTokenAuthenticationHandler.NewToken();
        var token = new McpAccessToken
        {
            Name = request.Name.Trim(),
            TokenHash = McpTokenAuthenticationHandler.Hash(value),
            Prefix = value[..(McpTokenAuthenticationHandler.TokenPrefix.Length + 4)],
            AllowAll = request.AllowAll,
            AllowedTools = KnownTools(request.AllowedTools),
            CreatedAt = time.GetUtcNow(),
        };
        db.McpAccessTokens.Add(token);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(AuditActions.McpTokenCreated, token.Name, token.Id.ToString(), token.AllowAll ? "Acceso total" : string.Join(", ", token.AllowedTools));
        return new CreatedMcpAccessTokenDto(ToDto(token), value);
    }

    /// <summary>Solo nombres de herramientas que existen; lista nueva porque la columna jsonb se compara por referencia.</summary>
    private static List<string> KnownTools(IReadOnlyList<string>? tools) =>
        (tools ?? []).Where(t => McpToolPermissions.All.Any(k => k.Name == t)).Distinct().ToList();

    private static McpAccessTokenDto ToDto(McpAccessToken t) =>
        new(t.Id, t.Name, t.Prefix, t.AllowAll, t.AllowedTools, t.CreatedAt, t.LastUsedAt);

    [HttpDelete("api/mcp-tokens/{id:guid}")]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken ct)
    {
        var name = await db.McpAccessTokens.Where(t => t.Id == id).Select(t => t.Name).SingleOrDefaultAsync(ct);
        if (name is null)
        {
            return NotFound();
        }

        await db.McpAccessTokens.Where(t => t.Id == id).ExecuteDeleteAsync(ct);
        await audit.RecordAsync(AuditActions.McpTokenRevoked, name, id.ToString());
        return NoContent();
    }
}
