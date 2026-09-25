using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Mapache.Api.Realtime;

/// <summary>
/// Eventos que el servidor empuja al panel. Los nombres de método son los que escucha el cliente
/// (frontend/src/realtime/useRealtime.ts); mantenerlos sincronizados.
/// </summary>
public interface IEventsClient
{
    /// <summary>Se creó, editó o borró una extensión: el panel refresca la lista.</summary>
    Task ExtensionsChanged();

    /// <summary>El bot dejó un recado o alguien cambió su estado.</summary>
    Task MessagesChanged();

    /// <summary>El bot envió un formulario o alguien cambió el estado de una respuesta.</summary>
    Task FormSubmissionsChanged();

    /// <summary>Llegó o se borró una grabación.</summary>
    Task RecordingsChanged();
}

/// <summary>Hub de solo servidor → panel. En Fase 2 suma estado de registro SIP y llamadas en vivo.</summary>
[Authorize]
public sealed class EventsHub : Hub<IEventsClient>
{
    public const string Path = "/hubs/events";
}
