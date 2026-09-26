using Wan.Domain.Entities;
using Microsoft.Extensions.Logging;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;

namespace Wan.Sip.Engine;

/// <summary>Llamada saliente del teléfono web: marca con las credenciales de la cuenta.</summary>
internal sealed class OutboundCall : BrowserCall
{
    // Pasado este tiempo sonando se cancela, como hace un teléfono físico.
    private const int RingTimeoutSeconds = 60;

    private readonly SipAccountRuntime _runtime;
    private readonly SIPUserAgent _ua;
    private readonly RTPSession _rtp;
    private readonly SIPCallDescriptor _descriptor;
    private readonly string _number;
    private readonly ILogger _logger;
    private readonly TaskCompletionSource<string> _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public OutboundCall(SipAccountRuntime runtime, SIPUserAgent ua, RTPSession rtp, SIPCallDescriptor descriptor, string number, ILogger logger)
    {
        _runtime = runtime;
        _ua = ua;
        _rtp = rtp;
        _descriptor = descriptor;
        _number = number;
        _logger = logger;
    }

    public override async Task<string> RunAsync(CancellationToken ct)
    {
        var callId = await _runtime.OutboundCallStartedAsync(_number);
        var status = CallStatus.Failed;
        var reason = "error";
        using var scope = _logger.BeginScope("Llamada saliente {CallId} a {Number}", callId, _number);

        try
        {
            Attach(_ua, _rtp);
            _ua.ClientCallRinging += (_, _) => Raise(PhoneCallState.Ringing);
            _ua.ClientCallFailed += (_, error, response) => _ended.TrySetResult(Describe(error, response));
            _ua.OnCallHungup += _ => _ended.TrySetResult("el otro lado colgó");
            await using var registration = ct.Register(Hangup);

            Raise(PhoneCallState.Calling);
            _logger.LogInformation("Marcando {Uri}", _descriptor.Uri);

            if (!await _ua.Call(_descriptor, _rtp, RingTimeoutSeconds))
            {
                reason = _ended.Task.IsCompleted ? await _ended.Task : "no contestó";
                status = CallStatus.Missed;
                return reason;
            }

            MarkConnected();
            await _runtime.Events.CallAnsweredAsync(callId, null);
            _logger.LogInformation("Contestada");

            reason = await _ended.Task;
            status = CallStatus.Completed;
            return reason;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error en la llamada saliente");
            reason = ex.Message;
            return reason;
        }
        finally
        {
            Detach(reason);
            Raise(PhoneCallState.Ended, reason);
            await _runtime.CallFinishedAsync(callId, status, reason);
        }
    }

    public override void Hangup()
    {
        // El motivo primero: Hangup dispara OnCallHungup en el acto y ganaría "el otro lado colgó".
        if (Connected)
        {
            _ended.TrySetResult("colgaste");
            _ua.Hangup();
        }
        else
        {
            _ended.TrySetResult("cancelada antes de contestar");
            _ua.Cancel();
        }
    }

    private static string Describe(string error, SIPResponse? response) => response?.Status switch
    {
        SIPResponseStatusCodesEnum.BusyHere or SIPResponseStatusCodesEnum.BusyEverywhere => "ocupado",
        SIPResponseStatusCodesEnum.NotFound => "el número no existe",
        SIPResponseStatusCodesEnum.TemporarilyUnavailable => "no disponible",
        SIPResponseStatusCodesEnum.Decline => "rechazó la llamada",
        SIPResponseStatusCodesEnum.Forbidden => "la central no permite esta llamada (403)",
        SIPResponseStatusCodesEnum.RequestTerminated => "cancelada antes de contestar",
        SIPResponseStatusCodesEnum.RequestTimeout => "no contestó",
        { } status => $"{(int)status} {response!.ReasonPhrase}",
        null => error,
    };
}
