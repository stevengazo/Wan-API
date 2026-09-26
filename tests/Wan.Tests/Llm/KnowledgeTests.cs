using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Mapache.Infrastructure.Knowledge;
using Mapache.Infrastructure.Llm;

namespace Mapache.Tests.Llm;

public class KnowledgeTests
{
    [Fact]
    public void ChunksAreBoundedAndCoverTheWholeText()
    {
        var paragraphs = Enumerable.Range(1, 30).Select(i => $"Párrafo {i}. " + string.Concat(Enumerable.Repeat("texto de relleno ", 10)));
        var text = string.Join("\n\n", paragraphs);

        var chunks = DocumentText.Chunk(text, size: 500, overlap: 80);

        Assert.All(chunks, c => Assert.True(c.Length <= 500));
        Assert.StartsWith("Párrafo 1.", chunks[0]);
        Assert.Contains("Párrafo 30.", chunks[^1]);
    }

    [Fact]
    public void ShortTextIsASingleChunk() => Assert.Equal(["Hola"], DocumentText.Chunk("Hola"));

    [Fact]
    public void ReadsTextHtmlAndDocx()
    {
        Assert.Equal("Horario: lunes a viernes", DocumentText.Extract("horario.txt", Encoding.UTF8.GetBytes("﻿Horario:   lunes a viernes\r\n")));

        var html = DocumentText.Extract("p.html", Encoding.UTF8.GetBytes("<html><style>x{}</style><h1>Planes</h1><p>Pro &amp; Básico</p></html>"));
        Assert.Equal("Planes\n\nPro & Básico", html);

        Assert.Equal("Primer párrafo\n\nSegundo párrafo", DocumentText.Extract("d.docx", Docx("Primer párrafo", "Segundo párrafo")));
    }

    [Fact]
    public void RejectsUnsupportedFormats() =>
        Assert.Throws<DocumentTextException>(() => DocumentText.Extract("foto.png", [1, 2, 3]));

    [Theory]
    [InlineData("¿Cuánto cuesta el plan Pro?", "cuanto | cuesta | el | plan | pro")]
    [InlineData("?? !", null)]
    public void QueryWordsAreJoinedWithOr(string query, string? expected) => Assert.Equal(expected, KnowledgeService.ToTsQuery(query));

    [Fact]
    public void FormatRespectsTheBudget()
    {
        var hits = Enumerable.Range(1, 10).Select(i => new KnowledgeHit(new string('x', 900), $"doc{i}.pdf", "Planes", 1)).ToList();

        var text = KnowledgeService.Format(hits, maxChars: 2500)!;

        Assert.True(text.Length <= 2500);
        Assert.Contains("[Planes · doc1.pdf]", text);
        Assert.Null(KnowledgeService.Format([]));
    }

    [Fact]
    public void LastUserMessageSupportsTextParts()
    {
        var request = JsonNode.Parse("""
            {"messages":[{"role":"user","content":"hola"},{"role":"assistant","content":"¿En qué ayudo?"},
              {"role":"user","content":[{"type":"text","text":"precio del plan"}]}]}
            """)!.AsObject();

        Assert.Equal("precio del plan", ChatCompletionRouter.LastUserMessage(request));
    }

    private static byte[] Docx(params string[] paragraphs)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var xml = "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>"
                + string.Concat(paragraphs.Select(p => $"<w:p><w:r><w:t>{p}</w:t></w:r></w:p>"))
                + "</w:body></w:document>";
            using var writer = new StreamWriter(zip.CreateEntry("word/document.xml").Open());
            writer.Write(xml);
        }

        return stream.ToArray();
    }
}
