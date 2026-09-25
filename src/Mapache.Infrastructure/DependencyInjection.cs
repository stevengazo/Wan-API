using Mapache.Domain.Entities;
using Mapache.Infrastructure.Actions;
using Mapache.Infrastructure.Llm;
using Mapache.Infrastructure.Persistence;
using Mapache.Infrastructure.Recordings;
using Mapache.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Mapache.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Mapache");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Falta ConnectionStrings:Mapache (user-secrets o variable de entorno)");
        }

        services.AddDbContext<MapacheDbContext>(options => options.UseNpgsql(connectionString));

        // Llaves en la base para que sobrevivan a redeploys del contenedor y todas las instancias compartan las mismas.
        services.AddDataProtection()
            .SetApplicationName("Mapache")
            .PersistKeysToDbContext<MapacheDbContext>();

        services.AddSingleton<ISecretProtector, SecretProtector>();
        services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();

        services.Configure<LlmOptions>(configuration.GetSection(LlmOptions.SectionName));
        services.AddHttpClient<OpenAiCompatibleChatClient>();
        services.AddSingleton<AnthropicChatClient>();
        services.AddScoped<ChatCompletionRouter>();

        services.Configure<RecordingOptions>(configuration.GetSection(RecordingOptions.SectionName));
        services.AddSingleton<RecordingStore>();

        // Un destino lento no debe trabar la cola: si no responde se reintenta más tarde.
        services.AddHttpClient<FormActionSender>(c => c.Timeout = TimeSpan.FromSeconds(15));
        services.AddScoped<FormActionRunner>();
        return services;
    }
}
