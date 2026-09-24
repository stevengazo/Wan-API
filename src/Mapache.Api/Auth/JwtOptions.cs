namespace Mapache.Api.Auth;

public sealed class JwtOptions
{
    public const string SectionName = "Auth:Jwt";

    public string Issuer { get; set; } = "mapache";

    public string Audience { get; set; } = "mapache-panel";

    /// <summary>Llave HMAC-SHA256 de al menos 32 bytes. Secreto: user-secrets o variable de entorno.</summary>
    public string SigningKey { get; set; } = "";

    public int ExpiryMinutes { get; set; } = 480;
}
