using Microsoft.AspNetCore.Authorization;
using Mapache.Api.Sip;
using Mapache.Domain.Entities;
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

    /// <summary>Cambió el registro de una extensión (registrando, registrada, error) o sus llamadas activas.</summary>
    Task ExtensionStatusChanged(ExtensionStatusDto status);

    /// <summary>Empezó, se contestó o terminó una llamada.</summary>
    Task CallsChanged();

    /// <summary>Nueva línea de transcripción de una llamada en curso.</summary>
    Task CallTranscript(Guid callId, TranscriptSpeaker speaker, string text);
}

/// <summary>Hub de solo servidor → panel. En Fase 2 suma estado de registro SIP y llamadas en vivo.</summary>
[Authorize]
public sealed class EventsHub : Hub<IEventsClient>
{
    public const string Path = "/hubs/events";
}
