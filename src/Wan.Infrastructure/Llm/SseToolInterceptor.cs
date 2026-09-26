using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Wan.Infrastructure.Llm;

/// <summary>Llamada a herramienta armada a partir de los fragmentos del stream.</summary>
public sealed class StreamedToolCall
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public StringBuilder Arguments { get; } = new();
}

/// <summary>
/// Se interpone entre el proveedor y ElevenLabs en el stream SSE de chat completions. El texto pasa en el
/// momento (es voz que el llamante espera); las llamadas a herramientas, el <c>finish_reason</c> y el
/// <c>[DONE]</c> se retienen hasta saber si las ejecuta Wan o se le entregan a ElevenLabs.
/// </summary>
public sealed class SseToolInterceptor(Stream output) : Stream
{
    private readonly List<byte> _pending = [];
    private readonly List<byte[]> _held = [];
    private readonly SortedDictionary<int, StreamedToolCall> _calls = [];
    private readonly StringBuilder _text = new();

    /// <summary>Texto que ya se envió en esta ronda; se guarda como contenido del mensaje del asistente.</summary>
    public string Text => _text.ToString();

    public IReadOnlyList<StreamedToolCall> ToolCalls => _calls.Values.ToList();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        _pending.AddRange(buffer.Span);
        // Los eventos SSE terminan en una línea vacía; un trozo puede traer medio evento.
        while (IndexOfEventEnd() is var end and >= 0)
        {
            var raw = _pending.GetRange(0, end + 2).ToArray();
            _pending.RemoveRange(0, end + 2);
            if (Inspect(raw))
            {
                await output.WriteAsync(raw, cancellationToken);
            }
            else
            {
                _held.Add(raw);
            }
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override Task FlushAsync(CancellationToken cancellationToken) => output.FlushAsync(cancellationToken);

    /// <summary>Entrega lo retenido (llamadas de ElevenLabs, fin y <c>[DONE]</c>) tal como llegó.</summary>
    public async Task ReleaseHeldAsync(CancellationToken ct)
    {
        if (_pending.Count > 0)
        {
            _held.Add([.. _pending]);
            _pending.Clear();
        }

        foreach (var raw in _held)
        {
            await output.WriteAsync(raw, ct);
        }

        _held.Clear();
        await output.FlushAsync(ct);
    }

    /// <summary>True si el evento pasa directo; false si hay que retenerlo.</summary>
    private bool Inspect(byte[] raw)
    {
        var text = Encoding.UTF8.GetString(raw);
        var data = string.Concat(text.Split('\n').Where(l => l.StartsWith("data:", StringComparison.Ordinal)).Select(l => l[5..].TrimStart()));
        if (data.Length == 0)
        {
            return true;
        }

        if (data == "[DONE]")
        {
            return false;
        }

        JsonNode? chunk;
        try
        {
            chunk = JsonNode.Parse(data);
        }
        catch (JsonException)
        {
            return true;
        }

        var choice = chunk?["choices"]?[0];
        var delta = choice?["delta"];
        if (delta?["content"] is JsonValue content && content.TryGetValue<string>(out var piece))
        {
            _text.Append(piece);
        }

        var hold = false;
        if (delta?["tool_calls"] is JsonArray toolCalls)
        {
            foreach (var call in toolCalls)
            {
                var index = call?["index"]?.GetValue<int>() ?? 0;
                if (!_calls.TryGetValue(index, out var streamed))
                {
                    streamed = new StreamedToolCall();
                    _calls[index] = streamed;
                }

                if (call?["id"]?.GetValue<string>() is { Length: > 0 } id)
                {
                    streamed.Id = id;
                }

                if (call?["function"]?["name"]?.GetValue<string>() is { Length: > 0 } name)
                {
                    streamed.Name = name;
                }

                if (call?["function"]?["arguments"]?.GetValue<string>() is { } arguments)
                {
                    streamed.Arguments.Append(arguments);
                }
            }

            hold = true;
        }

        if (choice?["finish_reason"] is JsonValue finish && finish.GetValueKind() == JsonValueKind.String)
        {
            hold = true;
        }

        return !hold;
    }

    private int IndexOfEventEnd()
    {
        for (var i = 0; i < _pending.Count - 1; i++)
        {
            if (_pending[i] == '\n' && _pending[i + 1] == '\n')
            {
                return i;
            }
        }

        return -1;
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => output.Flush();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer, offset, count).GetAwaiter().GetResult();
}
