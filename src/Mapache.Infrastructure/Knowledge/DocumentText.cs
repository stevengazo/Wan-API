using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using UglyToad.PdfPig;

namespace Mapache.Infrastructure.Knowledge;

/// <summary>Archivo que no se puede leer; el mensaje se muestra en el panel.</summary>
public sealed class DocumentTextException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Extrae texto de los archivos que se suben a una base y lo parte en fragmentos para buscar.</summary>
public static partial class DocumentText
{
    public static readonly IReadOnlyList<string> SupportedExtensions = [".pdf", ".docx", ".txt", ".md", ".csv", ".html", ".htm"];

    public static string Extract(string fileName, byte[] content)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        try
        {
            var text = extension switch
            {
                ".pdf" => Pdf(content),
                ".docx" => Docx(content),
                ".html" or ".htm" => Html(Utf8(content)),
                ".txt" or ".md" or ".csv" => Utf8(content),
                _ => throw new DocumentTextException($"Formato no soportado ({extension}). Usa {string.Join(", ", SupportedExtensions)}"),
            };
            return Clean(text);
        }
        catch (DocumentTextException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new DocumentTextException($"No se pudo leer el archivo: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Fragmentos de hasta <paramref name="size"/> caracteres cortados en límites de párrafo u oración, con un
    /// solapamiento corto para no perder el contexto entre uno y otro.
    /// </summary>
    public static IReadOnlyList<string> Chunk(string text, int size = 900, int overlap = 150)
    {
        var chunks = new List<string>();
        var start = 0;
        while (start < text.Length)
        {
            var end = Math.Min(start + size, text.Length);
            if (end < text.Length)
            {
                var window = text[start..end];
                var cut = window.LastIndexOf("\n\n", StringComparison.Ordinal);
                if (cut < size / 2)
                {
                    cut = Math.Max(window.LastIndexOf(". ", StringComparison.Ordinal) + 1, window.LastIndexOf('\n'));
                }

                if (cut > size / 2)
                {
                    end = start + cut;
                }
            }

            var chunk = text[start..end].Trim();
            if (chunk.Length > 0)
            {
                chunks.Add(chunk);
            }

            if (end >= text.Length)
            {
                break;
            }

            start = Math.Max(end - overlap, start + 1);
            // El solapamiento arranca en una palabra entera.
            var space = text.IndexOf(' ', start);
            if (space > 0 && space < end)
            {
                start = space + 1;
            }
        }

        return chunks;
    }

    /// <summary>Minúsculas sin tildes, igual que se normaliza la búsqueda.</summary>
    public static string Normalize(string text)
    {
        var plain = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                plain.Append(char.ToLowerInvariant(c));
            }
        }

        return plain.ToString();
    }

    private static string Pdf(byte[] content)
    {
        using var pdf = PdfDocument.Open(content);
        return string.Join("\n\n", pdf.GetPages().Select(p => p.Text));
    }

    /// <summary>Un .docx es un zip: el texto está en los párrafos (w:p) de word/document.xml.</summary>
    private static string Docx(byte[] content)
    {
        using var zip = new ZipArchive(new MemoryStream(content));
        var entry = zip.GetEntry("word/document.xml") ?? throw new DocumentTextException("El .docx no tiene contenido");
        using var stream = entry.Open();
        XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var paragraphs = XDocument.Load(stream).Descendants(w + "p")
            .Select(p => string.Concat(p.Descendants(w + "t").Select(t => t.Value)));
        return string.Join("\n\n", paragraphs);
    }

    private static string Html(string html)
    {
        var withoutScripts = ScriptOrStyle().Replace(html, " ");
        var withBreaks = BlockTag().Replace(withoutScripts, "\n\n");
        return WebUtility.HtmlDecode(AnyTag().Replace(withBreaks, " "));
    }

    private static string Utf8(byte[] content) => Encoding.UTF8.GetString(content).TrimStart('﻿');

    private static string Clean(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(l => Spaces().Replace(l, " ").Trim());
        return ManyBlankLines().Replace(string.Join('\n', lines), "\n\n").Trim();
    }

    [GeneratedRegex(@"<(script|style)[^>]*>.*?</\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ScriptOrStyle();

    [GeneratedRegex(@"</?(p|div|br|li|h[1-6]|tr|section|article)[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockTag();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex AnyTag();

    [GeneratedRegex(@"[ \t ]+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ManyBlankLines();
}
