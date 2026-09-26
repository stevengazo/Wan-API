using Mapache.Domain.Entities;
using Mapache.Infrastructure.Campaigns;

namespace Mapache.Tests.Llm;

public class CampaignTests
{
    [Fact]
    public void ParsesExcelSpanishCsvWithQuotesAndVariables()
    {
        const string csv = "Nombre;Teléfono;Monto adeudado;Fecha\n" +
            "\"Mora, Ana\";+506 8888-1234;15000;15 de octubre\n" +
            "Carlos;2222 3333;;\n";

        var result = ContactCsv.Parse(csv);

        Assert.Empty(result.Problems);
        Assert.Equal(2, result.Contacts.Count);
        Assert.Equal("+50688881234", result.Contacts[0].PhoneNumber);
        Assert.Equal("Mora, Ana", result.Contacts[0].Name);
        Assert.Equal("15000", result.Contacts[0].Variables["monto_adeudado"]);
        Assert.Equal("15 de octubre", result.Contacts[0].Variables["fecha"]);
        Assert.Empty(result.Contacts[1].Variables);
    }

    [Fact]
    public void ReportsMissingPhoneColumnInvalidRowsAndDuplicates()
    {
        Assert.Single(ContactCsv.Parse("nombre,email\nAna,a@b.com").Problems);

        var result = ContactCsv.Parse("phone,name\n88881234,Ana\nabc,Luis\n8888-1234,Ana otra vez");

        Assert.Single(result.Contacts);
        Assert.Equal(2, result.Problems.Count);
    }

    [Fact]
    public void PromptRendersContactVariables()
    {
        var campaign = new Campaign
        {
            Name = "Cobro",
            Instructions = "Recuerda a {{nombre}} que su pago de {{monto}} vence el {{fecha}}. {{inexistente}}",
            Outcomes = ["Pagará", "No pagará"],
        };
        var contact = new CampaignContact
        {
            PhoneNumber = "88881234",
            Name = "Ana",
            Variables = new() { ["monto"] = "₡15 000", ["fecha"] = "15 de octubre" },
        };

        var prompt = CampaignPrompt.Build(campaign, contact);

        Assert.Contains("SALIENTE", prompt);
        Assert.Contains("Recuerda a Ana que su pago de ₡15 000 vence el 15 de octubre.", prompt);
        Assert.DoesNotContain("{{", prompt);
        Assert.Contains(CampaignPrompt.ResultTool, prompt);
    }
}
