using Mapache.Api.Webhooks;

namespace Mapache.Tests.Api;

public class ElevenLabsSignatureTests
{
    private const string Secret = "wsec_test";
    private const string Body = """{"type":"post_call_transcription","data":{"conversation_id":"abc"}}""";
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_790_000_000);

    private static string Header(DateTimeOffset at, string body = Body, string secret = Secret)
    {
        var t = at.ToUnixTimeSeconds().ToString();
        return $"t={t},v0={ElevenLabsSignature.Compute(t, body, secret)}";
    }

    [Fact]
    public void AcceptsValidSignature() =>
        Assert.True(ElevenLabsSignature.IsValid(Header(Now), Body, Secret, Now));

    [Fact]
    public void MatchesKnownHmacVector()
    {
        // printf '1.body' | openssl dgst -sha256 -hmac key
        Assert.Equal(
            "91b5374b153842ad05b2c4eab9349b8321b14703165bd3fb8b034dfb8be98ae5",
            ElevenLabsSignature.Compute("1", "body", "key"));
    }

    [Fact]
    public void RejectsTamperedBody() =>
        Assert.False(ElevenLabsSignature.IsValid(Header(Now), Body + " ", Secret, Now));

    [Fact]
    public void RejectsWrongSecret() =>
        Assert.False(ElevenLabsSignature.IsValid(Header(Now, secret: "otro"), Body, Secret, Now));

    [Fact]
    public void RejectsSignatureOlderThanTolerance() =>
        Assert.False(ElevenLabsSignature.IsValid(Header(Now.AddMinutes(-31)), Body, Secret, Now));

    [Fact]
    public void AcceptsSignatureWithinTolerance() =>
        Assert.True(ElevenLabsSignature.IsValid(Header(Now.AddMinutes(-29)), Body, Secret, Now));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("v0=abc")]
    [InlineData("t=notanumber,v0=abc")]
    public void RejectsMalformedHeader(string? header) =>
        Assert.False(ElevenLabsSignature.IsValid(header, Body, Secret, Now));

    [Fact]
    public void RejectsWhenSecretIsNotConfigured() =>
        Assert.False(ElevenLabsSignature.IsValid(Header(Now), Body, "", Now));
}
