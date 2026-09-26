using Mapache.Domain.Entities;
using Mapache.Infrastructure.Persistence;
using Mapache.Infrastructure.Tools;
using Microsoft.EntityFrameworkCore;

namespace Mapache.Infrastructure.Transfers;

/// <summary>Transferencia de la llamada en curso; la implementa la API con el motor SIP.</summary>
public interface ICallTransfer
{
    /// <summary>Devuelve el resultado en texto para el modelo.</summary>
    Task<string> TransferAsync(Guid callId, string target, CancellationToken ct);
}

/// <summary>
/// Herramienta <c>transfer_call</c> del Custom LLM. Solo transfiere a destinos del directorio: el modelo no
/// puede marcar un número arbitrario (evita fraude con llamadas a números de pago).
/// </summary>
public static class CallTransferTool
{
    public const string Name = "transfer_call";

    public static BotTool Create(Guid callId, IReadOnlyCollection<DirectoryEntry> directory, ICallTransfer transfer) => new(
        Name,
        "Transfiere la llamada a una persona o área del directorio de transferencias. Antes de usarla, dile a quien " +
        "llama que lo vas a transferir. Después de transferir la llamada ya no sigue contigo.",
        ToolSchema.Object(("target", "string", "Destino exacto del directorio (el valor después de \"destino\") o el nombre de la persona o área", true)),
        async (arguments, ct) =>
        {
            var target = arguments["target"]?.GetValue<string>()?.Trim() ?? "";
            if (Resolve(directory, target) is not { } entry)
            {
                return "Ese destino no está en el directorio: no se transfirió. Pregunta con quién quiere hablar o usa uno de la lista.";
            }

            return await transfer.TransferAsync(callId, entry.Target, ct);
        });

    /// <summary>Por destino exacto o, si no, por nombre cuando la coincidencia es única.</summary>
    public static DirectoryEntry? Resolve(IReadOnlyCollection<DirectoryEntry> directory, string target)
    {
        if (target.Length == 0)
        {
            return null;
        }

        var exact = directory.FirstOrDefault(d => string.Equals(d.Target, target, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }

        var matches = DirectoryPrompt.Search(directory, target, limit: 2);
        return matches.Count == 1 ? matches[0] : null;
    }
}

/// <summary>
/// Ofrece <c>transfer_call</c> solo cuando tiene sentido: hay una llamada, la extensión está en horario y el
/// directorio tiene destinos. Sin <see cref="ICallTransfer"/> (fuera de la API) no ofrece nada.
/// </summary>
public sealed class TransferToolSource(MapacheDbContext db, TimeProvider time, ICallTransfer? transfer = null) : IBotToolSource
{
    public async Task<IReadOnlyList<BotTool>> GetToolsAsync(BotCallContext call, CancellationToken ct)
    {
        if (transfer is null || call is not { CallId: { } callId, ExtensionId: { } extensionId })
        {
            return [];
        }

        var extension = await db.Extensions.AsNoTracking().SingleOrDefaultAsync(e => e.Id == extensionId, ct);
        if (extension is null || extension.BusinessHours.StateAt(time.GetUtcNow()) != BusinessHoursState.Open)
        {
            return [];
        }

        var directory = await db.DirectoryEntries.AsNoTracking().Where(d => d.Enabled).ToListAsync(ct);
        return directory.Count == 0 ? [] : [CallTransferTool.Create(callId, directory, transfer)];
    }
}
