using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Wan.Domain.Entities;
using Wan.Sip.Media;
using Microsoft.Extensions.Logging;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorcery.Sys;
using SIPSorceryMedia.Abstractions;

namespace Wan.Sip.Engine;

/// <summary>
/// Una cuenta SIP en funcionamiento: su transporte, su registro (con paso al servidor secundario si el
/// principal falla), el keepalive de NAT y las llamadas que entran por ella.
/// </summary>
internal sealed class SipAccountRuntime : IAsyncDisposable
{
    private readonly SipAccount _account;
    private readonly Extension _ext;
    private readonly SipRuntimeServices _services;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly Lock _stateLock = new();

    private SIPTransport? _transport;
    private SIPChannel? _channel;
    private SIPRegistrationUserAgent? _registration;
    private IPAddress? _publicIp;
    private bool _usingSecondary;
    private int _activeCalls;
    private SipRegistrationStatus _status;
    private Task? _keepAlive;

    // En campañas se deja sonar un poco menos que un teléfono: más allá suele atender el buzón de voz.
    private const int CampaignRingSeconds = 35;

    // Llamadas que atiende el bot ahora mismo, por si un operador las toma desde el panel.
    private readonly ConcurrentDictionary<Guid, BotCall> _botCalls = new();

    private sealed class BotCall(CallBridge bridge)
    {
        public CallBridge Bridge { get; } = bridge;

        public TakenCall? Taker { get; set; }
    }

    public SipAccountRuntime(SipAccount account, SipRuntimeServices services)
    {
        _account = account;
        _ext = account.Extension;
        _services = services;
        _logger = services.Loggers.CreateLogger($"Wan.Sip.Account.{_ext.SipUsername}");
        _status = new SipRegistrationStatus(_ext.Id, SipRegistrationState.Registering, _ext.SipServer, null, 0, services.Time.GetUtcNow());
    }

    public DateTimeOffset ConfigVersion => _ext.UpdatedAt;

    public SipRegistrationStatus Status
    {
        get
        {
            lock (_stateLock)
            {
                return _status with { ActiveCalls = _activeCalls };
            }
        }
    }

    public async Task StartAsync()
    {
        try
        {
            _publicIp = await ResolvePublicIpAsync();
            _transport = new SIPTransport();
            _channel = CreateChannel();
            _transport.AddSIPChannel(_channel);
            if (_publicIp is not null)
            {
                // Detrás de NAT el Contact debe llevar la IP pública o el servidor no puede entregar el INVITE.
                _transport.ContactHost = _publicIp.ToString();
            }

            _transport.SIPTransportRequestReceived += OnRequestAsync;
            StartRegistration();

            if (_ext.KeepAliveSeconds > 0 && _ext.Transport == SipTransport.Udp)
            {
                _keepAlive = Task.Run(KeepAliveLoopAsync);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo iniciar la cuenta {Name}", _ext.Name);
            await SetStatusAsync(SipRegistrationState.Failed, $"No se pudo iniciar: {ex.Message}");
        }
    }

    private string CurrentServer => _usingSecondary && !string.IsNullOrWhiteSpace(_ext.SecondarySipServer) ? _ext.SecondarySipServer! : _ext.SipServer;

    private string Domain => string.IsNullOrWhiteSpace(_ext.SipDomain) ? HostOf(CurrentServer) : _ext.SipDomain!;

    private string AuthUser => string.IsNullOrWhiteSpace(_ext.AuthUsername) ? _ext.SipUsername : _ext.AuthUsername!;

    internal ISipEventSink Events => _services.Events;

    private void StartRegistration()
    {
        var server = CurrentServer;
        var aor = SIPURI.ParseSIPURIRelaxed($"{_ext.SipUsername}@{Domain}");
        var authUser = AuthUser;

        // En TCP/TLS, o con proxy, se envía a un destino explícito con el protocolo de la cuenta.
        var proxy = !string.IsNullOrWhiteSpace(_ext.SipProxy) ? ResolveEndPoint(_ext.SipProxy!)
            : _ext.Transport != SipTransport.Udp ? ResolveEndPoint(server)
            : null;

        // Contact con 0.0.0.0:0: el transporte lo reemplaza por la dirección real (o ContactHost) al enviar.
        var contact = new SIPURI(SIPSchemesEnum.sip, IPAddress.Any, 0) { User = _ext.SipUsername };
        if (_ext.Transport != SipTransport.Udp)
        {
            contact.Parameters.Set("transport", _ext.Transport == SipTransport.Tls ? "tls" : "tcp");
        }

        _registration = new SIPRegistrationUserAgent(
            _transport, proxy, aor, authUser, _account.Password, null, server, contact,
            _ext.RegisterExpirySeconds, null,
            maxRegistrationAttemptTimeout: 30,
            registerFailureRetryInterval: Math.Max(5, _ext.RegisterRetrySeconds),
            maxRegisterAttempts: 3,
            exitOnUnequivocalFailure: true);
        if (!string.IsNullOrWhiteSpace(_ext.DisplayName))
        {
            _registration.UserDisplayName = _ext.DisplayName;
        }

        _registration.RegistrationSuccessful += (_, _) => _ = SetStatusAsync(SipRegistrationState.Registered, null);
        _registration.RegistrationTemporaryFailure += (_, _, error) => _ = SetStatusAsync(SipRegistrationState.Registering, error);
        _registration.RegistrationFailed += (_, _, error) => _ = OnRegistrationFailedAsync(error);
        _ = SetStatusAsync(SipRegistrationState.Registering, null);
        _registration.Start();
        _logger.LogInformation("Registrando {User}@{Server} por {Transport}", _ext.SipUsername, server, _ext.Transport);
    }

    /// <summary>Tras agotar los intentos se alterna entre servidor principal y secundario, esperando el reintento.</summary>
    private async Task OnRegistrationFailedAsync(string error)
    {
        await SetStatusAsync(SipRegistrationState.Failed, error);
        _registration?.Stop(false);
        _logger.LogWarning("Registro fallido en {Server}: {Error}", CurrentServer, error);

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, _ext.RegisterRetrySeconds)), _cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(_ext.SecondarySipServer))
        {
            _usingSecondary = !_usingSecondary;
        }

        StartRegistration();
    }

    /// <summary>Doble CRLF (RFC 5626) al servidor: mantiene abierto el agujero del NAT para que lleguen los INVITE.</summary>
    private async Task KeepAliveLoopAsync()
    {
        var ping = Encoding.ASCII.GetBytes("\r\n\r\n");
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_ext.KeepAliveSeconds), _cts.Token);
                if (_registration?.IsRegistered == true && ResolveEndPoint(_ext.SipProxy ?? CurrentServer) is { } target)
                {
                    await _transport!.SendRawAsync(_channel!.ListeningSIPEndPoint, target, ping);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Keepalive fallido");
            }
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
                    _ = Task.Run(() => HandleInboundAsync(request));
                    break;
                case SIPMethodsEnum.OPTIONS:
                    // Algunos servidores sondean con OPTIONS para verificar que la cuenta sigue viva.
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

    private async Task HandleInboundAsync(SIPRequest invite)
    {
        var caller = invite.Header.From?.FromURI?.User ?? "desconocido";
        using var scope = _logger.BeginScope("Llamada {CallId} de {Caller}", invite.Header.CallId, caller);

        var ua = new SIPUserAgent(_transport, null);
        var uas = ua.AcceptCall(invite);

        if (_ext.MaxConcurrentCalls > 0 && Volatile.Read(ref _activeCalls) >= _ext.MaxConcurrentCalls)
        {
            _logger.LogInformation("Llamada rechazada: límite de {Max} simultáneas", _ext.MaxConcurrentCalls);
            uas.Reject(SIPResponseStatusCodesEnum.BusyHere, null, null);
            return;
        }

        Interlocked.Increment(ref _activeCalls);
        var callId = await _services.Events.CallStartedAsync(_ext.Id, caller, CallDirection.Inbound);
        await PublishStatusAsync();
        var status = CallStatus.Failed;
        var reason = "error";

        try
        {
            if (_ext.AnswerMode == AnswerMode.Human)
            {
                // El softphone del panel todavía no existe: sin operador conectado no hay quién conteste.
                uas.Reject(SIPResponseStatusCodesEnum.TemporarilyUnavailable, null, null);
                (status, reason) = (CallStatus.Missed, "no hay operador conectado en el panel");
                return;
            }

            (status, reason) = await AnswerWithBotAsync(ua, uas, callId, caller);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error en la llamada");
            if (ua.IsCallActive)
            {
                ua.Hangup();
            }
            else
            {
                uas.Reject(SIPResponseStatusCodesEnum.ServiceUnavailable, null, null);
            }

            reason = ex.Message;
        }
        finally
        {
            Interlocked.Decrement(ref _activeCalls);
            await _services.Events.CallEndedAsync(callId, status, reason);
            await PublishStatusAsync();
        }
    }

    private async Task<(CallStatus Status, string Reason)> AnswerWithBotAsync(SIPUserAgent ua, SIPServerUserAgent uas, Guid callId, string caller)
    {
        var agent = await _services.VoiceAgent.GetAsync(_cts.Token);
        if (agent is null)
        {
            _logger.LogWarning("Llamada rechazada: falta configurar la voz en el panel");
            uas.Reject(SIPResponseStatusCodesEnum.ServiceUnavailable, null, null);
            return (CallStatus.Failed, "falta configurar el proveedor de voz (Configuración → Voz)");
        }

        uas.Progress(SIPResponseStatusCodesEnum.Ringing, null, null, null, null);
        var rtp = CreateRtpSession();

        // Se abre la conversación mientras suena, así el agente está listo al contestar.
        IVoiceSession conversation;
        try
        {
            conversation = await VoiceSessionFactory.StartAsync(
                agent, new VoiceCallContext(_ext.Id, callId, caller, _ext.Name), _services.Http.CreateClient("voice"), _services.Brain, _logger, _cts.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "No se pudo abrir la conversación con {Provider}", agent.DisplayName);
            uas.Reject(SIPResponseStatusCodesEnum.ServiceUnavailable, null, null);
            rtp.Close("voz no disponible");
            return (CallStatus.Failed, $"{agent.DisplayName} no respondió: {ex.Message}");
        }

        conversation.UserTranscript += text => _ = _services.Events.TranscriptAsync(callId, TranscriptSpeaker.Caller, text);
        conversation.AgentResponse += text => _ = _services.Events.TranscriptAsync(callId, TranscriptSpeaker.Agent, text);

        if (!await ua.Answer(uas, rtp, _publicIp))
        {
            await conversation.DisposeAsync();
            rtp.Close("answer fallido");
            return (CallStatus.Missed, "el llamante colgó antes de contestar");
        }

        var conversationId = ConversationIdFor(conversation, callId);
        await _services.Events.CallAnsweredAsync(callId, conversationId);
        return await RunBotAsync(ua, rtp, conversation, callId, conversationId, agent);
    }

    /// <summary>
    /// Id que enlaza la llamada con sus recados, formularios y grabación: el del proveedor o, si no da uno
    /// (OpenAI Realtime), el de la llamada.
    /// </summary>
    private static string ConversationIdFor(IVoiceSession conversation, Guid callId) =>
        conversation.ConversationId ?? callId.ToString("N");

    /// <summary>
    /// Corre el bot sobre una llamada ya contestada (entrante o de campaña) hasta que termina, se transfiere o
    /// la toma un operador del panel.
    /// </summary>
    private async Task<(CallStatus Status, string Reason)> RunBotAsync(
        SIPUserAgent ua, RTPSession rtp, IVoiceSession conversation, Guid callId, string conversationId, VoiceProviderOptions agent)
    {
        // ElevenLabs manda su propia grabación por webhook al terminar; con los demás graba Wan.
        var recorder = agent is not ElevenLabsOptions && await _services.Events.RecordingEnabledAsync()
            ? new CallRecorder(System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"wan-{callId:N}.wav"))
            : null;
        try
        {
            return await RunBotCoreAsync(ua, rtp, conversation, callId, recorder);
        }
        finally
        {
            if (recorder is not null)
            {
                var duration = await recorder.StopAsync();
                try
                {
                    await _services.Events.RecordingCompletedAsync(callId, conversationId, recorder.Path, duration);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "No se pudo guardar la grabación de {CallId}", callId);
                }
            }
        }
    }

    private async Task<(CallStatus Status, string Reason)> RunBotCoreAsync(
        SIPUserAgent ua, RTPSession rtp, IVoiceSession conversation, Guid callId, CallRecorder? recorder)
    {
        var bridge = new CallBridge(ua, rtp, conversation, HostOf(CurrentServer), _logger, recorder);
        var botCall = new BotCall(bridge);
        string reason;
        try
        {
            _botCalls[callId] = botCall;
            reason = await bridge.RunAsync();
        }
        finally
        {
            _botCalls.TryRemove(callId, out _);
            // Con traspaso, el puente cierra la conversación de voz pero deja viva la llamada.
            await bridge.DisposeAsync();
        }

        if (botCall.Taker is { } taker)
        {
            if (taker.Completion.IsCompleted)
            {
                // El operador desistió mientras el puente soltaba la llamada: nadie la atiende.
                ua.Hangup();
                rtp.Close("toma fallida");
                return (CallStatus.Failed, await taker.Completion);
            }

            taker.AttachFromBot(ua, rtp, recorder);
            var humanReason = await taker.Completion;
            return (CallStatus.Completed, $"la tomó un operador; {humanReason}");
        }

        return (reason.StartsWith("transferida", StringComparison.Ordinal) ? CallStatus.Transferred : CallStatus.Completed, reason);
    }

    /// <summary>Prepara una llamada saliente del teléfono web con las credenciales de la cuenta.</summary>
    public IPhoneCall CreateOutboundCall(string destination)
    {
        var (ua, descriptor) = PrepareOutbound(destination);
        return new OutboundCall(this, ua, CreateRtpSession(), descriptor, destination.Trim(), _logger);
    }

    /// <summary>Agente y descriptor de una llamada saliente con las credenciales, el proxy y el prefijo de la cuenta.</summary>
    private (SIPUserAgent Ua, SIPCallDescriptor Descriptor) PrepareOutbound(string destination)
    {
        if (_transport is null)
        {
            throw new InvalidOperationException("La extensión todavía no está lista.");
        }

        if (_ext.MaxConcurrentCalls > 0 && Volatile.Read(ref _activeCalls) >= _ext.MaxConcurrentCalls)
        {
            throw new InvalidOperationException($"La extensión ya tiene {_ext.MaxConcurrentCalls} llamadas en curso, su límite.");
        }

        var target = destination.Trim();
        var uri = target.StartsWith("sip:", StringComparison.OrdinalIgnoreCase) ? target
            : target.Contains('@') ? $"sip:{target}"
            : $"sip:{_ext.DialPrefix}{target}@{Domain}";
        var from = string.IsNullOrWhiteSpace(_ext.DisplayName)
            ? $"<sip:{_ext.SipUsername}@{Domain}>"
            : $"\"{_ext.DisplayName}\" <sip:{_ext.SipUsername}@{Domain}>";

        // Siempre por el proxy o el servidor de la cuenta, aunque el dominio del URI sea otro: así marca un softphone.
        var outboundProxy = ResolveEndPoint(string.IsNullOrWhiteSpace(_ext.SipProxy) ? CurrentServer : _ext.SipProxy!);
        var descriptor = new SIPCallDescriptor(
            AuthUser, _account.Password, uri, from, uri, null, null, AuthUser,
            SIPCallDirection.Out, SDP.SDP_MIME_CONTENTTYPE, null, _publicIp);

        return (new SIPUserAgent(_transport, outboundProxy), descriptor);
    }

    /// <summary>
    /// Llamada saliente atendida por el bot (campañas): marca, espera que contesten y hace hablar al bot con el
    /// guion de la campaña. Devuelve cómo terminó para que el marcador decida si reintentar.
    /// </summary>
    public async Task<BotCallResult> DialBotAsync(BotCallRequest request, CancellationToken ct)
    {
        var agent = await _services.VoiceAgent.GetAsync(ct)
            ?? throw new InvalidOperationException("Falta configurar el proveedor de voz (Configuración → Voz).");
        var (ua, descriptor) = PrepareOutbound(request.Destination);
        var rtp = CreateRtpSession();
        var callId = await OutboundCallStartedAsync(request.Destination.Trim());
        using var scope = _logger.BeginScope("Llamada de campaña {CallId} a {Number}", callId, request.Destination);
        SIPResponseStatusCodesEnum? failure = null;
        ua.ClientCallFailed += (_, _, response) => failure = response?.Status;
        var status = CallStatus.Failed;
        var reason = "error";

        try
        {
            _logger.LogInformation("Marcando {Uri} para la campaña", descriptor.Uri);
            if (!await ua.Call(descriptor, rtp, CampaignRingSeconds))
            {
                rtp.Close("sin respuesta");
                var busy = failure is SIPResponseStatusCodesEnum.BusyHere or SIPResponseStatusCodesEnum.BusyEverywhere;
                (status, reason) = (CallStatus.Missed, busy ? "ocupado" : failure is { } code ? $"no contestó ({(int)code})" : "no contestó");
                return new BotCallResult(callId, false, busy, reason);
            }

            // El proveedor de voz se abre al contestar: en salientes no hay tono durante el cual prepararlo.
            IVoiceSession conversation;
            try
            {
                conversation = await VoiceSessionFactory.StartAsync(
                    agent,
                    new VoiceCallContext(_ext.Id, callId, request.Destination, _ext.Name, request.CampaignId, request.ContactId, request.OpeningLine),
                    _services.Http.CreateClient("voice"),
                    _services.Brain,
                    _logger,
                    ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "No se pudo abrir la conversación con {Provider}", agent.DisplayName);
                ua.Hangup();
                rtp.Close("voz no disponible");
                (status, reason) = (CallStatus.Failed, $"{agent.DisplayName} no respondió: {ex.Message}");
                return new BotCallResult(callId, false, false, reason);
            }

            conversation.UserTranscript += text => _ = _services.Events.TranscriptAsync(callId, TranscriptSpeaker.Caller, text);
            conversation.AgentResponse += text => _ = _services.Events.TranscriptAsync(callId, TranscriptSpeaker.Agent, text);
            var conversationId = ConversationIdFor(conversation, callId);
            await _services.Events.CallAnsweredAsync(callId, conversationId);
            (status, reason) = await RunBotAsync(ua, rtp, conversation, callId, conversationId, agent);
            return new BotCallResult(callId, true, false, reason);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error en la llamada de campaña");
            reason = ex.Message;
            return new BotCallResult(callId, false, false, reason);
        }
        finally
        {
            await CallFinishedAsync(callId, status, reason);
        }
    }

    internal async Task<Guid> OutboundCallStartedAsync(string number)
    {
        Interlocked.Increment(ref _activeCalls);
        var callId = await _services.Events.CallStartedAsync(_ext.Id, number, CallDirection.Outbound);
        await PublishStatusAsync();
        return callId;
    }

    internal async Task CallFinishedAsync(Guid callId, CallStatus status, string reason)
    {
        Interlocked.Decrement(ref _activeCalls);
        await _services.Events.CallEndedAsync(callId, status, reason);
        await PublishStatusAsync();
    }

    /// <summary>Prepara la toma de una llamada que atiende el bot; null si no es de esta cuenta.</summary>
    public IPhoneCall? CreateTakeOver(Guid callId) =>
        _botCalls.ContainsKey(callId) ? new TakenCall(this, callId, _logger) : null;

    /// <summary>Transferencia pedida por el bot; null si la llamada no es de esta cuenta.</summary>
    internal async Task<string?> TransferAsync(Guid callId, string target)
    {
        if (!_botCalls.TryGetValue(callId, out var call))
        {
            return null;
        }

        // Doble control: el Custom LLM ya no ofrece la herramienta fuera de horario, pero el motor no confía en eso.
        var state = _ext.BusinessHours.StateAt(_services.Time.GetUtcNow());
        if (state != BusinessHoursState.Open)
        {
            return "No se transfirió: la extensión está fuera del horario de atención. Ofrece tomar un recado.";
        }

        return await call.Bridge.TransferAsync(target)
            ? $"Llamada transferida a {target}."
            : "La central no aceptó la transferencia. Discúlpate y ofrece tomar un recado.";
    }

    /// <summary>Pide al puente que suelte la llamada; false si ya terminó o la tomó otra persona.</summary>
    internal bool RequestHandOff(Guid callId, TakenCall taker)
    {
        if (!_botCalls.TryRemove(callId, out var call))
        {
            return false;
        }

        call.Taker = taker;
        call.Bridge.HandOff();
        return true;
    }

    /// <summary>
    /// El bridge con ElevenLabs trabaja en G.711: de los códecs de la cuenta se ofrecen PCMU y PCMA en su orden.
    /// </summary>
    private RTPSession CreateRtpSession()
    {
        var formats = _ext.Codecs
            .Select(c => c.ToUpperInvariant() switch
            {
                "PCMU" => new AudioFormat(SDPWellKnownMediaFormatsEnum.PCMU),
                "PCMA" => new AudioFormat(SDPWellKnownMediaFormatsEnum.PCMA),
                _ => (AudioFormat?)null,
            })
            .OfType<AudioFormat>()
            .ToList();
        if (formats.Count == 0)
        {
            formats = [new AudioFormat(SDPWellKnownMediaFormatsEnum.PCMU), new AudioFormat(SDPWellKnownMediaFormatsEnum.PCMA)];
        }

        var range = _services.Options.RtpPortStart > 0 && _services.Options.RtpPortEnd > _services.Options.RtpPortStart
            ? new PortRange(_services.Options.RtpPortStart, _services.Options.RtpPortEnd, true, null)
            : null;
        var rtp = new RTPSession(false, false, false, IPAddress.Any, 0, range);
        rtp.addTrack(new MediaStreamTrack(formats, MediaStreamStatusEnum.SendRecv));
        return rtp;
    }

    private SIPChannel CreateChannel()
    {
        var endpoint = new IPEndPoint(IPAddress.Any, _ext.LocalPort);
        return _ext.Transport switch
        {
            SipTransport.Tcp => new SIPTCPChannel(endpoint, false),
            // Solo cliente: se valida el certificado del servidor con las raíces del sistema.
            SipTransport.Tls => new SIPTLSChannel(endpoint, false, null),
            _ => new SIPUDPChannel(endpoint),
        };
    }

    private async Task<IPAddress?> ResolvePublicIpAsync()
    {
        if (!string.IsNullOrWhiteSpace(_ext.PublicAddress) && IPAddress.TryParse(_ext.PublicAddress, out var configured))
        {
            return configured;
        }

        if (string.IsNullOrWhiteSpace(_ext.StunServer))
        {
            return null;
        }

        var (host, port) = SplitHostPort(_ext.StunServer!, 3478);
        var discovered = await Task.Run(() => STUNClient.GetPublicIPAddress(host, port));
        _logger.LogInformation("IP pública por STUN ({Stun}): {Ip}", _ext.StunServer, discovered);
        return discovered;
    }

    private SIPEndPoint? ResolveEndPoint(string hostAndPort)
    {
        var protocol = _ext.Transport switch
        {
            SipTransport.Tcp => SIPProtocolsEnum.tcp,
            SipTransport.Tls => SIPProtocolsEnum.tls,
            _ => SIPProtocolsEnum.udp,
        };
        var (host, port) = SplitHostPort(hostAndPort, protocol == SIPProtocolsEnum.tls ? 5061 : 5060);
        var address = IPAddress.TryParse(host, out var ip)
            ? ip
            : Dns.GetHostAddresses(host).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
        return address is null ? null : new SIPEndPoint(protocol, address, port);
    }

    internal static (string Host, int Port) SplitHostPort(string value, int defaultPort)
    {
        var text = value.Trim();
        var colon = text.LastIndexOf(':');
        return colon > 0 && int.TryParse(text[(colon + 1)..], out var port) ? (text[..colon], port) : (text, defaultPort);
    }

    internal static string HostOf(string server) => SplitHostPort(server, 5060).Host;

    private async Task SetStatusAsync(SipRegistrationState state, string? error)
    {
        lock (_stateLock)
        {
            _status = new SipRegistrationStatus(_ext.Id, state, CurrentServer, error, _activeCalls, _services.Time.GetUtcNow());
        }

        await PublishStatusAsync();
    }

    private async Task PublishStatusAsync()
    {
        try
        {
            await _services.Events.RegistrationChangedAsync(Status);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "No se pudo publicar el estado");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        try
        {
            // Des-registrar (expires 0) para que el servidor no siga mandando llamadas a una cuenta que ya no atiende.
            _registration?.Stop(true);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error al des-registrar");
        }

        if (_keepAlive is not null)
        {
            await _keepAlive;
        }

        _transport?.Shutdown();
        _cts.Dispose();
    }
}
