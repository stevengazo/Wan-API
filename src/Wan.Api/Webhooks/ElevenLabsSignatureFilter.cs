using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

namespace Mapache.Api.Webhooks;

/// <summary>Rechaza con 401 las requests sin firma válida de ElevenLabs.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequireElevenLabsSignatureAttribute() : TypeFilterAttribute(typeof(ElevenLabsSignatureFilter));

public sealed class ElevenLabsSignatureFilter(
    IOptions<AgentWebhookOptions> options,
    TimeProvider time,
    ILogger<ElevenLabsSignatureFilter> logger) : IAsyncAuthorizationFilter
{
    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var request = context.HttpContext.Request;

        // La firma se calcula sobre el body crudo: se lee y se rebobina para que el model binding lo vuelva a leer.
        request.EnableBuffering();
        using var reader = new StreamReader(request.Body, Encoding.UTF8, leaveOpen: true);
        var body = await reader.ReadToEndAsync(context.HttpContext.RequestAborted);
        request.Body.Position = 0;

        var header = request.Headers[ElevenLabsSignature.HeaderName].ToString();
        if (!ElevenLabsSignature.IsValid(header, body, options.Value.PostCallSecret, time.GetUtcNow()))
        {
            logger.LogWarning("Webhook de ElevenLabs con firma inválida o vencida desde {Ip}", context.HttpContext.Connection.RemoteIpAddress);
            context.Result = new UnauthorizedResult();
        }
    }
}
