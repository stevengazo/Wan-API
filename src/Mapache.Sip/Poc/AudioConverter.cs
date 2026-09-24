using SIPSorcery.Media;

namespace Mapache.Sip.Poc;

public enum TelephonyCodec
{
    Pcmu,
    Pcma,
}

/// <summary>
/// Formato de audio de ElevenLabs: "ulaw_8000" o "pcm_{rate}" (PCM 16 bits little-endian mono).
/// </summary>
public readonly record struct ElevenLabsAudioFormat(bool IsMuLaw, int SampleRate)
{
    public static ElevenLabsAudioFormat Parse(string value)
    {
        if (value == "ulaw_8000")
        {
            return new(true, 8000);
        }

        if (value.StartsWith("pcm_", StringComparison.Ordinal) && int.TryParse(value.AsSpan(4), out var rate))
        {
            return new(false, rate);
        }

        throw new NotSupportedException($"Formato de audio de ElevenLabs no soportado: {value}");
    }
}

/// <summary>
/// Convierte audio entre G.711 a 8 kHz (lado telefónico) y el formato que negocia ElevenLabs.
/// Si ambos lados son µ-law 8 kHz el audio pasa sin tocar (objetivo de D3).
/// </summary>
public static class AudioConverter
{
    public const int TelephonySampleRate = 8000;

    public static bool IsPassthrough(TelephonyCodec codec, ElevenLabsAudioFormat format) =>
        codec == TelephonyCodec.Pcmu && format.IsMuLaw;

    public static byte[] TelephonyToElevenLabs(ReadOnlySpan<byte> g711, TelephonyCodec codec, ElevenLabsAudioFormat target)
    {
        if (IsPassthrough(codec, target))
        {
            return g711.ToArray();
        }

        var pcm = DecodeG711(g711, codec);
        if (target.IsMuLaw)
        {
            return EncodeG711(pcm, TelephonyCodec.Pcmu);
        }

        return ToBytes(Resample(pcm, TelephonySampleRate, target.SampleRate));
    }

    public static byte[] ElevenLabsToTelephony(ReadOnlySpan<byte> audio, ElevenLabsAudioFormat source, TelephonyCodec codec)
    {
        if (IsPassthrough(codec, source))
        {
            return audio.ToArray();
        }

        var pcm = source.IsMuLaw
            ? DecodeG711(audio, TelephonyCodec.Pcmu)
            : Resample(FromBytes(audio), source.SampleRate, TelephonySampleRate);

        return EncodeG711(pcm, codec);
    }

    public static short[] DecodeG711(ReadOnlySpan<byte> g711, TelephonyCodec codec)
    {
        var pcm = new short[g711.Length];
        for (var i = 0; i < g711.Length; i++)
        {
            pcm[i] = codec == TelephonyCodec.Pcmu
                ? MuLawDecoder.MuLawToLinearSample(g711[i])
                : ALawDecoder.ALawToLinearSample(g711[i]);
        }

        return pcm;
    }

    public static byte[] EncodeG711(ReadOnlySpan<short> pcm, TelephonyCodec codec)
    {
        var g711 = new byte[pcm.Length];
        for (var i = 0; i < pcm.Length; i++)
        {
            g711[i] = codec == TelephonyCodec.Pcmu
                ? MuLawEncoder.LinearToMuLawSample(pcm[i])
                : ALawEncoder.LinearToALawSample(pcm[i]);
        }

        return g711;
    }

    /// <summary>Interpolación lineal. Suficiente para el POC; sin filtro anti-aliasing al bajar de tasa.</summary>
    public static short[] Resample(short[] input, int fromRate, int toRate)
    {
        if (fromRate == toRate || input.Length == 0)
        {
            return input;
        }

        var outputLength = (int)((long)input.Length * toRate / fromRate);
        var output = new short[outputLength];
        var step = (double)fromRate / toRate;
        for (var i = 0; i < outputLength; i++)
        {
            var position = i * step;
            var index = (int)position;
            var next = Math.Min(index + 1, input.Length - 1);
            var fraction = position - index;
            output[i] = (short)Math.Round(input[index] + (input[next] - input[index]) * fraction);
        }

        return output;
    }

    /// <summary>Energía RMS normalizada (0..1) de un bloque PCM, para detectar voz en la medición de latencia.</summary>
    public static double Rms(ReadOnlySpan<short> pcm)
    {
        if (pcm.IsEmpty)
        {
            return 0;
        }

        double sum = 0;
        foreach (var sample in pcm)
        {
            sum += (double)sample * sample;
        }

        return Math.Sqrt(sum / pcm.Length) / short.MaxValue;
    }

    private static short[] FromBytes(ReadOnlySpan<byte> bytes)
    {
        var samples = new short[bytes.Length / 2];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (short)(bytes[2 * i] | (bytes[2 * i + 1] << 8));
        }

        return samples;
    }

    private static byte[] ToBytes(ReadOnlySpan<short> samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            bytes[2 * i] = (byte)samples[i];
            bytes[2 * i + 1] = (byte)(samples[i] >> 8);
        }

        return bytes;
    }
}
