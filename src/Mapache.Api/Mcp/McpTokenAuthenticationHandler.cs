using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Mapache.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Mapache.Api.Mcp;

/// <summary>
/// Esquema del servidor MCP de Mapache: <c>Authorization: Bearer &lt;token&gt;</c> con un token creado en el panel.
/// Aparte del JWT de usuarios y del token de tools del agente.
/// </summary>
public sealed class McpTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> schemeOptions,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    MapacheDbContext db,
    TimeProvider time)
    : AuthenticationHandler<AuthenticationSchemeOptions>(schemeOptions, loggerFactory, encoder)
{
    public const string SchemeName = "McpToken";
    public const string PolicyName = "Mcp";
    public const string TokenPrefix = "mapache_mcp_";

    /// <summary>Claim con cada herramienta permitida; <see cref="AllToolsValue"/> permite todas.</summary>
    public const string ToolClaim = "mcp_tool";
    public const string AllToolsValue = "*";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        var hash = Hash(header["Bearer ".Length..].Trim());
        var token = await db.McpAccessTokens.SingleOrDefaultAsync(t => t.TokenHash == hash, Context.RequestAborted);
        if (token is null)
        {
            return AuthenticateResult.Fail("Token MCP inválido");
        }

        // Una escritura por minuto como mucho: el cliente MCP hace varias peticiones seguidas.
        var now = time.GetUtcNow();
        if (token.LastUsedAt is null || now - token.LastUsedAt > TimeSpan.FromMinutes(1))
        {
            token.LastUsedAt = now;
            await db.SaveChangesAsync(Context.RequestAborted);
        }

        var claims = new List<Claim> { new("sub", $"mcp:{token.Id}"), new("name", token.Name) };
        claims.AddRange(token.AllowAll
            ? [new Claim(ToolClaim, AllToolsValue)]
            : token.AllowedTools.Select(t => new Claim(ToolClaim, t)));
        var identity = new ClaimsIdentity(claims, SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    /// <summary>Si el usuario del token puede usar la herramienta.</summary>
    public static bool CanUse(ClaimsPrincipal? user, string tool) =>
        user?.FindAll(ToolClaim).Any(c => c.Value == AllToolsValue || c.Value == tool) == true;

    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static string NewToken() => TokenPrefix + Base64Url(RandomNumberGenerator.GetBytes(32));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
