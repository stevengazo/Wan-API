using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mapache.Sip.Engine;

/// <summary>Dependencias que comparten las cuentas en funcionamiento.</summary>
internal sealed record SipRuntimeServices(
    ISipEventSink Events,
    IVoiceAgentSource VoiceAgent,
    IHttpClientFactory Http,
    ILoggerFactory Loggers,
    TimeProvider Time,
    SipEngineOptions Options);

/// <summary>
/// Motor SIP: mantiene registrada cada cuenta habilitada y la vuelve a crear cuando cambia su configuración.
/// Se sincroniza al arrancar, cuando la API pide recargar y cada minuto por las dudas.
/// </summary>
public sealed class SipEngine : BackgroundService, ISipEngine
{
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromMinutes(1);

    private readonly ISipAccountSource _accounts;
    private readonly SipRuntimeServices _services;
    private readonly ILogger<SipEngine> _logger;
    private readonly SemaphoreSlim _reload = new(0, 1);
    private readonly Dictionary<Guid, SipAccountRuntime> _runtimes = [];
    private readonly Lock _runtimesLock = new();

    public SipEngine(
        ISipAccountSource accounts,
        ISipEventSink events,
        IVoiceAgentSource voiceAgent,
        IHttpClientFactory http,
        ILoggerFactory loggers,
        TimeProvider time,
        IOptions<SipEngineOptions> options)
    {
        _accounts = accounts;
        _services = new SipRuntimeServices(events, voiceAgent, http, loggers, time, options.Value);
        _logger = loggers.CreateLogger<SipEngine>();
        SIPSorcery.LogFactory.Set(loggers);
    }

    public void RequestReload()
    {
        // Si ya hay una recarga pendiente no hace falta otra.
        if (_reload.CurrentCount == 0)
        {
            _reload.Release();
        }
    }

    public IPhoneCall PlaceCall(Guid extensionId, string destination)
    {
        SipAccountRuntime? runtime;
        lock (_runtimesLock)
        {
            _runtimes.TryGetValue(extensionId, out runtime);
        }

        return runtime is null
            ? throw new InvalidOperationException("La extensión no está activa: habilítala y espera a que se registre.")
            : runtime.CreateOutboundCall(destination);
    }

    public IPhoneCall TakeCall(Guid callId)
    {
        List<SipAccountRuntime> runtimes;
        lock (_runtimesLock)
        {
            runtimes = [.. _runtimes.Values];
        }

        return runtimes.Select(r => r.CreateTakeOver(callId)).FirstOrDefault(c => c is not null)
            ?? throw new InvalidOperationException("La llamada ya terminó, la tomó otra persona o todavía no la atiende el bot.");
    }

    public async Task<string> TransferAsync(Guid callId, string target)
    {
        List<SipAccountRuntime> runtimes;
        lock (_runtimesLock)
        {
            runtimes = [.. _runtimes.Values];
        }

        foreach (var runtime in runtimes)
        {
            if (await runtime.TransferAsync(callId, target) is { } result)
            {
                return result;
            }
        }

        return "No se encontró la llamada en curso: no se pudo transferir.";
    }

    public IReadOnlyList<SipRegistrationStatus> GetStatuses()
    {
        lock (_runtimesLock)
        {
            return _runtimes.Values.Select(r => r.Status).ToList();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_services.Options.Enabled)
        {
            _logger.LogInformation("Motor SIP deshabilitado (Sip:Enabled=false)");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReconcileAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error sincronizando las cuentas SIP");
            }

            try
            {
                await _reload.WaitAsync(ReconcileInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ReconcileAsync(CancellationToken ct)
    {
        var wanted = (await _accounts.GetEnabledAsync(ct)).ToDictionary(a => a.Extension.Id);
        List<SipAccountRuntime> toStop;
        List<SipAccount> toStart;
        lock (_runtimesLock)
        {
            // Se reinicia la cuenta si se borró, se deshabilitó o cambió cualquier dato (UpdatedAt).
            toStop = _runtimes
                .Where(r => !wanted.TryGetValue(r.Key, out var account) || account.Extension.UpdatedAt != r.Value.ConfigVersion)
                .Select(r => r.Value)
                .ToList();
            foreach (var runtime in toStop)
            {
                _runtimes.Remove(runtime.Status.ExtensionId);
            }

            toStart = wanted.Values.Where(a => !_runtimes.ContainsKey(a.Extension.Id)).ToList();
        }

        foreach (var runtime in toStop)
        {
            await runtime.DisposeAsync();
        }

        foreach (var account in toStart)
        {
            var runtime = new SipAccountRuntime(account, _services);
            lock (_runtimesLock)
            {
                _runtimes[account.Extension.Id] = runtime;
            }

            await runtime.StartAsync();
        }

        if (toStop.Count + toStart.Count > 0)
        {
            _logger.LogInformation("Cuentas SIP: {Stopped} detenidas, {Started} iniciadas, {Total} activas", toStop.Count, toStart.Count, wanted.Count);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        List<SipAccountRuntime> runtimes;
        lock (_runtimesLock)
        {
            runtimes = [.. _runtimes.Values];
            _runtimes.Clear();
        }

        foreach (var runtime in runtimes)
        {
            await runtime.DisposeAsync();
        }
    }
}

public static class SipEngineServiceCollectionExtensions
{
    /// <summary>
    /// Registra el motor SIP. La API aporta <see cref="ISipAccountSource"/>, <see cref="IVoiceAgentSource"/> e
    /// <see cref="ISipEventSink"/>.
    /// </summary>
    public static IServiceCollection AddSipEngine(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<SipEngineOptions>(configuration.GetSection(SipEngineOptions.SectionName));
        services.AddHttpClient("elevenlabs");
        services.AddSingleton<SipEngine>();
        services.AddSingleton<ISipEngine>(sp => sp.GetRequiredService<SipEngine>());
        services.AddHostedService(sp => sp.GetRequiredService<SipEngine>());
        return services;
    }
}
