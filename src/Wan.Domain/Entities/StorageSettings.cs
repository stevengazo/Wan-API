namespace Wan.Domain.Entities;

/// <summary>Dónde se guardan las grabaciones.</summary>
public enum StorageProvider
{
    /// <summary>En el disco del servidor (volumen <c>recordings</c> en Docker).</summary>
    Local,

    /// <summary>Amazon S3 o compatible (MinIO, Cloudflare R2, DigitalOcean Spaces) con URL propia.</summary>
    S3,

    AzureBlob,
}

/// <summary>Almacenamiento de las grabaciones y sus credenciales (cifradas). Hay una sola fila.</summary>
public class StorageSettings
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;

    public StorageProvider Provider { get; set; } = StorageProvider.Local;

    public string? S3Bucket { get; set; }

    public string? S3Region { get; set; }

    /// <summary>Solo para servicios compatibles con S3 (https://&lt;cuenta&gt;.r2.cloudflarestorage.com, MinIO...).</summary>
    public string? S3ServiceUrl { get; set; }

    public string? S3AccessKeyId { get; set; }

    public string? S3SecretKeyEncrypted { get; set; }

    public string? AzureConnectionStringEncrypted { get; set; }

    public string? AzureContainer { get; set; }

    /// <summary>Carpeta dentro del bucket o contenedor ("wan/grabaciones").</summary>
    public string? Prefix { get; set; }

    /// <summary>Cifrar las grabaciones nuevas antes de guardarlas, en disco o en la nube.</summary>
    public bool EncryptRecordings { get; set; } = true;

    /// <summary>Clave AES-256 de las grabaciones, protegida con Data Protection. Se crea al cifrar la primera.</summary>
    public string? RecordingKeyEncrypted { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
