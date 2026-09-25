using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;
using Mapache.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Mapache.Infrastructure.Security;

/// <summary>
/// Cifrado en reposo de las llaves de Data Protection (tabla <c>DataProtectionKeys</c>). La clave maestra vive fuera
/// de la base, así un respaldo de la base por sí solo no alcanza para leer los secretos guardados.
/// </summary>
public static class KeyEncryption
{
    public const string KeySetting = "DataProtection:KeyEncryptionKey";
    public const string CertificatePathSetting = "DataProtection:CertificatePath";
    public const string CertificatePasswordSetting = "DataProtection:CertificatePassword";

    public static void Configure(IDataProtectionBuilder builder, IConfiguration configuration)
    {
        if (configuration[KeySetting] is { Length: > 0 } encoded)
        {
            var key = MasterKey.Parse(encoded);
            builder.Services.AddSingleton(key);
            builder.Services.Configure<KeyManagementOptions>(o => o.XmlEncryptor = new AesGcmXmlEncryptor(key));
            builder.Services.AddHostedService<UnencryptedKeyRotation>();
            return;
        }

        if (configuration[CertificatePathSetting] is { Length: > 0 } path)
        {
            var certificate = X509CertificateLoader.LoadPkcs12FromFile(path, configuration[CertificatePasswordSetting]);
            builder.ProtectKeysWithCertificate(certificate);
            builder.Services.AddHostedService<UnencryptedKeyRotation>();
        }
    }

    /// <summary>
    /// Una llave cifrada lleva un <c>encryptedSecret</c>; las creadas antes de configurar la clave maestra quedan con
    /// el secreto a la vista.
    /// </summary>
    internal static bool IsEncrypted(XElement key) => key.Descendants().Any(e => e.Name.LocalName == "encryptedSecret");
}

/// <summary>Clave maestra de 32 bytes en base64 (<c>openssl rand -base64 32</c>).</summary>
public sealed class MasterKey
{
    private MasterKey(byte[] value)
    {
        Value = value;
        // Identifica la clave sin revelarla: sirve para avisar si se cambió.
        Id = Convert.ToHexString(SHA256.HashData(value)[..8]).ToLowerInvariant();
    }

    public byte[] Value { get; }

    public string Id { get; }

    public static MasterKey Parse(string encoded)
    {
        byte[] value;
        try
        {
            value = Convert.FromBase64String(encoded.Trim());
        }
        catch (FormatException)
        {
            throw new InvalidOperationException($"{KeyEncryption.KeySetting} debe estar en base64 (openssl rand -base64 32)");
        }

        return value.Length == 32
            ? new MasterKey(value)
            : throw new InvalidOperationException($"{KeyEncryption.KeySetting} debe tener 32 bytes (openssl rand -base64 32)");
    }
}

public sealed class AesGcmXmlEncryptor(MasterKey key) : IXmlEncryptor
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    public EncryptedXmlInfo Encrypt(XElement plaintextElement)
    {
        var plain = System.Text.Encoding.UTF8.GetBytes(plaintextElement.ToString(SaveOptions.DisableFormatting));
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var tag = new byte[TagSize];
        var cipher = new byte[plain.Length];
        using (var aes = new AesGcm(key.Value, TagSize))
        {
            aes.Encrypt(nonce, plain, cipher, tag);
        }

        var element = new XElement(
            "aesGcmEncryptedKey",
            new XAttribute("keyId", key.Id),
            new XElement("value", Convert.ToBase64String([.. nonce, .. tag, .. cipher])));
        return new EncryptedXmlInfo(element, typeof(AesGcmXmlDecryptor));
    }
}

/// <summary>Data Protection la crea por reflexión con el <see cref="IServiceProvider"/>.</summary>
public sealed class AesGcmXmlDecryptor(IServiceProvider services) : IXmlDecryptor
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    public XElement Decrypt(XElement encryptedElement)
    {
        var key = services.GetService<MasterKey>()
            ?? throw new InvalidOperationException($"Las llaves de Data Protection están cifradas y falta {KeyEncryption.KeySetting}");
        if ((string?)encryptedElement.Attribute("keyId") is { } expected && expected != key.Id)
        {
            throw new CryptographicException(
                $"Las llaves de Data Protection se cifraron con otra clave maestra ({expected}, la configurada es {key.Id})");
        }

        var data = Convert.FromBase64String((string)encryptedElement.Element("value")!);
        var nonce = data.AsSpan(0, NonceSize);
        var tag = data.AsSpan(NonceSize, TagSize);
        var cipher = data.AsSpan(NonceSize + TagSize);
        var plain = new byte[cipher.Length];
        using (var aes = new AesGcm(key.Value, TagSize))
        {
            aes.Decrypt(nonce, cipher, tag, plain);
        }

        return XElement.Parse(System.Text.Encoding.UTF8.GetString(plain));
    }
}

/// <summary>
/// Al activar el cifrado en una instalación existente, la llave en uso quedó sin cifrar. Se crea una nueva (cifrada)
/// para lo que se guarde de ahora en adelante; la anterior se conserva para leer los secretos que ya protegió.
/// </summary>
internal sealed class UnencryptedKeyRotation(IServiceProvider services, IKeyManager keys, TimeProvider time, ILogger<UnencryptedKeyRotation> logger)
    : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MapacheDbContext>();
        var stored = await db.DataProtectionKeys.AsNoTracking().Select(k => k.Xml).ToListAsync(cancellationToken);
        var now = time.GetUtcNow();
        var newest = stored
            .Where(xml => xml is not null)
            .Select(xml => XElement.Parse(xml!))
            .Where(k => k.Name.LocalName == "key")
            .Select(k => (Element: k, Activation: (DateTimeOffset?)k.Element("activationDate"), Expiration: (DateTimeOffset?)k.Element("expirationDate")))
            .Where(k => k.Activation <= now && k.Expiration > now)
            .MaxBy(k => k.Activation);

        if (newest.Element is not null && !KeyEncryption.IsEncrypted(newest.Element))
        {
            keys.CreateNewKey(now, now.AddDays(90));
            logger.LogInformation("Llave de Data Protection sin cifrar reemplazada por una cifrada; la anterior se conserva para leer lo guardado");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
