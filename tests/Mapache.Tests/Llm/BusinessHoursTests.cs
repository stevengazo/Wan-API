using System.Text.Json.Nodes;
using Mapache.Domain.Entities;
using Mapache.Infrastructure.Llm;
using Mapache.Infrastructure.Transfers;

namespace Mapache.Tests.Llm;

public class BusinessHoursTests
{
    // Costa Rica es UTC-6 todo el año: las 14:00 UTC son las 08:00 locales.
    private static readonly BusinessHours Hours = new()
    {
        Enabled = true,
        TimeZone = "America/Costa_Rica",
        Days =
        [
            new() { Day = DayOfWeek.Monday, Opens = new(8, 0), Closes = new(17, 0), BreakStarts = new(12, 0), BreakEnds = new(13, 0) },
            new() { Day = DayOfWeek.Saturday, Opens = new(9, 0), Closes = new(12, 0) },
        ],
        ClosedMessage = "Atendemos de lunes a viernes de 8 a 17 h.",
    };

    private static DateTimeOffset Local(int day, int hour, int minute = 0) =>
        new DateTimeOffset(2026, 9, day, hour, minute, 0, TimeSpan.FromHours(-6));

    [Theory]
    [InlineData(28, 8, 0, BusinessHoursState.Open)]      // lunes al abrir
    [InlineData(28, 7, 59, BusinessHoursState.Closed)]   // lunes antes de abrir
    [InlineData(28, 12, 30, BusinessHoursState.OnBreak)] // lunes en el almuerzo
    [InlineData(28, 13, 0, BusinessHoursState.Open)]     // lunes al volver
    [InlineData(28, 17, 0, BusinessHoursState.Closed)]   // lunes al cerrar
    [InlineData(29, 10, 0, BusinessHoursState.Closed)]   // martes: no está en la lista
    [InlineData(26, 10, 0, BusinessHoursState.Open)]     // sábado sin descanso
    public void StateFollowsTheLocalTimeOfTheOffice(int day, int hour, int minute, BusinessHoursState expected) =>
        Assert.Equal(expected, Hours.StateAt(Local(day, hour, minute)));

    [Fact]
    public void ConvertsFromUtcToTheConfiguredZone() =>
        // 18:30 UTC del lunes = 12:30 en Costa Rica: descanso.
        Assert.Equal(BusinessHoursState.OnBreak, Hours.StateAt(new DateTimeOffset(2026, 9, 28, 18, 30, 0, TimeSpan.Zero)));

    [Fact]
    public void DisabledIsAlwaysOpen() =>
        Assert.Equal(BusinessHoursState.Open, new BusinessHours { Enabled = false }.StateAt(Local(29, 3)));

    [Fact]
    public void BreakWithoutItsOwnMessageUsesTheClosedOne() =>
        Assert.Equal(Hours.ClosedMessage, Hours.MessageFor(BusinessHoursState.OnBreak));

    [Fact]
    public void ClosedContextForbidsTransfersAndOffersAMessage()
    {
        var request = new JsonObject
        {
            ["tools"] = new JsonArray(new JsonObject { ["type"] = "function", ["function"] = new JsonObject { ["name"] = BotContext.LeaveMessageTool } }),
        };
        var directory = new[] { new DirectoryEntry { Name = "Ventas", Target = "101" } };

        var context = BotContext.Build(request, directory, [], null, BusinessHoursState.Closed, Hours.ClosedMessage)!;

        Assert.Contains("No transfieras", context);
        Assert.Contains(Hours.ClosedMessage!, context);
        Assert.Contains("recado", context);
        Assert.DoesNotContain("ofrécele transferirlo", context);
    }

    [Fact]
    public void TransferOnlyResolvesDirectoryTargets()
    {
        var directory = new[]
        {
            new DirectoryEntry { Name = "Laura Mora", Department = "Ventas", Target = "101" },
            new DirectoryEntry { Name = "Soporte técnico", Target = "sip:soporte@pbx" },
        };

        Assert.Equal("101", CallTransferTool.Resolve(directory, "101")?.Target);
        Assert.Equal("sip:soporte@pbx", CallTransferTool.Resolve(directory, "soporte")?.Target);
        Assert.Null(CallTransferTool.Resolve(directory, "+19005551234"));
        Assert.Null(CallTransferTool.Resolve(directory, ""));
    }
}
