using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Mapache.Api.Webhooks;

/// <summary>
/// Esquema de autenticación para los server tools del agente: <c>Authorization: Bearer &lt;ToolToken&gt;</c>.
/// Es independiente del JWT de usuarios: un token de usuario no sirve para los tools ni al revés.
/// </summary>
public sealed class AgentToolAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> schemeOptions,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    IOptions<AgentWebhookOptions> webhookOptions)
    : AuthenticationHandler<AuthenticationSchemeOptions>(schemeOptions, loggerFactory, encoder)
{
    public const string SchemeName = "AgentTool";
    public const string PolicyName = "AgentTool";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var expected = webhookOptions.Value.ToolToken;
        if (string.IsNullOrEmpty(expected))
        {
            return Task.FromResult(AuthenticateResult.Fail("AgentWebhooks:ToolToken no está configurado"));
        }

        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var provided = header["Bearer ".Length..].Trim();
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(expected)))
        {
            return Task.FromResult(AuthenticateResult.Fail("Token de tool inválido"));
        }

        var identity = new ClaimsIdentity([new Claim("sub", "elevenlabs-agent")], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
