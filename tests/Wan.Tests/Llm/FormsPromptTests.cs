using Wan.Domain.Entities;
using Wan.Infrastructure.Forms;

namespace Wan.Tests.Llm;

public class FormsPromptTests
{
    private static readonly FormTemplate Form = new()
    {
        Id = Guid.NewGuid(),
        Name = "Solicitud de cotización",
        Description = "Cuando alguien quiere precios",
        Fields =
        [
            new() { Key = "nombre", Label = "Nombre", Required = true },
            new() { Key = "telefono", Label = "Teléfono", Type = FormFieldType.Phone, Required = true },
            new() { Key = "correo", Label = "Correo", Type = FormFieldType.Email },
            new() { Key = "empleados", Label = "Empleados", Type = FormFieldType.Number },
            new() { Key = "cliente_actual", Label = "¿Ya es cliente?", Type = FormFieldType.YesNo },
            new() { Key = "plan", Label = "Plan", Type = FormFieldType.Choice, Options = ["Básico", "Pro"] },
        ],
    };

    private static Dictionary<string, string?> Values(params (string Key, string? Value)[] values) =>
        values.ToDictionary(v => v.Key, v => v.Value);

    [Fact]
    public void NormalizesValuesByType()
    {
        var result = FormsPrompt.Validate(Form, Values(
            ("nombre", " Ana "), ("TELEFONO", "+506 8888-1234"), ("correo", "Ana@Ejemplo.com"),
            ("empleados", "12,5"), ("cliente_actual", "sí"), ("plan", "pro")));

        Assert.True(result.IsValid);
        Assert.Equal("Ana", result.Values["nombre"]);
        Assert.Equal("+50688881234", result.Values["telefono"]);
        Assert.Equal("ana@ejemplo.com", result.Values["correo"]);
        Assert.Equal("12.5", result.Values["empleados"]);
        Assert.Equal("Sí", result.Values["cliente_actual"]);
        Assert.Equal("Pro", result.Values["plan"]);
    }

    [Fact]
    public void ReportsMissingRequiredAndInvalidValues()
    {
        var result = FormsPrompt.Validate(Form, Values(("nombre", "Ana"), ("correo", "no-es-correo"), ("plan", "Premium")));

        Assert.False(result.IsValid);
        Assert.Contains(result.Problems, p => p.Contains("Falta \"Teléfono\""));
        Assert.Contains(result.Problems, p => p.Contains("\"Correo\""));
        Assert.Contains(result.Problems, p => p.Contains("Básico, Pro"));
    }

    [Fact]
    public void OptionalFieldsCanBeOmitted() =>
        Assert.True(FormsPrompt.Validate(Form, Values(("nombre", "Ana"), ("telefono", "88881234"))).IsValid);

    [Fact]
    public void FindsByNameIgnoringAccentsOrById()
    {
        Assert.Same(Form, FormsPrompt.Find([Form], "solicitud de cotizacion"));
        Assert.Same(Form, FormsPrompt.Find([Form], Form.Id.ToString()));
        Assert.Null(FormsPrompt.Find([Form], "otro"));
    }

    [Fact]
    public void BuildDescribesFields()
    {
        var prompt = FormsPrompt.Build([Form])!;

        Assert.Contains("Formulario \"Solicitud de cotización\": Cuando alguien quiere precios", prompt);
        Assert.Contains("- telefono (Teléfono, teléfono, obligatorio)", prompt);
        Assert.Contains("- plan (Plan, una de: Básico, Pro, opcional)", prompt);
    }
}
