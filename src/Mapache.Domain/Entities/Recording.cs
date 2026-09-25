namespace Mapache.Domain.Entities;

/// <summary>Grabación de una conversación. El audio vive en disco; aquí solo la referencia.</summary>
public class Recording
{
    public Guid Id { get; set; }

    public required string ConversationId { get; set; }

    /// <summary>Nombre del archivo dentro de <c>Recordings:Path</c>.</summary>
    public required string FileName { get; set; }

    public long SizeBytes { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Opciones generales de las llamadas. Hay una sola fila.</summary>
public class CallSettings
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;

    /// <summary>Guardar el audio que envía ElevenLabs al terminar cada conversación.</summary>
    public bool RecordCalls { get; set; }

    /// <summary>Aviso de grabación que el bot debe dar al inicio de la llamada.</summary>
    public string? RecordingNotice { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
