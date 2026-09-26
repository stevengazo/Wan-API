namespace Wan.Domain.Entities;

/// <summary>Grabación de una conversación. El audio vive en disco; aquí solo la referencia.</summary>
public class Recording
{
    public Guid Id { get; set; }

    public required string ConversationId { get; set; }

    /// <summary>Nombre del archivo (clave del objeto en S3 o Azure) dentro del almacenamiento.</summary>
    public required string FileName { get; set; }

    /// <summary>Dónde quedó guardada: cambiar el almacenamiento no mueve las grabaciones anteriores.</summary>
    public StorageProvider Storage { get; set; } = StorageProvider.Local;

    /// <summary>audio/mpeg (ElevenLabs) o audio/wav (grabación de Wan).</summary>
    public string ContentType { get; set; } = "audio/mpeg";

    /// <summary>Llamada de la que salió, si la grabó Wan.</summary>
    public Guid? CallId { get; set; }

    public double? DurationSeconds { get; set; }

    /// <summary>Guardada cifrada (AES-256-GCM): se descifra en la API al escucharla.</summary>
    public bool Encrypted { get; set; }

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

    /// <summary>Días que se guardan las grabaciones; nulo = sin borrado automático.</summary>
    public int? RecordingRetentionDays { get; set; }

    /// <summary>Días que se guardan las transcripciones de las llamadas; nulo = sin borrado automático.</summary>
    public int? TranscriptRetentionDays { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
