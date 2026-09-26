using Mapache.Api.Audit;
using System.ComponentModel.DataAnnotations;
using Mapache.Api.Auth;
using Mapache.Domain.Entities;
using Mapache.Infrastructure.Persistence;
using Mapache.Infrastructure.Recordings;
using Mapache.Infrastructure.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Mapache.Api.Controllers;

/// <summary>Los secretos no salen de la API: solo se informa si están cargados.</summary>
public sealed record StorageSettingsDto(
    StorageProvider Provider,
    string? S3Bucket,
    string? S3Region,
    string? S3ServiceUrl,
    string? S3AccessKeyId,
    bool HasS3SecretKey,
    bool HasAzureConnectionString,
    string? AzureContainer,
    string? Prefix,
    bool EncryptRecordings);

/// <summary>Secretos vacíos conservan los guardados.</summary>
public sealed record SaveStorageSettingsRequest(
    StorageProvider Provider,
    [StringLength(128, ErrorMessage = Messages.MaxLength)] string? S3Bucket,
    [StringLength(64, ErrorMessage = Messages.MaxLength)] string? S3Region,
    [StringLength(256, ErrorMessage = Messages.MaxLength)] string? S3ServiceUrl,
    [StringLength(128, ErrorMessage = Messages.MaxLength)] string? S3AccessKeyId,
    [StringLength(256, ErrorMessage = Messages.MaxLength)] string? S3SecretKey,
    [StringLength(2000, ErrorMessage = Messages.MaxLength)] string? AzureConnectionString,
    [StringLength(128, ErrorMessage = Messages.MaxLength)] string? AzureContainer,
    [StringLength(256, ErrorMessage = Messages.MaxLength)] string? Prefix,
    bool EncryptRecordings = true);

[ApiController]
[Authorize(Roles = Roles.Admin)]
public sealed class StorageSettingsController(MapacheDbContext db, RecordingService recordings, ISecretProtector protector, TimeProvider time, AuditLog audit)
    : ControllerBase
{
    [HttpGet("api/settings/storage")]
    public async Task<StorageSettingsDto> Get(CancellationToken ct) =>
        ToDto(await db.StorageSettings.AsNoTracking().SingleOrDefaultAsync(ct) ?? new StorageSettings());

    [HttpPut("api/settings/storage")]
    public async Task<ActionResult<StorageSettingsDto>> Save(SaveStorageSettingsRequest request, CancellationToken ct)
    {
        var settings = await db.StorageSettings.SingleOrDefaultAsync(ct);
        if (settings is null)
        {
            settings = new StorageSettings();
            db.StorageSettings.Add(settings);
        }

        Apply(settings, request);
        if (Validate(settings) is { } problem)
        {
            return problem;
        }

        settings.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(AuditActions.SettingsChanged, "Almacenamiento de grabaciones", details: $"Destino: {settings.Provider}");
        return ToDto(settings);
    }

    /// <summary>Sube, firma y borra un archivo con los datos del formulario, sin guardarlos.</summary>
    [HttpPost("api/settings/storage/test")]
    public async Task<ActionResult<ToolResult>> Test(SaveStorageSettingsRequest request, CancellationToken ct)
    {
        var current = await db.StorageSettings.AsNoTracking().SingleOrDefaultAsync(ct) ?? new StorageSettings();
        Apply(current, request);
        if (Validate(current) is { } problem)
        {
            return problem;
        }

        try
        {
            await recordings.TestAsync(current, ct);
            return new ToolResult(true, "Funciona: se subió, se firmó y se borró un archivo de prueba.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ToolResult(false, $"No funcionó: {ex.Message}");
        }
    }

    private void Apply(StorageSettings settings, SaveStorageSettingsRequest request)
    {
        // Nulo conserva el valor y vacío lo borra: cambiar de destino no debe perder la configuración de los otros,
        // porque las grabaciones que ya están ahí se siguen abriendo con esas credenciales.
        static string? Merge(string? value, string? current) =>
            value is null ? current : string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        settings.Provider = request.Provider;
        settings.EncryptRecordings = request.EncryptRecordings;
        settings.S3Bucket = Merge(request.S3Bucket, settings.S3Bucket);
        settings.S3Region = Merge(request.S3Region, settings.S3Region);
        settings.S3ServiceUrl = Merge(request.S3ServiceUrl, settings.S3ServiceUrl);
        settings.S3AccessKeyId = Merge(request.S3AccessKeyId, settings.S3AccessKeyId);
        settings.AzureContainer = Merge(request.AzureContainer, settings.AzureContainer)?.ToLowerInvariant();
        settings.Prefix = Merge(request.Prefix, settings.Prefix)?.Trim('/');
        static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (Optional(request.S3SecretKey) is { } secret)
        {
            settings.S3SecretKeyEncrypted = protector.Protect(secret);
        }

        if (Optional(request.AzureConnectionString) is { } connection)
        {
            settings.AzureConnectionStringEncrypted = protector.Protect(connection);
        }
    }

    private ActionResult? Validate(StorageSettings settings)
    {
        if (settings.Provider == StorageProvider.S3)
        {
            if (settings.S3Bucket is null)
            {
                ModelState.AddModelError(nameof(SaveStorageSettingsRequest.S3Bucket), Messages.Required);
            }

            if (settings.S3AccessKeyId is null)
            {
                ModelState.AddModelError(nameof(SaveStorageSettingsRequest.S3AccessKeyId), Messages.Required);
            }

            if (settings.S3SecretKeyEncrypted is null)
            {
                ModelState.AddModelError(nameof(SaveStorageSettingsRequest.S3SecretKey), Messages.Required);
            }

            if (settings.S3ServiceUrl is not null && !Uri.TryCreate(settings.S3ServiceUrl, UriKind.Absolute, out _))
            {
                ModelState.AddModelError(nameof(SaveStorageSettingsRequest.S3ServiceUrl), "Debe ser una URL completa");
            }
        }
        else if (settings.Provider == StorageProvider.AzureBlob)
        {
            if (settings.AzureConnectionStringEncrypted is null)
            {
                ModelState.AddModelError(nameof(SaveStorageSettingsRequest.AzureConnectionString), Messages.Required);
            }

            if (settings.AzureContainer is null)
            {
                ModelState.AddModelError(nameof(SaveStorageSettingsRequest.AzureContainer), Messages.Required);
            }
        }

        return ModelState.IsValid ? null : ValidationProblem(ModelState);
    }

    private static StorageSettingsDto ToDto(StorageSettings s) => new(
        s.Provider, s.S3Bucket, s.S3Region, s.S3ServiceUrl, s.S3AccessKeyId, s.S3SecretKeyEncrypted is not null,
        s.AzureConnectionStringEncrypted is not null, s.AzureContainer, s.Prefix, s.EncryptRecordings);
}
