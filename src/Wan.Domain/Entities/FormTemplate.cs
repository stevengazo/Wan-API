namespace Wan.Domain.Entities;

public enum FormFieldType
{
    Text,
    Number,
    Phone,
    Email,
    Date,
    YesNo,

    /// <summary>Una de las opciones de <see cref="FormField.Options"/>.</summary>
    Choice,
}

/// <summary>Campo de un formulario. Se guarda como parte del JSON de <see cref="FormTemplate.Fields"/>.</summary>
public class FormField
{
    /// <summary>Identificador estable del campo (<c>nombre_cliente</c>); es la clave que usa el bot.</summary>
    public required string Key { get; set; }

    public required string Label { get; set; }

    public FormFieldType Type { get; set; } = FormFieldType.Text;

    public bool Required { get; set; }

    /// <summary>Indicación para el bot sobre cómo pedir o validar el dato.</summary>
    public string? Hint { get; set; }

    public List<string> Options { get; set; } = [];
}

/// <summary>Formulario que el bot llena durante la llamada con lo que le dice quien llama.</summary>
public class FormTemplate
{
    public Guid Id { get; set; }

    public required string Name { get; set; }

    /// <summary>Cuándo usarlo, en lenguaje natural: le da contexto al bot para decidir.</summary>
    public string? Description { get; set; }

    public List<FormField> Fields { get; set; } = [];

    /// <summary>Solo los formularios activos llegan al bot.</summary>
    public bool Enabled { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public enum SubmissionStatus
{
    New,
    Reviewed,
}

/// <summary>Respuesta de un formulario enviada por el bot.</summary>
public class FormSubmission
{
    public Guid Id { get; set; }

    public Guid FormTemplateId { get; set; }

    public FormTemplate? FormTemplate { get; set; }

    /// <summary>Valores por <see cref="FormField.Key"/>, ya normalizados.</summary>
    public Dictionary<string, string> Values { get; set; } = [];

    /// <summary>Conversación de ElevenLabs: enlaza con la grabación y la transcripción.</summary>
    public string? ConversationId { get; set; }

    public string? CallerNumber { get; set; }

    public SubmissionStatus Status { get; set; } = SubmissionStatus.New;

    public DateTimeOffset CreatedAt { get; set; }
}
