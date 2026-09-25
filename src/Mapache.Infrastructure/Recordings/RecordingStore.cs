using Microsoft.Extensions.Options;

namespace Mapache.Infrastructure.Recordings;

public sealed class RecordingOptions
{
    public const string SectionName = "Recordings";

    /// <summary>Carpeta de las grabaciones locales. En Docker es un volumen para que sobrevivan a los redeploys.</summary>
    public string Path { get; set; } = "data/recordings";
}

/// <summary>Guarda y abre los audios de las grabaciones en disco.</summary>
public sealed class RecordingStore(IOptions<RecordingOptions> options)
{
    private string Root => System.IO.Path.GetFullPath(options.Value.Path);

    /// <summary>Escribe el audio y devuelve el nombre de archivo, derivado del id de la grabación.</summary>
    public async Task<string> SaveAsync(Guid recordingId, byte[] audio, CancellationToken ct)
    {
        System.IO.Directory.CreateDirectory(Root);
        var fileName = $"{recordingId:N}.mp3";
        await File.WriteAllBytesAsync(System.IO.Path.Combine(Root, fileName), audio, ct);
        return fileName;
    }

    /// <summary>Ruta donde escribir un archivo nuevo; crea la carpeta si hace falta.</summary>
    public string FullPath(string fileName)
    {
        System.IO.Directory.CreateDirectory(Root);
        var path = System.IO.Path.GetFullPath(System.IO.Path.Combine(Root, fileName));
        return path.StartsWith(Root, StringComparison.Ordinal) ? path : throw new InvalidOperationException("Nombre de archivo inválido");
    }

    public string? PathFor(string fileName)
    {
        // El nombre sale de la base, pero igual se impide salir de la carpeta.
        var path = System.IO.Path.GetFullPath(System.IO.Path.Combine(Root, fileName));
        return path.StartsWith(Root, StringComparison.Ordinal) && File.Exists(path) ? path : null;
    }

    public void Delete(string fileName)
    {
        if (PathFor(fileName) is { } path)
        {
            File.Delete(path);
        }
    }
}
