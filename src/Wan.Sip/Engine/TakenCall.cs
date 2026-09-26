using Wan.Sip.Media;
using Microsoft.Extensions.Logging;
using SIPSorcery.Net;
using SIPSorcery.SIP.App;

namespace Wan.Sip.Engine;

/// <summary>
/// Llamada entrante que atendía el bot y toma una persona desde el panel. El motor la engancha
/// (<see cref="AttachFromBot"/>) cuando el puente con ElevenLabs suelta el audio.
/// </summary>
internal sealed class TakenCall : BrowserCall
{
    // Si el puente no suelta la llamada en este tiempo, algo falló (la llamada terminó en el medio).
    private static readonly TimeSpan HandOffTimeout = TimeSpan.FromSeconds(5);

    private readonly SipAccountRuntime _runtime;
    private readonly Guid _callId;
    private readonly ILogger _logger;
    private readonly TaskCompletionSource _attached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<string> _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<string> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TakenCall(SipAccountRuntime runtime, Guid callId, ILogger logger)
    {
        _runtime = runtime;
        _callId = callId;
        _logger = logger;
    }

    /// <summary>Motivo del fin, para que el motor cierre el registro de la llamada.</summary>
    public Task<string> Completion => _completion.Task;

    public override async Task<string> RunAsync(CancellationToken ct)
    {
        var reason = "error";
        try
        {
            Raise(PhoneCallState.Calling);
            if (!_runtime.RequestHandOff(_callId, this))
            {
                reason = "la llamada ya terminó o la tomó otra persona";
                return reason;
            }

            await using var registration = ct.Register(Hangup);
            await _attached.Task.WaitAsync(HandOffTimeout, ct);
            MarkConnected();
            _logger.LogInformation("Llamada {CallId} tomada por un operador", _callId);

            reason = await _ended.Task;
            return reason;
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            reason = "no se pudo tomar la llamada";
            return reason;
        }
        finally
        {
            Detach(reason);
            Raise(PhoneCallState.Ended, reason);
            _completion.TrySetResult(reason);
        }
    }

    /// <summary>Lo llama el motor cuando el puente ya soltó la llamada, sin colgar.</summary>
    public void AttachFromBot(SIPUserAgent ua, RTPSession rtp, CallRecorder? recorder)
    {
        Recorder = recorder;
        Attach(ua, rtp);
        ua.OnCallHungup += _ => _ended.TrySetResult("el llamante colgó");
        _attached.TrySetResult();
    }

    public override void Hangup()
    {
        _ended.TrySetResult("colgaste");
        Ua?.Hangup();
    }
}
