using System.Text.Json.Nodes;
using Wan.Domain.Entities;
using Wan.Infrastructure.Llm;

namespace Wan.Tests.Llm;

public class BotContextTests
{
    private static readonly FormTemplate[] Forms = [new() { Name = "Cotización", Fields = [new() { Key = "nombre", Label = "Nombre" }] }];

    private static JsonObject Request(params string[] tools) => new()
    {
        ["tools"] = new JsonArray(tools.Select(t => (JsonNode)new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject { ["name"] = t },
        }).ToArray()),
    };

    [Fact]
    public void FormsAndMessagesNeedTheirTools()
    {
        var context = BotContext.Build(Request(), [], Forms, null);

        Assert.Null(context);
    }

    [Fact]
    public void IncludesFormsAndMessagesWhenTheAgentHasTheTools()
    {
        var context = BotContext.Build(Request(BotContext.SubmitFormTool, BotContext.LeaveMessageTool), [], Forms, null)!;

        Assert.Contains("Formulario \"Cotización\"", context);
        Assert.Contains("leave_message", context);
    }

    [Fact]
    public void RecordingNoticeOnlyWhenRecording()
    {
        var notice = "Esta llamada puede ser grabada.";

        Assert.Null(BotContext.Build(Request(), [], [], new CallSettings { RecordCalls = false, RecordingNotice = notice }));
        Assert.Contains(notice, BotContext.Build(Request(), [], [], new CallSettings { RecordCalls = true, RecordingNotice = notice }));
    }
}
