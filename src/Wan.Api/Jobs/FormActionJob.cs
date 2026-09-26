using Hangfire;
using Mapache.Api.Realtime;
using Microsoft.AspNetCore.SignalR;
using Mapache.Infrastructure.Actions;

namespace Mapache.Api.Jobs;

/// <summary>
/// Job de Hangfire que ejecuta una acción de formulario. Si el destino falla, Hangfire reintenta con
/// esperas crecientes; cada intento queda en la <c>ActionExecution</c>.
/// </summary>
public sealed class FormActionJob(FormActionRunner runner)
{
    [AutomaticRetry(Attempts = 5, DelaysInSeconds = [30, 120, 600, 1800, 3600], OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public Task RunAsync(Guid executionId, CancellationToken ct) => runner.RunAsync(executionId, ct);
}

public sealed class HangfireFormActionQueue(IBackgroundJobClient jobs) : IFormActionQueue
{
    public void Enqueue(Guid executionId) => jobs.Enqueue<FormActionJob>(job => job.RunAsync(executionId, CancellationToken.None));
}

public sealed class SignalRFormActionNotifier(IHubContext<EventsHub, IEventsClient> events) : IFormActionNotifier
{
    public Task ExecutionChangedAsync() => events.Clients.All.FormSubmissionsChanged();
}
