using System.ComponentModel.DataAnnotations;
using Mapache.Api.Auth;
using Mapache.Api.Mcp;
using Mapache.Domain.Entities;
using Mapache.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Mapache.Api.Controllers;

public sealed record McpAccessTokenDto(Guid Id, string Name, string Prefix, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt);

/// <summary>El token completo solo viaja en esta respuesta; después no se puede recuperar.</summary>
public sealed record CreatedMcpAccessTokenDto(McpAccessTokenDto Token, string Value);

public sealed record CreateMcpAccessTokenRequest(
    [Required(ErrorMessage = Messages.Required), StringLength(128, ErrorMessage = Messages.MaxLength)] string Name);

/// <summary>Tokens para el servidor MCP de Mapache.</summary>
[ApiController]
[Authorize(Roles = Roles.Admin)]
public sealed class McpAccessController(MapacheDbContext db, TimeProvider time) : ControllerBase
{
    [HttpGet("api/mcp-tokens")]
    public async Task<IReadOnlyList<McpAccessTokenDto>> List(CancellationToken ct) =>
        await db.McpAccessTokens.AsNoTracking()
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => new McpAccessTokenDto(t.Id, t.Name, t.Prefix, t.CreatedAt, t.LastUsedAt))
            .ToListAsync(ct);

    [HttpPost("api/mcp-tokens")]
    public async Task<CreatedMcpAccessTokenDto> Create(CreateMcpAccessTokenRequest request, CancellationToken ct)
    {
        var value = McpTokenAuthenticationHandler.NewToken();
        var token = new McpAccessToken
        {
            Name = request.Name.Trim(),
            TokenHash = McpTokenAuthenticationHandler.Hash(value),
            Prefix = value[..(McpTokenAuthenticationHandler.TokenPrefix.Length + 4)],
            CreatedAt = time.GetUtcNow(),
        };
        db.McpAccessTokens.Add(token);
        await db.SaveChangesAsync(ct);
        return new CreatedMcpAccessTokenDto(new McpAccessTokenDto(token.Id, token.Name, token.Prefix, token.CreatedAt, null), value);
    }

    [HttpDelete("api/mcp-tokens/{id:guid}")]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken ct) =>
        await db.McpAccessTokens.Where(t => t.Id == id).ExecuteDeleteAsync(ct) == 0 ? NotFound() : NoContent();
}
