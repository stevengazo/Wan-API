using Microsoft.AspNetCore.DataProtection;

namespace Mapache.Infrastructure.Security;

public interface ISecretProtector
{
    string Protect(string plaintext);

    string Unprotect(string ciphertext);
}

/// <summary>Cifrado en reposo de credenciales (contraseñas SIP) con ASP.NET Data Protection.</summary>
public sealed class SecretProtector(IDataProtectionProvider provider) : ISecretProtector
{
    // Cambiar el propósito vuelve ilegibles los secretos ya guardados.
    private readonly IDataProtector _protector = provider.CreateProtector("Mapache.SipCredentials.v1");

    public string Protect(string plaintext) => _protector.Protect(plaintext);

    public string Unprotect(string ciphertext) => _protector.Unprotect(ciphertext);
}
