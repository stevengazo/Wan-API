using System.Net;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;

namespace Mapache.Sip.Poc;

/// <summary>
/// POC de Fase 0: registra una extensión y contesta cada llamada entrante puenteándola con ElevenLabs.
/// </summary>
public sealed class SipPocWorker(
    IOptions<PocOptions> options,
    IHttpClientFactory httpClientFactory,
    ILoggerFactory loggerFactory) : BackgroundService
{
    private readonly PocOptions _options = options.Value;
    private readonly ILogger _logger = loggerFactory.CreateLogger<SipPocWorker>();
    private SIPTransport? _transport;
    private SIPRegistrationUserAgent? _registration;
    private IPAddress? _publicIp;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        SIPSorcery.LogFactory.Set(loggerFactory);

        _publicIp = string.IsNullOrWhiteSpace(_options.Sip.PublicIpAddress) ? null : IPAddress.Parse(_options.Sip.PublicIpAddress);
        _transport = new SIPTransport();
        _transport.AddSIPChannel(new SIPUDPChannel(new IPEndPoint(IPAddress.Any, _options.Sip.ListenPort)));
        if (_publicIp is not null)
        {
            // Detrás de NAT el Contact debe llevar la IP pública o el proveedor no puede entregar el INVITE.
            _transport.ContactHost = _publicIp.ToString();
        }

        _transport.SIPTransportRequestReceived += OnRequestAsync;

        _registration = new SIPRegistrationUserAgent(
            _transport, _options.Sip.Username, _options.Sip.Password, _options.Sip.Server, _options.Sip.ExpirySeconds);
        _registration.RegistrationSuccessful += (uri, _) => _logger.LogInformation("Extensión registrada: {Uri}", uri);
        _registration.RegistrationTemporaryFailure += (uri, _, error) => _logger.LogWarning("Fallo temporal de registro {Uri}: {Error}", uri, error);
        _registration.RegistrationFailed += (uri, _, error) => _logger.LogError("Registro fallido {Uri}: {Error}", uri, error);
        _registration.Start();

        _logger.LogInformation("POC SIP escuchando en UDP {Port}, registrando {User}@{Server}",
            _options.Sip.ListenPort, _options.Sip.Username, _options.Sip.Server);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task OnRequestAsync(SIPEndPoint localEndPoint, SIPEndPoint remoteEndPoint, SIPRequest request)
    {
        try
        {
            if (request.Header.To?.ToTag is not null)
            {
                // Request dentro de un diálogo: lo procesa el SIPUserAgent de esa llamada.
                return;
            }

            switch (request.Method)
            {
                case SIPMethodsEnum.INVITE:
                    _ = Task.Run(() => HandleCallAsync(request));
                    break;

                case SIPMethodsEnum.OPTIONS:
                    // Algunos proveedores sondean con OPTIONS para verificar que la extensión sigue viva.
                    await _transport!.SendResponseAsync(SIPResponse.GetResponse(request, SIPResponseStatusCodesEnum.Ok, null));
                    break;

                case SIPMethodsEnum.BYE:
                    await _transport!.SendResponseAsync(SIPResponse.GetResponse(request, SIPResponseStatusCodesEnum.CallLegTransactionDoesNotExist, null));
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error procesando {Method} de {Remote}", request.Method, remoteEndPoint);
        }
    }

    private async Task HandleCallAsync(SIPRequest invite)
    {
        var caller = invite.Header.From?.FromURI?.User ?? "desconocido";
        using var scope = _logger.BeginScope("Llamada {CallId} de {Caller}", invite.Header.CallId, caller);
        _logger.LogInformation("INVITE entrante");

        var ua = new SIPUserAgent(_transport, null);
        var uas = ua.AcceptCall(invite);
        var rtp = CreateRtpSession();
        ElevenLabsConversation? conversation = null;

        try
        {
            uas.Progress(SIPResponseStatusCodesEnum.Ringing, null, null, null, null);

            // Se abre la conversación mientras suena, así el agente está listo al contestar.
            conversation = await ElevenLabsConversation.StartAsync(
                httpClientFactory.CreateClient(nameof(SipPocWorker)),
                _options.ElevenLabs,
                new Dictionary<string, string> { ["caller_id"] = caller },
                _logger,
                CancellationToken.None);

            if (!await ua.Answer(uas, rtp, _publicIp))
            {
                _logger.LogWarning("No se pudo contestar la llamada");
                await conversation.DisposeAsync();
                rtp.Close("answer fallido");
                return;
            }

            await using var bridge = new CallBridge(ua, rtp, conversation, _options, _logger);
            await bridge.RunAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error en la llamada");
            if (!ua.IsCallActive)
            {
                uas.Reject(SIPResponseStatusCodesEnum.ServiceUnavailable, null, null);
            }
            else
            {
                ua.Hangup();
            }

            if (conversation is not null)
            {
                await conversation.DisposeAsync();
            }

            rtp.Close("error");
        }
    }

    private static RTPSession CreateRtpSession()
    {
        var rtp = new RTPSession(false, false, false);
        // PCMU primero: si ElevenLabs usa ulaw_8000 el audio pasa sin transcodificar.
        rtp.addTrack(new MediaStreamTrack(
            [new AudioFormat(SDPWellKnownMediaFormatsEnum.PCMU), new AudioFormat(SDPWellKnownMediaFormatsEnum.PCMA)],
            MediaStreamStatusEnum.SendRecv));
        return rtp;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _registration?.Stop();
        await base.StopAsync(cancellationToken);
        _transport?.Shutdown();
    }
}
