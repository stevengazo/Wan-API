using Wan.Domain.Entities;
using Wan.Infrastructure.Actions;
using Wan.Infrastructure.Forms;
using Wan.Infrastructure.Knowledge;
using Wan.Infrastructure.Llm;
using Wan.Infrastructure.Persistence;
using Wan.Infrastructure.Recordings;
using Wan.Infrastructure.Security;
using Wan.Infrastructure.Tools;
using Wan.Infrastructure.Transfers;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Wan.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Wan");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Falta ConnectionStrings:Wan (user-secrets o variable de entorno)");
        }

        services.AddDbContext<WanDbContext>(options => options.UseNpgsql(connectionString));

        // Llaves en la base para que sobrevivan a redeploys del contenedor y todas las instancias compartan las mismas.
        // El nombre de aplicación sigue siendo el original: cambiarlo invalida las llaves y los secretos ya cifrados.
        var dataProtection = services.AddDataProtection()
            .SetApplicationName("Mapache")
            .PersistKeysToDbContext<WanDbContext>();
        KeyEncryption.Configure(dataProtection, configuration);

        services.AddSingleton<ISecretProtector, SecretProtector>();
        services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();

        services.Configure<LlmOptions>(configuration.GetSection(LlmOptions.SectionName));
        services.AddHttpClient<OpenAiCompatibleChatClient>();
        services.AddSingleton<AnthropicChatClient>();
        services.AddScoped<LlmResolver>();
        services.AddScoped<ChatCompletionRouter>();
        services.AddHttpClient<LlmJsonClient>(c => c.Timeout = TimeSpan.FromSeconds(90));
        services.AddScoped<FormGenerator>();
        services.AddScoped<KnowledgeService>();

        // Herramientas que ejecuta Wan dentro del Custom LLM; el orden define cuál gana si se repite un nombre.
        services.AddHttpClient<HttpToolRunner>();
        services.AddScoped<IBotToolSource, KnowledgeToolSource>();
        services.AddScoped<IBotToolSource, HttpToolSource>();
        services.AddSingleton<McpConnector>();
        services.AddScoped<IBotToolSource, McpToolSource>();
        services.AddScoped<IBotToolSource, TransferToolSource>();
        services.AddScoped<IBotToolSource, Wan.Infrastructure.Campaigns.CampaignToolSource>();
        services.AddScoped<BotToolRegistry>();
        services.AddScoped<BotSessionBuilder>();

        services.Configure<RecordingOptions>(configuration.GetSection(RecordingOptions.SectionName));
        services.AddSingleton<RecordingStore>();
        services.AddScoped<RecordingService>();

        // Un destino lento no debe trabar la cola: si no responde se reintenta más tarde.
        services.AddHttpClient<FormActionSender>(c => c.Timeout = TimeSpan.FromSeconds(15));
        services.AddScoped<FormActionRunner>();
        return services;
    }
}
