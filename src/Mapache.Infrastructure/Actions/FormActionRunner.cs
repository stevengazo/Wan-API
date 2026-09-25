using Mapache.Domain.Entities;
using Mapache.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Mapache.Infrastructure.Actions;

/// <summary>Avisa al panel que cambió el estado de una ejecución (SignalR en la API).</summary>
public interface IFormActionNotifier
{
    Task ExecutionChangedAsync();
}

/// <summary>Pone en cola la ejecución de una acción (Hangfire en la API).</summary>
public interface IFormActionQueue
{
    void Enqueue(Guid executionId);
}

/// <summary>
/// Ejecuta una acción y deja el resultado en su <see cref="ActionExecution"/>. Si falla vuelve a lanzar
/// la excepción para que la cola reintente.
/// </summary>
public sealed class FormActionRunner(
    MapacheDbContext db,
    FormActionSender sender,
    TimeProvider time,
    IFormActionNotifier notifier,
    ILogger<FormActionRunner> logger)
{
    public async Task RunAsync(Guid executionId, CancellationToken ct)
    {
        var execution = await db.ActionExecutions.Include(x => x.FormAction).SingleOrDefaultAsync(x => x.Id == executionId, ct);
        if (execution?.FormAction is not { } action)
        {
            // La respuesta o la acción se borró mientras esperaba en la cola.
            return;
        }

        var submission = await db.FormSubmissions.AsNoTracking().SingleAsync(s => s.Id == execution.FormSubmissionId, ct);
        var form = await db.FormTemplates.AsNoTracking().SingleAsync(f => f.Id == submission.FormTemplateId, ct);

        execution.Attempts++;
        try
        {
            await sender.SendAsync(action, form, submission, ct);
            execution.Status = ActionExecutionStatus.Succeeded;
            execution.LastError = null;
            execution.CompletedAt = time.GetUtcNow();
            await db.SaveChangesAsync(ct);
            await notifier.ExecutionChangedAsync();
        }
        catch (FormActionException ex)
        {
            execution.Status = ActionExecutionStatus.Failed;
            execution.LastError = ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message;
            execution.CompletedAt = time.GetUtcNow();
            await db.SaveChangesAsync(ct);
            await notifier.ExecutionChangedAsync();
            logger.LogWarning("Acción {Action} ({Type}) falló en el intento {Attempt}: {Error}", action.Name, action.Type, execution.Attempts, ex.Message);
            throw;
        }
    }

    /// <summary>Crea una ejecución por cada acción activa del formulario y las pone en cola.</summary>
    public static async Task<int> DispatchAsync(MapacheDbContext db, IFormActionQueue queue, FormSubmission submission, DateTimeOffset now, CancellationToken ct)
    {
        var actions = await db.FormActions.AsNoTracking()
            .Where(a => a.FormTemplateId == submission.FormTemplateId && a.Enabled)
            .Select(a => a.Id)
            .ToListAsync(ct);
        var executions = actions.Select(id => new ActionExecution
        {
            Id = Guid.CreateVersion7(),
            FormActionId = id,
            FormSubmissionId = submission.Id,
            CreatedAt = now,
        }).ToList();
        db.ActionExecutions.AddRange(executions);
        await db.SaveChangesAsync(ct);

        foreach (var execution in executions)
        {
            queue.Enqueue(execution.Id);
        }

        return executions.Count;
    }
}
