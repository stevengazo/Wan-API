using Wan.Domain.Entities;
using Wan.Infrastructure.Transfers;

namespace Wan.Tests.Llm;

public class DirectoryPromptTests
{
    private static readonly DirectoryEntry[] Entries =
    [
        new() { Name = "Ventas", Department = "Comercial", Target = "101", Description = "Cotizaciones y precios de planes" },
        new() { Name = "Ana Rodríguez", Department = "Facturación", Target = "102", Description = "Cobros, facturas y reembolsos" },
        new() { Name = "Soporte técnico", Target = "sip:soporte@pbx.local", Description = "Fallas del servicio" },
    ];

    [Fact]
    public void BuildListsEveryEntryWithItsTarget()
    {
        var prompt = DirectoryPrompt.Build(Entries)!;

        Assert.Contains("Ventas (Comercial): destino 101. Atiende: Cotizaciones y precios de planes", prompt);
        Assert.Contains("Soporte técnico: destino sip:soporte@pbx.local", prompt);
    }

    [Fact]
    public void BuildWithoutEntriesIsNull() => Assert.Null(DirectoryPrompt.Build([]));

    [Fact]
    public void SearchIgnoresAccentsAndCase() =>
        Assert.Equal("Ana Rodríguez", DirectoryPrompt.Search(Entries, "ANA RODRIGUEZ").First().Name);

    [Fact]
    public void SearchMatchesByTopic() =>
        Assert.Equal("Ana Rodríguez", DirectoryPrompt.Search(Entries, "quiero un reembolso").Single().Name);

    [Fact]
    public void NameMatchesRankFirst()
    {
        var entries = Entries.Append(new DirectoryEntry { Name = "Recepción", Target = "100", Description = "Pasa a ventas si no sabe" });

        Assert.Equal("Ventas", DirectoryPrompt.Search(entries, "ventas").First().Name);
    }

    [Fact]
    public void CommonWordsDoNotMatch() =>
        Assert.Equal("Soporte técnico", DirectoryPrompt.Search(Entries, "quiero hablar con técnico").Single().Name);

    [Fact]
    public void SearchByExtensionNumber() => Assert.Equal("Ventas", DirectoryPrompt.Search(Entries, "101").Single().Name);

    [Fact]
    public void SearchWithoutMatchesIsEmpty() => Assert.Empty(DirectoryPrompt.Search(Entries, "xyz"));
}
