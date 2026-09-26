using System.Security.Cryptography;

namespace Wan.Infrastructure.Recordings;

/// <summary>
/// Cifrado de grabaciones con AES-256-GCM: detecta cualquier alteración del archivo al descifrar. Formato:
/// "MPC1" + nonce (12) + tag (16) + audio cifrado. Un nonce aleatorio por archivo con la misma clave es seguro
/// para muchos más archivos de los que va a tener una instalación.
/// </summary>
public static class RecordingCipher
{
    private static readonly byte[] Magic = "MPC1"u8.ToArray();
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int HeaderSize = 4 + NonceSize + TagSize;

    public static byte[] NewKey() => RandomNumberGenerator.GetBytes(32);

    public static byte[] Encrypt(byte[] key, ReadOnlySpan<byte> plain)
    {
        var output = new byte[HeaderSize + plain.Length];
        Magic.CopyTo(output, 0);
        var nonce = output.AsSpan(4, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plain, output.AsSpan(HeaderSize), output.AsSpan(4 + NonceSize, TagSize));
        return output;
    }

    /// <exception cref="CryptographicException">Clave incorrecta o archivo alterado.</exception>
    public static byte[] Decrypt(byte[] key, ReadOnlySpan<byte> sealedData)
    {
        if (sealedData.Length < HeaderSize || !sealedData[..4].SequenceEqual(Magic))
        {
            throw new CryptographicException("No es una grabación cifrada de Wan");
        }

        var plain = new byte[sealedData.Length - HeaderSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(sealedData.Slice(4, NonceSize), sealedData[HeaderSize..], sealedData.Slice(4 + NonceSize, TagSize), plain);
        return plain;
    }
}
