using Microsoft.AspNetCore.Authorization;
using Mapache.Api.Sip;
using Mapache.Domain.Entities;
using Microsoft.AspNetCore.SignalR;

namespace Mapache.Api.Realtime;

/// <summary>
/// Eventos que el servidor empuja al panel. Los nombres de método son los que escucha el cliente
/// (frontend/src/hooks/useRealtime.ts); mantenerlos sincronizados.
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

    /// <summary>Estado de una llamada al entrar, contestarse o terminar: el panel abre el modal de las entrantes.</summary>
    Task LiveCall(LiveCallDto call);

    /// <summary>Algo nuevo para avisar en el dispositivo (recado, formulario): el panel lo muestra como notificación.</summary>
    Task Notify(PanelNotification notification);
}

/// <summary>Llamada en vivo para el panel.</summary>
public sealed record LiveCallDto(
    Guid CallId,
    Guid ExtensionId,
    string ExtensionName,
    string CallerNumber,
    CallDirection Direction,
    CallStatus Status,
    string? EndReason,
    DateTimeOffset StartedAt,
    DateTimeOffset? AnsweredAt);

/// <summary>Aviso para el dispositivo. <see cref="Url"/> es la ruta del panel que abre al tocarlo.</summary>
public sealed record PanelNotification(string Kind, string Title, string Body, string Url);

/// <summary>Hub de solo servidor → panel.</summary>
[Authorize]
public sealed class EventsHub : Hub<IEventsClient>
{
    public const string Path = "/hubs/events";
}
