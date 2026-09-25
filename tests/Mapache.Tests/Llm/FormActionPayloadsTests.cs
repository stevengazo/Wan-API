using System.Security.Cryptography;
using System.Text;
using Mapache.Domain.Entities;
using Mapache.Infrastructure.Actions;

namespace Mapache.Tests.Llm;

public class FormActionPayloadsTests
{
    private static readonly FormTemplate Form = new()
    {
        Id = Guid.NewGuid(),
        Name = "Cotización",
        Fields = [new() { Key = "nombre", Label = "Nombre" }, new() { Key = "plan", Label = "Plan" }],
    };

    private static readonly FormSubmission Submission = new()
    {
        Id = Guid.NewGuid(),
        FormTemplateId = Form.Id,
        // Orden distinto al del formulario y un campo que ya no existe en la plantilla.
        Values = new() { ["plan"] = "Pro", ["viejo"] = "x", ["nombre"] = "Ana <b>" },
        CallerNumber = "+50688881234",
    };

    [Fact]
    public void FieldsFollowTheFormOrderAndKeepUnknownOnesLast() =>
        Assert.Equal(["nombre", "plan", "viejo"], FormActionPayloads.Fields(Form, Submission).Select(f => f.Key));

    [Fact]
    public void WebhookIncludesKeysLabelsAndEvent()
    {
        var json = FormActionPayloads.Webhook(Form, Submission);

        Assert.Equal("form.submitted", json["event"]!.GetValue<string>());
        Assert.Equal("Pro", json["submission"]!["values"]!["plan"]!.GetValue<string>());
        Assert.Equal("Nombre", json["submission"]!["fields"]![0]!["label"]!.GetValue<string>());
    }

    [Fact]
    public void SignatureIsHmacSha256OfTheBody()
    {
        var expected = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes("s3cr3t"), Encoding.UTF8.GetBytes("{}")));

        Assert.Equal($"sha256={expected}", FormActionPayloads.Sign("s3cr3t", "{}"));
    }

    [Fact]
    public void TeamsSendsAnAdaptiveCardWithFacts()
    {
        var card = FormActionPayloads.Teams(Form, Submission)["attachments"]![0]!;

        Assert.Equal("application/vnd.microsoft.card.adaptive", card["contentType"]!.GetValue<string>());
        Assert.Equal("Plan", card["content"]!["body"]![1]!["facts"]![1]!["title"]!.GetValue<string>());
    }

    [Fact]
    public void ChatTextListsFieldsInBold()
    {
        var text = FormActionPayloads.ChatText(Form, Submission)["text"]!.GetValue<string>();

        Assert.StartsWith("*Nueva respuesta: Cotización*", text);
        Assert.Contains("• *Plan:* Pro", text);
    }

    [Fact]
    public void EmailEscapesHtml() => Assert.Contains("Ana &lt;b&gt;", FormActionPayloads.Email(Form, Submission).Html);
}
