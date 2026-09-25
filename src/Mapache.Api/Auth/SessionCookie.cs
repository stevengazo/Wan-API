using Microsoft.AspNetCore.Authentication.JwtBearer;

namespace Mapache.Api.Auth;

/// <summary>
/// Sesión del panel en una cookie HttpOnly: el JWT no queda al alcance de JavaScript, así un XSS no puede robarlo.
/// </summary>
public static class SessionCookie
{
    public const string Name = "mapache_session";

    /// <summary>
    /// Anti-CSRF: en métodos que cambian datos la cookie solo vale si viene este header. Un sitio ajeno no puede
    /// agregarlo sin un preflight CORS, que la API no habilita.
    /// </summary>
    public const string ClientHeader = "X-Mapache-Client";

    public static void Append(HttpContext context, string token, DateTimeOffset expiresAt) =>
        context.Response.Cookies.Append(Name, token, Options(context, expiresAt));

    public static void Delete(HttpContext context) =>
        context.Response.Cookies.Delete(Name, Options(context, null));

    /// <summary>
    /// Toma el JWT de la cookie cuando no viene en el header Authorization. Así también lo reciben SignalR y el
    /// WebSocket del teléfono (el navegador manda la cookie en el handshake).
    /// </summary>
    public static Task ReadToken(MessageReceivedContext context)
    {
        var request = context.Request;
        if (request.Headers.Authorization.Count > 0 || request.Cookies[Name] is not { Length: > 0 } token)
        {
            return Task.CompletedTask;
        }

        if (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method) || HttpMethods.IsOptions(request.Method)
            || request.Headers.ContainsKey(ClientHeader))
        {
            context.Token = token;
        }

        return Task.CompletedTask;
    }

    private static CookieOptions Options(HttpContext context, DateTimeOffset? expiresAt) => new()
    {
        HttpOnly = true,
        // Detrás de nginx, IsHttps sale de X-Forwarded-Proto (ReverseProxy:Enabled).
        Secure = context.Request.IsHttps,
        // Strict: la cookie no viaja en pedidos iniciados desde otro sitio (tampoco en WebSockets).
        SameSite = SameSiteMode.Strict,
        Path = "/",
        Expires = expiresAt,
    };
}
