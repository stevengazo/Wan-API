using System.Security.Cryptography;
using System.Text;

namespace Wan.Infrastructure.Security;

/// <summary>
/// Códigos de un solo uso por tiempo (RFC 6238): HMAC-SHA1, 6 dígitos, 30 segundos. Es lo que esperan Google
/// Authenticator, Microsoft Authenticator, 1Password y Authy por defecto.
/// </summary>
public static class Totp
{
    public const int Digits = 6;
    private const int StepSeconds = 30;
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(20);

    /// <summary>Intervalo de 30 segundos al que pertenece un instante.</summary>
    public static long StepAt(DateTimeOffset at) => at.ToUnixTimeSeconds() / StepSeconds;

    public static string Code(byte[] secret, long step, int digits = Digits)
    {
        Span<byte> counter = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(counter, step);
        Span<byte> hash = stackalloc byte[20];
        HMACSHA1.HashData(secret, counter, hash);

        var offset = hash[^1] & 0x0f;
        var binary = ((hash[offset] & 0x7f) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % (int)Math.Pow(10, digits)).ToString().PadLeft(digits, '0');
    }

    /// <summary>
    /// Acepta el intervalo actual y uno a cada lado (relojes desfasados), pero nunca uno igual o anterior al último
    /// usado: así un código visto por encima del hombro no sirve dos veces.
    /// </summary>
    public static long? Verify(byte[] secret, string code, DateTimeOffset now, long? lastUsedStep)
    {
        var normalized = new string(code.Where(char.IsDigit).ToArray());
        if (normalized.Length != Digits)
        {
            return null;
        }

        var current = StepAt(now);
        for (var step = current - 1; step <= current + 1; step++)
        {
            if (step > (lastUsedStep ?? long.MinValue)
                && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Code(secret, step)), Encoding.ASCII.GetBytes(normalized)))
            {
                return step;
            }
        }

        return null;
    }

    /// <summary>URI que leen las apps autenticadoras desde el QR.</summary>
    public static string ProvisioningUri(byte[] secret, string account, string issuer) =>
        $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}"
        + $"?secret={ToBase32(secret)}&issuer={Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits={Digits}&period={StepSeconds}";

    public static string ToBase32(byte[] data)
    {
        var result = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                result.Append(Base32Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }

        if (bits > 0)
        {
            result.Append(Base32Alphabet[(buffer << (5 - bits)) & 31]);
        }

        return result.ToString();
    }

    /// <summary>Códigos de recuperación legibles: 10 caracteres sin letras que se confundan (0/O, 1/I).</summary>
    public static string NewRecoveryCode()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var chars = RandomNumberGenerator.GetItems<char>(alphabet, 10);
        return $"{new string(chars, 0, 5)}-{new string(chars, 5, 5)}";
    }

    public static string HashRecoveryCode(string code) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(NormalizeRecoveryCode(code))));

    public static string NormalizeRecoveryCode(string code) =>
        new string(code.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}
