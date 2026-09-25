namespace Mapache.Domain.Entities;

public enum FormActionType
{
    /// <summary>POST con el JSON de la respuesta a una URL propia, firmado si hay secreto.</summary>
    Webhook,

    /// <summary>Webhook de Teams (flujo "Post to a channel when a webhook request is received").</summary>
    Teams,

    /// <summary>Webhook entrante de un espacio de Google Chat.</summary>
    GoogleChat,

    /// <summary>Incoming webhook de Slack.</summary>
    Slack,

    /// <summary>Correo por el SMTP de Configuración.</summary>
    Email,

    /// <summary>
    /// Petición a la API de otro sistema (CRM, ERP, service desk) con método, headers y cuerpo propios; los
    /// datos van con marcadores <c>{{campo}}</c> para mapearlos al formato que espera el destino.
    /// </summary>
    HttpRequest,
}

/// <summary>Qué hacer cuando un formulario recibe una respuesta.</summary>
public class FormAction
{
    public Guid Id { get; set; }

    public Guid FormTemplateId { get; set; }

    public FormTemplate? FormTemplate { get; set; }

    public FormActionType Type { get; set; }

    /// <summary>Nombre para reconocerla en el panel ("Canal de ventas").</summary>
    public required string Name { get; set; }

    /// <summary>URL del webhook (con marcadores en peticiones HTTP), o destinatarios separados por coma si es correo.</summary>
    public required string Target { get; set; }

    /// <summary>Método de la petición HTTP (POST, PUT, PATCH...). Solo <see cref="FormActionType.HttpRequest"/>.</summary>
    public string? Method { get; set; }

    /// <summary>Cuerpo JSON con marcadores; vacío = el JSON estándar del webhook. Solo peticiones HTTP.</summary>
    public string? BodyTemplate { get; set; }

    /// <summary>Headers (p. ej. Authorization o API key) serializados y cifrados. Solo peticiones HTTP.</summary>
    public string? HeadersEncrypted { get; set; }

    /// <summary>Secreto HMAC del webhook propio, cifrado; nunca se expone por la API.</summary>
    public string? SecretEncrypted { get; set; }

    public bool Enabled { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }
}

public enum ActionExecutionStatus
{
    Pending,
    Succeeded,
    Failed,
}

/// <summary>Una ejecución de una acción para una respuesta: queda como log y permite reintentar.</summary>
public class ActionExecution
{
    public Guid Id { get; set; }

    public Guid FormActionId { get; set; }

    public FormAction? FormAction { get; set; }

    public Guid FormSubmissionId { get; set; }

    public ActionExecutionStatus Status { get; set; } = ActionExecutionStatus.Pending;

    public int Attempts { get; set; }

    /// <summary>Error del último intento fallido, recortado.</summary>
    public string? LastError { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }
}

/// <summary>Servidor de correo saliente para las acciones de tipo correo. Hay una sola fila.</summary>
public class SmtpSettings
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;

    public string? Host { get; set; }

    public int Port { get; set; } = 587;

    /// <summary>STARTTLS en 587 o TLS directo en 465; sin cifrado solo para servidores internos.</summary>
    public bool UseTls { get; set; } = true;

    public string? Username { get; set; }

    public string? PasswordEncrypted { get; set; }

    /// <summary>Remitente: <c>Mapache &lt;bot@empresa.com&gt;</c> o solo la dirección.</summary>
    public string? From { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
