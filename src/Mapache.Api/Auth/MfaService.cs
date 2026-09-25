using Mapache.Domain.Entities;
using Mapache.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;

namespace Mapache.Api.Auth;

public enum MfaCheck
{
    Invalid,
    Code,
    RecoveryCode,
}

/// <summary>Doble factor con app autenticadora (TOTP) y códigos de recuperación.</summary>
public sealed class MfaService(ISecretProtector protector, IDataProtectionProvider protection, TimeProvider time)
{
    public const int RecoveryCodeCount = 10;
    private static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(5);

    // Cambiar el propósito invalida los desafíos en curso (duran 5 minutos, no importa).
    private readonly ITimeLimitedDataProtector _challenges =
        protection.CreateProtector("Mapache.MfaChallenge.v1").ToTimeLimitedDataProtector();

    /// <summary>
    /// Después de la contraseña: prueba de que ya pasó el primer factor, sin dar acceso todavía. Incluye el hash
    /// de la contraseña para que cambiarla invalide los desafíos pendientes.
    /// </summary>
    public string CreateChallenge(User user) =>
        _challenges.Protect($"{user.Id:N}|{Fingerprint(user)}", ChallengeLifetime);

    /// <summary>Usuario del desafío si no venció; después hay que confirmar <see cref="StillValid"/>.</summary>
    public (Guid UserId, string Fingerprint)? ReadChallenge(string token)
    {
        try
        {
            var parts = _challenges.Unprotect(token).Split('|');
            return parts.Length == 2 && Guid.TryParseExact(parts[0], "N", out var id) ? (id, parts[1]) : null;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    public static bool StillValid(User user, string fingerprint) => user.MfaEnabled && Fingerprint(user) == fingerprint;

    /// <summary>Crea una semilla nueva (pendiente de confirmar) y devuelve el URI para el QR.</summary>
    public (string Secret, string Uri) BeginSetup(User user)
    {
        var secret = Totp.NewSecret();
        user.MfaSecretEncrypted = protector.Protect(Convert.ToBase64String(secret));
        return (Totp.ToBase32(secret), Totp.ProvisioningUri(secret, user.Email, "Mapache"));
    }

    /// <summary>Verifica un código de la app o, si <paramref name="allowRecovery"/>, uno de recuperación (que se gasta).</summary>
    public MfaCheck Verify(User user, string code, bool allowRecovery = true)
    {
        if (user.MfaSecretEncrypted is null || string.IsNullOrWhiteSpace(code))
        {
            return MfaCheck.Invalid;
        }

        var secret = Convert.FromBase64String(protector.Unprotect(user.MfaSecretEncrypted));
        if (Totp.Verify(secret, code, time.GetUtcNow(), user.MfaLastStep) is { } step)
        {
            user.MfaLastStep = step;
            return MfaCheck.Code;
        }

        var hash = Totp.HashRecoveryCode(code);
        if (allowRecovery && user.MfaRecoveryCodeHashes.Contains(hash))
        {
            // Lista nueva: EF compara la columna jsonb por referencia.
            user.MfaRecoveryCodeHashes = user.MfaRecoveryCodeHashes.Where(h => h != hash).ToList();
            return MfaCheck.RecoveryCode;
        }

        return MfaCheck.Invalid;
    }

    /// <summary>Reemplaza los códigos de recuperación; se muestran una sola vez.</summary>
    public IReadOnlyList<string> NewRecoveryCodes(User user)
    {
        var codes = Enumerable.Range(0, RecoveryCodeCount).Select(_ => Totp.NewRecoveryCode()).ToList();
        user.MfaRecoveryCodeHashes = codes.Select(Totp.HashRecoveryCode).ToList();
        return codes;
    }

    public static void Disable(User user)
    {
        user.MfaEnabled = false;
        user.MfaSecretEncrypted = null;
        user.MfaRecoveryCodeHashes = [];
        user.MfaLastStep = null;
    }

    private static string Fingerprint(User user) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(user.PasswordHash)))[..16];
}
