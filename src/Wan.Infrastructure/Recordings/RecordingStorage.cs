using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using Wan.Domain.Entities;
using Wan.Infrastructure.Persistence;
using Wan.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace Wan.Infrastructure.Recordings;

/// <summary>
/// Dónde escuchar una grabación: un archivo local (con rangos), una URL firmada de corta duración o, si está
/// cifrada, el audio ya descifrado en memoria.
/// </summary>
public sealed record RecordingLocation(string? LocalPath, Uri? Url, string ContentType, byte[]? Content = null);

/// <summary>Un destino de grabaciones: disco, S3 o Azure Blob.</summary>
public interface IRecordingStorage
{
    Task SaveAsync(string key, Stream content, string contentType, CancellationToken ct);

    /// <summary>Nulo si el objeto ya no existe.</summary>
    Task<RecordingLocation?> OpenAsync(string key, string contentType, CancellationToken ct);

    Task DeleteAsync(string key, CancellationToken ct);

    /// <summary>El archivo completo, para descifrarlo.</summary>
    Task<byte[]> ReadAsync(string key, CancellationToken ct);
}

/// <summary>Enlaces a la nube: lo justo para cargar el reproductor, que igual vuelve a pedir uno si se vence.</summary>
internal static class SignedUrl
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
}

public sealed class LocalRecordingStorage(RecordingStore store) : IRecordingStorage
{
    public async Task SaveAsync(string key, Stream content, string contentType, CancellationToken ct)
    {
        await using var file = File.Create(store.FullPath(key));
        await content.CopyToAsync(file, ct);
    }

    public Task<RecordingLocation?> OpenAsync(string key, string contentType, CancellationToken ct) =>
        Task.FromResult(store.PathFor(key) is { } path ? new RecordingLocation(path, null, contentType) : null);

    public Task DeleteAsync(string key, CancellationToken ct)
    {
        store.Delete(key);
        return Task.CompletedTask;
    }

    public Task<byte[]> ReadAsync(string key, CancellationToken ct) =>
        store.PathFor(key) is { } path ? File.ReadAllBytesAsync(path, ct) : throw new FileNotFoundException("La grabación ya no está en el disco");
}

public sealed class S3RecordingStorage(StorageSettings settings, string secretKey) : IRecordingStorage, IDisposable
{
    private readonly AmazonS3Client _client = CreateClient(settings, secretKey);

    private string Bucket => settings.S3Bucket ?? throw new InvalidOperationException("Falta el bucket de S3");

    private static AmazonS3Client CreateClient(StorageSettings settings, string secretKey)
    {
        var credentials = new BasicAWSCredentials(settings.S3AccessKeyId, secretKey);
        var config = new AmazonS3Config();
        if (!string.IsNullOrWhiteSpace(settings.S3ServiceUrl))
        {
            // Compatibles con S3 (R2, MinIO, Spaces): URL propia y rutas con el bucket, no subdominios.
            config.ServiceURL = settings.S3ServiceUrl;
            config.ForcePathStyle = true;
            config.AuthenticationRegion = string.IsNullOrWhiteSpace(settings.S3Region) ? "auto" : settings.S3Region;
        }
        else
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(string.IsNullOrWhiteSpace(settings.S3Region) ? "us-east-1" : settings.S3Region);
        }

        return new AmazonS3Client(credentials, config);
    }

    public async Task SaveAsync(string key, Stream content, string contentType, CancellationToken ct) =>
        await _client.PutObjectAsync(new PutObjectRequest { BucketName = Bucket, Key = key, InputStream = content, ContentType = contentType }, ct);

    public async Task<RecordingLocation?> OpenAsync(string key, string contentType, CancellationToken ct)
    {
        var url = await _client.GetPreSignedURLAsync(new GetPreSignedUrlRequest
        {
            BucketName = Bucket,
            Key = key,
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.Add(SignedUrl.Lifetime),
            // El SDK firma en HTTPS por defecto: un MinIO interno en http:// necesita el enlace en http.
            Protocol = settings.S3ServiceUrl?.StartsWith("http://", StringComparison.OrdinalIgnoreCase) == true ? Protocol.HTTP : Protocol.HTTPS,
        });
        return new RecordingLocation(null, new Uri(url), contentType);
    }

    public async Task DeleteAsync(string key, CancellationToken ct) =>
        await _client.DeleteObjectAsync(Bucket, key, ct);

    public async Task<byte[]> ReadAsync(string key, CancellationToken ct)
    {
        using var response = await _client.GetObjectAsync(Bucket, key, ct);
        using var buffer = new MemoryStream();
        await response.ResponseStream.CopyToAsync(buffer, ct);
        return buffer.ToArray();
    }

    public void Dispose() => _client.Dispose();
}

public sealed class AzureBlobRecordingStorage(string connectionString, string container) : IRecordingStorage
{
    private readonly BlobContainerClient _container = new(connectionString, container);

    public async Task SaveAsync(string key, Stream content, string contentType, CancellationToken ct)
    {
        await _container.CreateIfNotExistsAsync(cancellationToken: ct);
        await _container.GetBlobClient(key).UploadAsync(
            content, new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = contentType } }, ct);
    }

    public Task<RecordingLocation?> OpenAsync(string key, string contentType, CancellationToken ct)
    {
        var blob = _container.GetBlobClient(key);
        if (!blob.CanGenerateSasUri)
        {
            throw new InvalidOperationException("La cadena de conexión de Azure debe incluir la clave de la cuenta para firmar enlaces");
        }

        var url = blob.GenerateSasUri(BlobSasPermissions.Read, DateTimeOffset.UtcNow.Add(SignedUrl.Lifetime));
        return Task.FromResult<RecordingLocation?>(new RecordingLocation(null, url, contentType));
    }

    public async Task DeleteAsync(string key, CancellationToken ct) =>
        await _container.GetBlobClient(key).DeleteIfExistsAsync(cancellationToken: ct);

    public async Task<byte[]> ReadAsync(string key, CancellationToken ct) =>
        (await _container.GetBlobClient(key).DownloadContentAsync(ct)).Value.Content.ToArray();
}

/// <summary>
/// Guarda, abre y borra grabaciones en el almacenamiento que corresponda: las nuevas van al configurado en el
/// panel; las existentes se abren donde quedaron (<see cref="Recording.Storage"/>).
/// </summary>
public sealed class RecordingService(WanDbContext db, RecordingStore localStore, ISecretProtector protector, TimeProvider time)
{
    public async Task<Recording> SaveAsync(
        string conversationId, Stream content, string contentType, Guid? callId, double? durationSeconds, CancellationToken ct)
    {
        var settings = await db.StorageSettings.AsNoTracking().SingleOrDefaultAsync(ct) ?? new StorageSettings();
        if (settings.EncryptRecordings)
        {
            // Se cifra en memoria: una grabación de una hora en WAV son ~60 MB, manejable.
            using var plain = new MemoryStream();
            await content.CopyToAsync(plain, ct);
            content = new MemoryStream(RecordingCipher.Encrypt(await GetOrCreateKeyAsync(ct), plain.ToArray()));
        }

        var recording = new Recording
        {
            Id = Guid.CreateVersion7(),
            ConversationId = conversationId,
            FileName = "",
            Storage = settings.Provider,
            ContentType = contentType,
            CallId = callId,
            DurationSeconds = durationSeconds,
            Encrypted = settings.EncryptRecordings,
            CreatedAt = time.GetUtcNow(),
        };
        var extension = contentType == "audio/wav" ? "wav" : "mp3";
        var prefix = string.IsNullOrWhiteSpace(settings.Prefix) || settings.Provider == StorageProvider.Local ? "" : settings.Prefix.Trim('/') + "/";
        recording.FileName = $"{prefix}{recording.CreatedAt:yyyy/MM}/{recording.Id:N}.{extension}";
        if (settings.Provider == StorageProvider.Local)
        {
            recording.FileName = $"{recording.Id:N}.{extension}";
        }

        var storage = Create(settings);
        try
        {
            recording.SizeBytes = content.CanSeek ? content.Length : 0;
            await storage.SaveAsync(recording.FileName, content, contentType, ct);
        }
        finally
        {
            (storage as IDisposable)?.Dispose();
        }

        db.Recordings.Add(recording);
        await db.SaveChangesAsync(ct);
        return recording;
    }

    public async Task<RecordingLocation?> OpenAsync(Recording recording, CancellationToken ct)
    {
        var storage = await ForAsync(recording.Storage, ct);
        try
        {
            if (recording.Encrypted)
            {
                // Cifrada no sirve un enlace directo: el navegador recibiría el archivo cifrado.
                var sealedData = await storage.ReadAsync(recording.FileName, ct);
                return new RecordingLocation(null, null, recording.ContentType, RecordingCipher.Decrypt(await GetOrCreateKeyAsync(ct), sealedData));
            }

            return await storage.OpenAsync(recording.FileName, recording.ContentType, ct);
        }
        finally
        {
            (storage as IDisposable)?.Dispose();
        }
    }

    public async Task DeleteAsync(Recording recording, CancellationToken ct)
    {
        var storage = await ForAsync(recording.Storage, ct);
        try
        {
            await storage.DeleteAsync(recording.FileName, ct);
        }
        finally
        {
            (storage as IDisposable)?.Dispose();
        }
    }

    /// <summary>Sube, firma y borra un archivo de prueba con la configuración dada, antes de guardarla.</summary>
    public async Task TestAsync(StorageSettings settings, CancellationToken ct)
    {
        var storage = Create(settings);
        try
        {
            var key = $"{(string.IsNullOrWhiteSpace(settings.Prefix) ? "" : settings.Prefix.Trim('/') + "/")}wan-prueba-{Guid.NewGuid():N}.txt";
            if (settings.Provider == StorageProvider.Local)
            {
                key = $"wan-prueba-{Guid.NewGuid():N}.txt";
            }

            using var content = new MemoryStream("Prueba de Wan"u8.ToArray());
            await storage.SaveAsync(key, content, "text/plain", ct);
            _ = await storage.OpenAsync(key, "text/plain", ct);
            await storage.DeleteAsync(key, ct);
        }
        finally
        {
            (storage as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// Clave de las grabaciones: se crea una sola vez y se guarda protegida con Data Protection. Si se pierden las
    /// llaves de Data Protection, las grabaciones cifradas quedan ilegibles (igual que los demás secretos).
    /// </summary>
    private async Task<byte[]> GetOrCreateKeyAsync(CancellationToken ct)
    {
        var settings = await db.StorageSettings.SingleOrDefaultAsync(ct);
        if (settings?.RecordingKeyEncrypted is { } stored)
        {
            return Convert.FromBase64String(protector.Unprotect(stored));
        }

        if (settings is null)
        {
            settings = new StorageSettings();
            db.StorageSettings.Add(settings);
        }

        var key = RecordingCipher.NewKey();
        settings.RecordingKeyEncrypted = protector.Protect(Convert.ToBase64String(key));
        settings.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return key;
    }

    private async Task<IRecordingStorage> ForAsync(StorageProvider provider, CancellationToken ct)
    {
        if (provider == StorageProvider.Local)
        {
            return new LocalRecordingStorage(localStore);
        }

        // Las credenciales son las de hoy: si se cambió de cuenta, las grabaciones viejas de la nube dejan de abrirse.
        var settings = await db.StorageSettings.AsNoTracking().SingleOrDefaultAsync(ct) ?? new StorageSettings();
        settings.Provider = provider;
        return Create(settings);
    }

    private IRecordingStorage Create(StorageSettings settings) => settings.Provider switch
    {
        StorageProvider.S3 => new S3RecordingStorage(settings, Unprotect(settings.S3SecretKeyEncrypted, "la clave secreta de S3")),
        StorageProvider.AzureBlob => new AzureBlobRecordingStorage(
            Unprotect(settings.AzureConnectionStringEncrypted, "la cadena de conexión de Azure"),
            settings.AzureContainer ?? throw new InvalidOperationException("Falta el contenedor de Azure")),
        _ => new LocalRecordingStorage(localStore),
    };

    private string Unprotect(string? value, string what) =>
        value is null ? throw new InvalidOperationException($"Falta {what}") : protector.Unprotect(value);
}
