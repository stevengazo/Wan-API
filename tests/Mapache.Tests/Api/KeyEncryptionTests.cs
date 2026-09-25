using System.Security.Cryptography;
using System.Xml.Linq;
using Mapache.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace Mapache.Tests.Api;

public class KeyEncryptionTests
{
    [Fact]
    public void KeysAreStoredEncryptedAndReadableWithTheSameMasterKey()
    {
        var repository = new MemoryRepository();
        var masterKey = NewMasterKey();

        var secret = Protector(repository, masterKey).Protect("clave-sip");

        var stored = Assert.Single(repository.GetAllElements());
        Assert.Contains(stored.Descendants(), e => e.Name.LocalName == "aesGcmEncryptedKey");
        Assert.DoesNotContain(stored.Descendants(), e => e.Name.LocalName == "masterKey" && e.Parent?.Name.LocalName == "descriptor");
        Assert.Equal("clave-sip", Protector(repository, masterKey).Unprotect(secret));
    }

    [Fact]
    public void AnotherMasterKeyCannotReadTheSecrets()
    {
        var repository = new MemoryRepository();
        var secret = Protector(repository, NewMasterKey()).Protect("clave-sip");

        Assert.ThrowsAny<CryptographicException>(() => Protector(repository, NewMasterKey()).Unprotect(secret));
    }

    [Fact]
    public void RejectsMalformedMasterKeys()
    {
        Assert.Throws<InvalidOperationException>(() => MasterKey.Parse("no es base64"));
        Assert.Throws<InvalidOperationException>(() => MasterKey.Parse(Convert.ToBase64String(new byte[16])));
    }

    private static MasterKey NewMasterKey() => MasterKey.Parse(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));

    private static IDataProtector Protector(MemoryRepository repository, MasterKey masterKey)
    {
        var services = new ServiceCollection();
        services.AddSingleton(masterKey);
        services.AddDataProtection()
            .SetApplicationName("Mapache")
            .AddKeyManagementOptions(o =>
            {
                o.XmlRepository = repository;
                o.XmlEncryptor = new AesGcmXmlEncryptor(masterKey);
            });
        return services.BuildServiceProvider().GetRequiredService<IDataProtectionProvider>().CreateProtector("prueba");
    }

    private sealed class MemoryRepository : IXmlRepository
    {
        private readonly List<XElement> _elements = [];

        public IReadOnlyCollection<XElement> GetAllElements() => _elements.ToList();

        public void StoreElement(XElement element, string friendlyName) => _elements.Add(element);
    }
}
