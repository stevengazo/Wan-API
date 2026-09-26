namespace Mapache.Sip.Media;

/// <summary>
/// Buffer de reordenamiento por número de secuencia RTP. Retiene hasta <c>depth</c> paquetes esperando
/// el que falta; si no llega, lo da por perdido y sigue. Los paquetes que llegan tarde se descartan.
/// No hace relleno de pérdidas: el ASR de ElevenLabs tolera huecos cortos mejor que audio inventado.
/// </summary>
public sealed class JitterBuffer(int depth = 3)
{
    private readonly SortedDictionary<int, byte[]> _pending = new();
    private ushort _next;
    private bool _started;

    public int Dropped { get; private set; }

    public int Lost { get; private set; }

    public IReadOnlyList<byte[]> Push(ushort sequence, byte[] payload)
    {
        if (!_started)
        {
            _started = true;
            _next = sequence;
        }

        // Distancia con signo en 16 bits para sobrevivir al wrap-around de la secuencia.
        var offset = (short)(sequence - _next);
        if (offset < 0 || _pending.ContainsKey(offset))
        {
            Dropped++;
            return [];
        }

        _pending[offset] = payload;

        var ready = new List<byte[]>();
        while (_pending.Count > 0)
        {
            var first = _pending.First();
            if (first.Key != 0 && _pending.Count <= depth)
            {
                break;
            }

            Lost += first.Key;
            ready.Add(first.Value);
            _pending.Remove(first.Key);
            Advance(first.Key + 1);
        }

        return ready;
    }

    private void Advance(int count)
    {
        _next = (ushort)(_next + count);
        if (_pending.Count == 0)
        {
            return;
        }

        var shifted = _pending.ToDictionary(kv => kv.Key - count, kv => kv.Value);
        _pending.Clear();
        foreach (var (key, value) in shifted)
        {
            _pending[key] = value;
        }
    }
}
