using System.Text.Json;
using Mapache.Domain.Entities;
using Mapache.Infrastructure.Forms;

namespace Mapache.Tests.Llm;

public class FormGeneratorTests
{
    private static FormDraft Sanitize(string json) => FormGenerator.Sanitize(JsonDocument.Parse(json).RootElement);

    [Fact]
    public void KeepsAValidDraftAsIs()
    {
        var draft = Sanitize("""
            {"name":"Visita técnica","description":"Cuando piden un técnico","fields":[
              {"key":"direccion","label":"Dirección","type":"Text","required":true,"hint":"Con señas","options":[]},
              {"key":"horario","label":"Horario","type":"Choice","required":false,"hint":"","options":["Mañana","Tarde"]}]}
            """);

        Assert.Equal("Visita técnica", draft.Name);
        Assert.Equal(["direccion", "horario"], draft.Fields.Select(f => f.Key));
        Assert.Equal(FormFieldType.Choice, draft.Fields[1].Type);
        Assert.Equal(["Mañana", "Tarde"], draft.Fields[1].Options);
        Assert.Null(draft.Fields[1].Hint);
    }

    [Fact]
    public void FixesKeysTypesAndOptions()
    {
        var draft = Sanitize("""
            {"name":"X","description":"","fields":[
              {"key":"Nombre del Cliente","label":"Nombre","type":"Text","required":true,"hint":"","options":["no va"]},
              {"key":"nombre_del_cliente","label":"Otro nombre","type":"Text","required":false,"hint":"","options":[]},
              {"key":"plan","label":"Plan","type":"Choice","required":false,"hint":"","options":[]},
              {"key":"raro","label":"Raro","type":"Firma","required":false,"hint":"","options":[]},
              {"key":"","label":"","type":"Text","required":false,"hint":"","options":[]}]}
            """);

        Assert.Equal(["nombre_del_cliente", "nombre_del_cliente_2", "plan", "raro"], draft.Fields.Select(f => f.Key));
        Assert.Empty(draft.Fields[0].Options);
        Assert.Equal(FormFieldType.Text, draft.Fields[2].Type);
        Assert.Equal(FormFieldType.Text, draft.Fields[3].Type);
        Assert.Null(draft.Description);
    }

    [Fact]
    public void LimitsTheNumberOfFields()
    {
        var fields = string.Join(',', Enumerable.Range(1, 40).Select(i =>
            $$"""{"key":"c{{i}}","label":"Campo {{i}}","type":"Text","required":false,"hint":"","options":[]}"""));

        Assert.Equal(FormGenerator.MaxFields, Sanitize($$"""{"name":"X","description":"","fields":[{{fields}}]}""").Fields.Count);
    }

    [Theory]
    [InlineData("Teléfono de contacto", "telefono_de_contacto")]
    [InlineData("¿Ya es cliente?", "ya_es_cliente")]
    [InlineData("!!!", "campo")]
    public void ToKeyMatchesThePanelEditor(string label, string key) => Assert.Equal(key, FormGenerator.ToKey(label));
}
