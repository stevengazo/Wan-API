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

        var identity = new ClaimsIdentity([new Claim("sub", $"mcp:{token.Id}"), new Claim("name", token.Name)], SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static string NewToken() => TokenPrefix + Base64Url(RandomNumberGenerator.GetBytes(32));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
