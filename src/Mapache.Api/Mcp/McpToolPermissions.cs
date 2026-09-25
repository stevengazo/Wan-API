using System.ComponentModel;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Mapache.Api.Mcp;

/// <summary>Herramienta del servidor MCP de Mapache, para elegir permisos en el panel.</summary>
public sealed record McpToolDescriptor(string Name, string Description, bool ReadOnly);

/// <summary>
/// Aplica los permisos de cada token: un cliente MCP solo ve las herramientas que puede usar y, si llama a
/// otra, recibe un error.
/// </summary>
public static class McpToolPermissions
{
    /// <summary>Las herramientas se leen de <see cref="MapacheMcpTools"/>: agregar una la suma sola a la lista.</summary>
    public static readonly IReadOnlyList<McpToolDescriptor> All = typeof(MapacheMcpTools)
        .GetMethods(BindingFlags.Public | BindingFlags.Instance)
        .Select(m => (Method: m, Tool: m.GetCustomAttribute<McpServerToolAttribute>()))
        .Where(x => x.Tool?.Name is not null)
        .Select(x => new McpToolDescriptor(
            x.Tool!.Name!,
            x.Method.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "",
            x.Tool.ReadOnly))
        .ToList();

    public static IMcpServerBuilder WithTokenPermissions(this IMcpServerBuilder builder) =>
        builder.WithRequestFilters(filters =>
        {
            filters.AddListToolsFilter(next => async (context, ct) =>
            {
                var result = await next(context, ct);
                var user = context.Services?.GetRequiredService<IHttpContextAccessor>().HttpContext?.User;
                result.Tools = result.Tools.Where(t => McpTokenAuthenticationHandler.CanUse(user, t.Name)).ToList();
                return result;
            });

            filters.AddCallToolFilter(next => async (context, ct) =>
            {
                var user = context.Services?.GetRequiredService<IHttpContextAccessor>().HttpContext?.User;
                var tool = context.Params?.Name ?? "";
                return McpTokenAuthenticationHandler.CanUse(user, tool)
                    ? await next(context, ct)
                    : new CallToolResult
                    {
                        IsError = true,
                        Content = [new TextContentBlock { Text = $"Este token no tiene permiso para usar {tool}." }],
                    };
            });
        });
}
