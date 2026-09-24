using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Mapache.Sip.Poc;

public static class PocServiceCollectionExtensions
{
    /// <summary>Registra el worker del POC solo si <c>Sip:Poc:Enabled</c> es true.</summary>
    public static IServiceCollection AddSipPoc(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(PocOptions.SectionName);
        if (!section.GetValue<bool>(nameof(PocOptions.Enabled)))
        {
            return services;
        }

        services.AddOptions<PocOptions>()
            .Bind(section)
            .Validate(o => !string.IsNullOrWhiteSpace(o.Sip.Server) && !string.IsNullOrWhiteSpace(o.Sip.Username),
                "Faltan Sip:Poc:Sip:Server o Sip:Poc:Sip:Username")
            .Validate(o => !string.IsNullOrWhiteSpace(o.ElevenLabs.ApiKey) && !string.IsNullOrWhiteSpace(o.ElevenLabs.AgentId),
                "Faltan Sip:Poc:ElevenLabs:ApiKey o Sip:Poc:ElevenLabs:AgentId")
            .ValidateOnStart();

        services.AddHttpClient();
        services.AddHostedService<SipPocWorker>();
        return services;
    }
}
