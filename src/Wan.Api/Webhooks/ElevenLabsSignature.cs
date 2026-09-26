using System.Security.Cryptography;
using System.Text;

namespace Wan.Api.Webhooks;

/// <summary>
/// Verifica el header <c>ElevenLabs-Signature: t=&lt;unix&gt;,v0=&lt;hex&gt;</c> de los webhooks post-llamada:
/// HMAC-SHA256 de <c>"{t}.{body}"</c> con el secreto del webhook. Mismo algoritmo que
/// <c>webhooks.construct_event</c> del SDK oficial de Python.
/// </summary>
public static class ElevenLabsSignature
{
    public const string HeaderName = "ElevenLabs-Signature";

    /// <summary>Misma ventana que el SDK: rechaza firmas con más de 30 minutos (evita replays).</summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(30);

    public static bool IsValid(string? header, string body, string secret, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(header) || string.IsNullOrEmpty(secret))
        {
            return false;
        }

        string? timestamp = null;
        string? signature = null;
        foreach (var part in header.Split(','))
        {
            var trimmed = part.Trim();
            if (trimmed.StartsWith("t=", StringComparison.Ordinal))
            {
                timestamp = trimmed[2..];
            }
            else if (trimmed.StartsWith("v0=", StringComparison.Ordinal))
            {
                signature = trimmed[3..];
            }
        }

        if (timestamp is null || signature is null || !long.TryParse(timestamp, out var unixSeconds))
        {
            return false;
        }

        var signedAt = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        if (now - signedAt > Tolerance || signedAt - now > TimeSpan.FromMinutes(5))
        {
            return false;
        }

        var expected = Compute(timestamp, body, secret);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected),
            Encoding.ASCII.GetBytes(signature.ToLowerInvariant()));
    }

    public static string Compute(string timestamp, string body, string secret)
    {
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{body}"));
        return Convert.ToHexStringLower(hash);
    }
}
