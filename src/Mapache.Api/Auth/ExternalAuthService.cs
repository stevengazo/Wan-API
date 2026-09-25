using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mapache.Domain.Entities;
using Mapache.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Mapache.Api.Auth;

public enum ExternalFlowMode
{
    Login,
    Link,
}

/// <summary>
/// Lo que hay que recordar entre la ida al proveedor y la vuelta. Viaja cifrado en <c>state</c>, así no hace falta
/// guardarlo en el servidor; <see cref="Binding"/> lo ata a la cookie del navegador que lo inició (anti-CSRF).
/// </summary>
public sealed record ExternalFlow(ExternalProvider Provider, ExternalFlowMode Mode, Guid? UserId, string Binding, string Verifier, string Nonce);

public sealed record ExternalIdentity(ExternalProvider Provider, string Subject, string? Email, bool EmailVerified, string? Name);

public sealed class ExternalAuthException(string message) : Exception(message);

/// <summary>
/// OpenID Connect con Google y Microsoft (flujo de código con PKCE). El id_token se valida contra las llaves
/// publicadas por el proveedor: firma, emisor, audiencia, vencimiento y nonce.
/// </summary>
public sealed class ExternalAuthService(IHttpClientFactory httpFactory, IDataProtectionProvider protection, ISecretProtector secrets, ILogger<ExternalAuthService> logger)
{
    public const string BindingCookie = "mapache_sso";
    public static readonly TimeSpan FlowLifetime = TimeSpan.FromMinutes(10);

    // Inquilino de las cuentas personales de Microsoft (outlook.com, hotmail.com).
    private const string MicrosoftConsumersTenant = "9188040d-6c67-4c5b-b112-36a304b66dad";

    private static readonly ConcurrentDictionary<string, ConfigurationManager<OpenIdConnectConfiguration>> Metadata = new();

    private readonly ITimeLimitedDataProtector _state = protection.CreateProtector("Mapache.ExternalLogin.v1").ToTimeLimitedDataProtector();

    public static bool IsConfigured(SsoSettings s, ExternalProvider provider) => provider switch
    {
        ExternalProvider.Google => s.GoogleEnabled && s.GoogleClientId is not null && s.GoogleClientSecretEncrypted is not null,
        ExternalProvider.Microsoft => s.MicrosoftEnabled && s.MicrosoftClientId is not null && s.MicrosoftClientSecretEncrypted is not null,
        _ => false,
    };

    public static string DisplayName(ExternalProvider provider) => provider == ExternalProvider.Google ? "Google" : "Microsoft";

    public async Task<string> AuthorizeUrlAsync(SsoSettings settings, ExternalFlow flow, string redirectUri, CancellationToken ct)
    {
        var config = await ConfigurationAsync(settings, flow.Provider, ct);
        var challenge = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(flow.Verifier)));
        var query = new Dictionary<string, string?>
        {
            ["client_id"] = ClientId(settings, flow.Provider),
            ["response_type"] = "code",
            ["redirect_uri"] = redirectUri,
            ["scope"] = "openid email profile",
            ["state"] = _state.Protect(JsonSerializer.Serialize(flow), FlowLifetime),
            ["nonce"] = flow.Nonce,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            // Que elija la cuenta cada vez: evita entrar sin querer con la cuenta personal abierta en el navegador.
            ["prompt"] = "select_account",
        };
        return Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(config.AuthorizationEndpoint, query);
    }

    public static ExternalFlow NewFlow(ExternalProvider provider, ExternalFlowMode mode, Guid? userId) => new(
        provider, mode, userId, RandomToken(), RandomToken(), RandomToken());

    public ExternalFlow? ReadState(string? state)
    {
        if (string.IsNullOrEmpty(state))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ExternalFlow>(_state.Unprotect(state));
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Canjea el código por el id_token y lo valida.</summary>
    public async Task<ExternalIdentity> RedeemAsync(SsoSettings settings, ExternalFlow flow, string code, string redirectUri, CancellationToken ct)
    {
        var config = await ConfigurationAsync(settings, flow.Provider, ct);
        using var response = await httpFactory.CreateClient().PostAsync(
            config.TokenEndpoint,
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = redirectUri,
                ["client_id"] = ClientId(settings, flow.Provider),
                ["client_secret"] = ClientSecret(settings, flow.Provider),
                ["code_verifier"] = flow.Verifier,
            }),
            ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            // El detalle (p. ej. "invalid_client") sirve al administrador, no al usuario.
            logger.LogWarning("{Provider} rechazó el canje del código: {Status} {Body}", flow.Provider, (int)response.StatusCode, body);
            throw new ExternalAuthException($"{DisplayName(flow.Provider)} rechazó el inicio de sesión; revisa el Client ID y el secreto en Configuración");
        }

        var idToken = JsonDocument.Parse(body).RootElement.TryGetProperty("id_token", out var t) ? t.GetString() : null;
        if (idToken is null)
        {
            throw new ExternalAuthException($"{DisplayName(flow.Provider)} no devolvió la identidad");
        }

        var result = await new JsonWebTokenHandler().ValidateTokenAsync(idToken, new TokenValidationParameters
        {
            ValidAudience = ClientId(settings, flow.Provider),
            IssuerSigningKeys = config.SigningKeys,
            IssuerValidator = (issuer, token, _) => ValidateIssuer(settings, flow.Provider, issuer, (JsonWebToken)token),
            ClockSkew = TimeSpan.FromMinutes(2),
        });
        if (!result.IsValid)
        {
            logger.LogWarning(result.Exception, "id_token de {Provider} inválido", flow.Provider);
            throw new ExternalAuthException($"No se pudo verificar la identidad de {DisplayName(flow.Provider)}");
        }

        var claims = result.ClaimsIdentity;
        if (claims.FindFirst("nonce")?.Value != flow.Nonce)
        {
            throw new ExternalAuthException("El inicio de sesión no coincide; vuelve a intentarlo");
        }

        var subject = claims.FindFirst("sub")?.Value ?? throw new ExternalAuthException("La identidad no trae un identificador");
        var email = claims.FindFirst("email")?.Value;
        if (flow.Provider == ExternalProvider.Microsoft)
        {
            // Microsoft no garantiza que "email" esté verificado: se usa solo para mostrar y registrar, nunca para
            // encontrar una cuenta existente (ataque "nOAuth").
            email ??= claims.FindFirst("preferred_username")?.Value;
        }

        var verified = flow.Provider == ExternalProvider.Google && claims.FindFirst("email_verified")?.Value is "true" or "True";
        return new ExternalIdentity(flow.Provider, subject, email?.Trim().ToLowerInvariant(), verified, claims.FindFirst("name")?.Value);
    }

    private static string ValidateIssuer(SsoSettings settings, ExternalProvider provider, string issuer, JsonWebToken token)
    {
        if (provider == ExternalProvider.Google)
        {
            return issuer is "https://accounts.google.com" or "accounts.google.com"
                ? issuer
                : throw new SecurityTokenInvalidIssuerException($"Emisor inesperado: {issuer}");
        }

        // Con "common" u "organizations" el emisor depende del inquilino de la cuenta: se arma con su tid.
        var tenant = token.TryGetPayloadValue<string>("tid", out var tid) ? tid : null;
        if (tenant is null || issuer != $"https://login.microsoftonline.com/{tenant}/v2.0")
        {
            throw new SecurityTokenInvalidIssuerException($"Emisor inesperado: {issuer}");
        }

        var configured = settings.MicrosoftTenant;
        var allowed = configured switch
        {
            "common" => true,
            "organizations" => tenant != MicrosoftConsumersTenant,
            "consumers" => tenant == MicrosoftConsumersTenant,
            _ => string.Equals(tenant, configured, StringComparison.OrdinalIgnoreCase),
        };
        return allowed ? issuer : throw new SecurityTokenInvalidIssuerException("La cuenta no pertenece a la organización permitida");
    }

    private static Task<OpenIdConnectConfiguration> ConfigurationAsync(SsoSettings settings, ExternalProvider provider, CancellationToken ct)
    {
        var url = provider == ExternalProvider.Google
            ? "https://accounts.google.com/.well-known/openid-configuration"
            : $"https://login.microsoftonline.com/{Uri.EscapeDataString(settings.MicrosoftTenant)}/v2.0/.well-known/openid-configuration";
        var manager = Metadata.GetOrAdd(url, u => new ConfigurationManager<OpenIdConnectConfiguration>(
            u, new OpenIdConnectConfigurationRetriever(), new HttpDocumentRetriever { RequireHttps = true }));
        return manager.GetConfigurationAsync(ct);
    }

    private static string ClientId(SsoSettings s, ExternalProvider provider) =>
        (provider == ExternalProvider.Google ? s.GoogleClientId : s.MicrosoftClientId) ?? throw new ExternalAuthException("Proveedor sin configurar");

    private string ClientSecret(SsoSettings s, ExternalProvider provider) =>
        secrets.Unprotect((provider == ExternalProvider.Google ? s.GoogleClientSecretEncrypted : s.MicrosoftClientSecretEncrypted)
            ?? throw new ExternalAuthException("Proveedor sin configurar"));

    private static string RandomToken() => Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
}
