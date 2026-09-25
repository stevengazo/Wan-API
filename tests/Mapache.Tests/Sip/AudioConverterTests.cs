using Mapache.Sip.Media;

namespace Mapache.Tests.Sip;

public class AudioConverterTests
{
    private static readonly byte[] Ulaw = [0x00, 0x7F, 0x80, 0xFF, 0x12, 0xA5];

    [Theory]
    [InlineData("ulaw_8000", true, 8000)]
    [InlineData("pcm_16000", false, 16000)]
    [InlineData("pcm_8000", false, 8000)]
    public void ParsesElevenLabsFormats(string value, bool isMuLaw, int rate) =>
        Assert.Equal(new VoiceAudioFormat(isMuLaw, rate), VoiceAudioFormat.Parse(value));

    [Fact]
    public void RejectsUnknownFormat() =>
        Assert.Throws<NotSupportedException>(() => VoiceAudioFormat.Parse("mp3_44100"));

    [Fact]
    public void PcmuWithUlawPassesThroughUntouched()
    {
        var format = VoiceAudioFormat.Parse("ulaw_8000");

        Assert.Equal(Ulaw, AudioConverter.TelephonyToElevenLabs(Ulaw, TelephonyCodec.Pcmu, format));
        Assert.Equal(Ulaw, AudioConverter.ElevenLabsToTelephony(Ulaw, format, TelephonyCodec.Pcmu));
    }

    [Fact]
    public void Pcm16000DoublesSampleCountAndRoundTrips()
    {
        var format = VoiceAudioFormat.Parse("pcm_16000");
        var frame = AudioConverter.EncodeG711(Sine(160, 8000), TelephonyCodec.Pcma);

        var up = AudioConverter.TelephonyToElevenLabs(frame, TelephonyCodec.Pcma, format);
        Assert.Equal(160 * 2 * 2, up.Length);

        var down = AudioConverter.ElevenLabsToTelephony(up, format, TelephonyCodec.Pcma);
        Assert.Equal(160, down.Length);
        Assert.Equal(frame, down);
    }

    [Fact]
    public void RmsDetectsSilenceVersusVoice()
    {
        Assert.Equal(0, AudioConverter.Rms(new short[160]));
        Assert.True(AudioConverter.Rms(Sine(160, 8000)) > 0.1);
    }

    private static short[] Sine(int samples, int rate) =>
        Enumerable.Range(0, samples).Select(i => (short)(8000 * Math.Sin(2 * Math.PI * 440 * i / rate))).ToArray();
}
