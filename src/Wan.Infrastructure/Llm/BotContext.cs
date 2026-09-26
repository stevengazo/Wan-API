using System.Text.Json.Nodes;
using Mapache.Domain.Entities;
using Mapache.Infrastructure.Forms;
using Mapache.Infrastructure.Transfers;

namespace Mapache.Infrastructure.Llm;

/// <summary>
/// Contexto que Mapache agrega a las instrucciones del bot en cada respuesta: directorio, formularios,
/// recados y aviso de grabación.
/// </summary>
public static class BotContext
{
    public const string SubmitFormTool = "submit_form";
    public const string LeaveMessageTool = "leave_message";

    /// <summary>
    /// Formularios y recados solo se mencionan si el agente tiene la tool correspondiente: pedirle que use
    /// una herramienta que no existe lo confundiría.
    /// </summary>
    public static string? Build(
        JsonObject request,
        IReadOnlyCollection<DirectoryEntry> directory,
        IReadOnlyCollection<FormTemplate> forms,
        CallSettings? callSettings,
        BusinessHoursState hours = BusinessHoursState.Open,
        string? hoursMessage = null) =>
        Build(ToolNames(request), directory, forms, callSettings, hours, hoursMessage);

    /// <param name="tools">Nombres de todas las herramientas disponibles: las del agente y las de Mapache.</param>
    public static string? Build(
        IReadOnlySet<string> tools,
        IReadOnlyCollection<DirectoryEntry> directory,
        IReadOnlyCollection<FormTemplate> forms,
        CallSettings? callSettings,
        BusinessHoursState hours = BusinessHoursState.Open,
        string? hoursMessage = null)
    {
        var parts = new List<string?>();

        if (hours != BusinessHoursState.Open)
        {
            // Primero: es lo que más cambia qué puede hacer el bot en esta llamada.
            var when = hours == BusinessHoursState.OnBreak ? "es horario de descanso" : "estamos fuera del horario de atención";
            var say = string.IsNullOrWhiteSpace(hoursMessage) ? "" : $" Infórmalo con estas palabras: \"{hoursMessage.Trim()}\".";
            var offer = tools.Contains(LeaveMessageTool) ? " Ofrece tomar un recado para que le devuelvan la llamada." : "";
            parts.Add($"Ahora {when}. No transfieras llamadas ni ofrezcas hacerlo.{say}{offer}");
        }

        if (callSettings is { RecordCalls: true } && !string.IsNullOrWhiteSpace(callSettings.RecordingNotice))
        {
            parts.Add($"La llamada se graba. Si todavía no lo dijiste, al comienzo informa con estas palabras: \"{callSettings.RecordingNotice.Trim()}\"");
        }

        parts.Add(DirectoryPrompt.Build(directory, transfersAllowed: hours == BusinessHoursState.Open));

        if (tools.Contains(LeaveMessageTool))
        {
            parts.Add("Recados. Si la persona buscada no puede atender o quien llama prefiere dejar un mensaje, toma su nombre, " +
                "un número para devolver la llamada y el mensaje, confírmalos y guárdalo con la herramienta leave_message. " +
                "Marca urgent solo si quien llama lo pide o es claramente urgente.");
        }

        if (tools.Contains(SubmitFormTool))
        {
            parts.Add(FormsPrompt.Build(forms));
        }

        var text = string.Join("\n\n", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        return text.Length == 0 ? null : text;
    }

    public static HashSet<string> ToolNames(JsonObject request) =>
        (request["tools"] as JsonArray ?? [])
            .Select(t => t?["function"]?["name"]?.GetValue<string>())
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
}
