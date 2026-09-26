using System.Diagnostics;

namespace Wan.Sip.Media;

/// <summary>
/// Mide la latencia turno a turno: desde el último frame con voz del llamante hasta el primer audio
/// del agente. No incluye el tramo de red hacia el teléfono ni su jitter buffer.
/// </summary>
public sealed class LatencyTracker(Func<long>? clock = null)
{
    private readonly Func<long> _clock = clock ?? Stopwatch.GetTimestamp;
    private readonly List<TimeSpan> _samples = [];
    private readonly Lock _lock = new();
    private long _lastVoice;
    private long _transcript;
    private bool _awaitingResponse;

    public IReadOnlyList<TimeSpan> Samples
    {
        get
        {
            lock (_lock)
            {
                return _samples.ToList();
            }
        }
    }

    public void OnUserVoice()
    {
        lock (_lock)
        {
            _lastVoice = _clock();
            _awaitingResponse = true;
        }
    }

    public void OnUserTranscript()
    {
        lock (_lock)
        {
            _transcript = _clock();
        }
    }

    /// <summary>Devuelve la descripción de la medición si este audio abre la respuesta a un turno del usuario.</summary>
    public string? OnAgentAudio()
    {
        lock (_lock)
        {
            if (!_awaitingResponse)
            {
                return null;
            }

            _awaitingResponse = false;
            var now = _clock();
            var fromVoice = Stopwatch.GetElapsedTime(_lastVoice, now);
            _samples.Add(fromVoice);

            var fromTranscript = _transcript > _lastVoice ? Stopwatch.GetElapsedTime(_transcript, now) : (TimeSpan?)null;
            return fromTranscript is null
                ? $"fin de voz → audio {fromVoice.TotalMilliseconds:F0} ms"
                : $"fin de voz → audio {fromVoice.TotalMilliseconds:F0} ms (transcripción → audio {fromTranscript.Value.TotalMilliseconds:F0} ms)";
        }
    }

    public string Summary()
    {
        var ordered = Samples.Select(s => s.TotalMilliseconds).Order().ToList();
        if (ordered.Count == 0)
        {
            return "sin turnos medidos";
        }

        return $"{ordered.Count} turnos, p50 {Percentile(ordered, 0.5):F0} ms, p90 {Percentile(ordered, 0.9):F0} ms, máx {ordered[^1]:F0} ms";
    }

    private static double Percentile(List<double> ordered, double p) =>
        ordered[(int)Math.Ceiling(p * ordered.Count) - 1];
}
