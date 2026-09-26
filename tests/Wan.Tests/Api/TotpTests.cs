using System.Text;
using Mapache.Infrastructure.Security;

namespace Mapache.Tests.Api;

public class TotpTests
{
    // Semilla de los vectores de prueba del RFC 6238 (apéndice B, SHA-1).
    private static readonly byte[] RfcSecret = Encoding.ASCII.GetBytes("12345678901234567890");

    [Theory]
    [InlineData(59, "94287082")]
    [InlineData(1111111109, "07081804")]
    [InlineData(1234567890, "89005924")]
    [InlineData(20000000000, "65353130")]
    public void MatchesRfcVectors(long unixSeconds, string expected) =>
        Assert.Equal(expected, Totp.Code(RfcSecret, Totp.StepAt(DateTimeOffset.FromUnixTimeSeconds(unixSeconds)), 8));

    [Fact]
    public void AcceptsNeighbourStepsButNotReplays()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var step = Totp.StepAt(now);

        Assert.Equal(step - 1, Totp.Verify(RfcSecret, Totp.Code(RfcSecret, step - 1), now, null));
        Assert.Equal(step, Totp.Verify(RfcSecret, "  " + Totp.Code(RfcSecret, step).Insert(3, " "), now, null));
        Assert.Null(Totp.Verify(RfcSecret, Totp.Code(RfcSecret, step - 2), now, null));
        Assert.Null(Totp.Verify(RfcSecret, Totp.Code(RfcSecret, step), now, lastUsedStep: step));
        Assert.Null(Totp.Verify(RfcSecret, "12345", now, null));
    }

    [Fact]
    public void EncodesBase32AndBuildsTheProvisioningUri()
    {
        Assert.Equal("MZXW6YTBOI", Totp.ToBase32("foobar"u8.ToArray()));
        var uri = Totp.ProvisioningUri("foobar"u8.ToArray(), "ana@empresa.com", "Mapache");
        Assert.StartsWith("otpauth://totp/Mapache:ana%40empresa.com?secret=MZXW6YTBOI&issuer=Mapache", uri);
    }

    [Fact]
    public void RecoveryCodesIgnoreCaseAndDashes()
    {
        var code = Totp.NewRecoveryCode();
        Assert.Matches("^[A-Z2-9]{5}-[A-Z2-9]{5}$", code);
        Assert.Equal(Totp.HashRecoveryCode(code), Totp.HashRecoveryCode(code.Replace("-", "").ToLowerInvariant()));
    }
}
