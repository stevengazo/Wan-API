using System.Security.Cryptography;
using Wan.Infrastructure.Recordings;

namespace Wan.Tests.Api;

public class RecordingCipherTests
{
    private static readonly byte[] Audio = "RIFF....WAVEfmt datos de audio de prueba"u8.ToArray();

    [Fact]
    public void RoundTripsAndDoesNotLeaveTheAudioInClear()
    {
        var key = RecordingCipher.NewKey();

        var sealedData = RecordingCipher.Encrypt(key, Audio);

        Assert.Equal("MPC1"u8.ToArray(), sealedData[..4]);
        Assert.DoesNotContain("datos de audio"u8.ToArray(), Chunks(sealedData));
        Assert.Equal(Audio, RecordingCipher.Decrypt(key, sealedData));
    }

    [Fact]
    public void DetectsTamperingAndWrongKey()
    {
        var key = RecordingCipher.NewKey();
        var sealedData = RecordingCipher.Encrypt(key, Audio);
        var tampered = (byte[])sealedData.Clone();
        tampered[^1] ^= 0x01;

        Assert.ThrowsAny<CryptographicException>(() => RecordingCipher.Decrypt(key, tampered));
        Assert.ThrowsAny<CryptographicException>(() => RecordingCipher.Decrypt(RecordingCipher.NewKey(), sealedData));
        Assert.ThrowsAny<CryptographicException>(() => RecordingCipher.Decrypt(key, Audio));
    }

    [Fact]
    public void SameAudioEncryptsDifferentlyEachTime() =>
        Assert.NotEqual(RecordingCipher.Encrypt(RecordingCipher.NewKey(), Audio), RecordingCipher.Encrypt(RecordingCipher.NewKey(), Audio));

    private static IEnumerable<byte[]> Chunks(byte[] data) =>
        Enumerable.Range(0, Math.Max(0, data.Length - 13)).Select(i => data[i..(i + 14)]);
}
