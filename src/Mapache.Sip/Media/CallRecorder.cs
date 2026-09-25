using System.Buffers.Binary;
using System.Diagnostics;

namespace Mapache.Sip.Media;

/// <summary>
/// Graba una llamada mezclando las dos voces (quien llama y el bot u operador) en un WAV PCM 16 bits 8 kHz mono.
/// Cada lado entrega audio cuando lo tiene; un reloj propio toma 20 ms de cada uno por cuadro y rellena con
/// silencio, así los huecos (supresión de silencio, pausas del bot) no desfasan las dos voces.
/// </summary>
public sealed class CallRecorder : IAsyncDisposable
{
    private const int SampleRate = 8000;
    private const int FrameSamples = 160;
    // Más de un segundo acumulado en un lado es deriva de reloj entre productor y grabador: se descarta lo viejo.
    private const int MaxBuffered = SampleRate;
    private const int HeaderBytes = 44;
    private static readonly TimeSpan FrameDuration = TimeSpan.FromMilliseconds(20);

    private readonly FileStream _file;
    private readonly Lock _lock = new();
    private readonly Queue<short> _caller = new();
    private readonly Queue<short> _other = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _clock;
    private long _samples;
    private int _stopped;

    public CallRecorder(string path)
    {
        Path = path;
        _file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 64 * 1024, useAsync: true);
        _file.Write(new byte[HeaderBytes]);
        _clock = Task.Run(ClockLoopAsync);
    }

    public string Path { get; }

    public TimeSpan Duration => TimeSpan.FromSeconds((double)_samples / SampleRate);

    /// <summary>Audio de quien llama.</summary>
    public void AddCaller(ReadOnlySpan<short> pcm) => Enqueue(_caller, pcm);

    /// <summary>Audio del otro lado: el bot o, tras tomar la llamada, el operador.</summary>
    public void AddOther(ReadOnlySpan<short> pcm) => Enqueue(_other, pcm);

    private void Enqueue(Queue<short> queue, ReadOnlySpan<short> pcm)
    {
        lock (_lock)
        {
            foreach (var sample in pcm)
            {
                queue.Enqueue(sample);
            }

            while (queue.Count > MaxBuffered)
            {
                queue.Dequeue();
            }
        }
    }

    private async Task ClockLoopAsync()
    {
        var clock = Stopwatch.StartNew();
        long written = 0;
        var frame = new byte[FrameSamples * 2];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                // Por reloj y no por ticks, igual que el puente: Task.Delay en Windows tiene ~15 ms de granularidad.
                var due = clock.Elapsed.Ticks / FrameDuration.Ticks;
                for (; written < due; written++)
                {
                    MixFrame(frame);
                    await _file.WriteAsync(frame, _cts.Token);
                    _samples += FrameSamples;
                }

                await Task.Delay(10, _cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void MixFrame(byte[] frame)
    {
        lock (_lock)
        {
            for (var i = 0; i < FrameSamples; i++)
            {
                var a = _caller.Count > 0 ? _caller.Dequeue() : 0;
                var b = _other.Count > 0 ? _other.Dequeue() : 0;
                var mixed = (short)Math.Clamp(a + b, short.MinValue, short.MaxValue);
                BinaryPrimitives.WriteInt16LittleEndian(frame.AsSpan(i * 2), mixed);
            }
        }
    }

    /// <summary>Cierra el archivo con el encabezado WAV correcto. Devuelve la duración grabada.</summary>
    public async Task<TimeSpan> StopAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 1)
        {
            return Duration;
        }

        await _cts.CancelAsync();
        await _clock;
        _file.Position = 0;
        await _file.WriteAsync(Header(_samples));
        await _file.FlushAsync();
        await _file.DisposeAsync();
        return Duration;
    }

    public static byte[] Header(long samples)
    {
        var dataBytes = (uint)(samples * 2);
        var header = new byte[HeaderBytes];
        var span = header.AsSpan();
        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], 36 + dataBytes);
        "WAVE"u8.CopyTo(span[8..]);
        "fmt "u8.CopyTo(span[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(span[20..], 1); // PCM
        BinaryPrimitives.WriteUInt16LittleEndian(span[22..], 1); // mono
        BinaryPrimitives.WriteUInt32LittleEndian(span[24..], SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(span[28..], SampleRate * 2);
        BinaryPrimitives.WriteUInt16LittleEndian(span[32..], 2);
        BinaryPrimitives.WriteUInt16LittleEndian(span[34..], 16);
        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[40..], dataBytes);
        return header;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _cts.Dispose();
    }
}
