using System.Security.Claims;
using System.Text;
using Mapache.Domain.Entities;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Mapache.Api.Auth;

public sealed class TokenService(IOptions<JwtOptions> options, TimeProvider time)
{
    private readonly JwtOptions _options = options.Value;

    public (string Token, DateTimeOffset ExpiresAt) Create(User user)
    {
        var expiresAt = time.GetUtcNow().AddMinutes(_options.ExpiryMinutes);
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            Expires = expiresAt.UtcDateTime,
            Subject = new ClaimsIdentity(
            [
                new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
                new Claim(JwtRegisteredClaimNames.Email, user.Email),
                new Claim("role", user.Role.ToString()),
            ]),
            SigningCredentials = new SigningCredentials(CreateKey(_options), SecurityAlgorithms.HmacSha256),
        });

        return (token, expiresAt);
    }

    public static SymmetricSecurityKey CreateKey(JwtOptions options) => new(Encoding.UTF8.GetBytes(options.SigningKey));
}
