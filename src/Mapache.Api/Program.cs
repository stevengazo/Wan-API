using Mapache.Api.Controllers;
using Mapache.Api.Phone;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Hangfire;
using Hangfire.PostgreSql;
using Mapache.Api.Auth;
using Mapache.Api.Jobs;
using Mapache.Api.Mcp;
using Mapache.Api.Realtime;
using Mapache.Api.Webhooks;
using Mapache.Infrastructure;
using Mapache.Infrastructure.Actions;
using Mapache.Infrastructure.Persistence;
using Mapache.Api.Sip;
using Mapache.Sip.Engine;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using Serilog.Formatting.Compact;

// Logger mínimo para no perder errores que ocurran antes de que el host configure Serilog.
Log.Logger = new LoggerConfiguration().WriteTo.Console().CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Services.AddSerilog((services, logger) =>
    {
        logger.ReadFrom.Configuration(builder.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", "Mapache.Api");

        // JSON compacto fuera de desarrollo para que lo indexe cualquier colector de logs.
        if (builder.Environment.IsDevelopment())
        {
            logger.WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}");
        }
        else
        {
            logger.WriteTo.Console(new RenderedCompactJsonFormatter());
        }
    });

    // Add services to the container.

    builder.Services.AddControllers()
        .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
    builder.Services.AddProblemDetails();
    builder.Services.AddOpenApi();
    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddSignalR()
        .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

    builder.Services.AddInfrastructure(builder.Configuration);
    builder.Services.AddSingleton<ISipAccountSource, SipAccountSource>();
    builder.Services.AddSingleton<Mapache.Infrastructure.Transfers.ICallTransfer, SipCallTransfer>();
    builder.Services.AddSingleton<IVoiceAgentSource, VoiceAgentSource>();
    builder.Services.AddSingleton<ISipEventSink, SipEventSink>();
    builder.Services.AddSipEngine(builder.Configuration);

    builder.Services.AddHangfire(config => config
        .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
        .UseSimpleAssemblyNameTypeSerializer()
        .UseRecommendedSerializerSettings()
        .UsePostgreSqlStorage(
            o => o.UseNpgsqlConnection(builder.Configuration.GetConnectionString("Mapache")),
            new PostgreSqlStorageOptions { SchemaName = "hangfire" }));
    builder.Services.AddHangfireServer();
    builder.Services.AddScoped<IFormActionQueue, HangfireFormActionQueue>();
    builder.Services.AddScoped<IFormActionNotifier, SignalRFormActionNotifier>();

    var jwt = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
    if (jwt.SigningKey.Length < 32)
    {
        throw new InvalidOperationException("Auth:Jwt:SigningKey debe tener al menos 32 caracteres (user-secrets o variable de entorno)");
    }

    builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));
    builder.Services.Configure<AgentWebhookOptions>(builder.Configuration.GetSection(AgentWebhookOptions.SectionName));
    builder.Services.Configure<AppOptions>(builder.Configuration.GetSection(AppOptions.SectionName));
    builder.Services.AddSingleton<TokenService>();
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(o =>
        {
            // Sin el mapeo de claims de WS-Fed: "sub" y "role" llegan tal cual los emite TokenService.
            o.MapInboundClaims = false;
            o.TokenValidationParameters = new TokenValidationParameters
            {
                ValidIssuer = jwt.Issuer,
                ValidAudience = jwt.Audience,
                IssuerSigningKey = TokenService.CreateKey(jwt),
                NameClaimType = "sub",
                RoleClaimType = "role",
                ClockSkew = TimeSpan.FromMinutes(1),
            };
            o.Events = new JwtBearerEvents
            {
                // WebSockets no permite headers en el navegador: el cliente de SignalR manda el token por query string.
                OnMessageReceived = context =>
                {
                    var token = context.Request.Query["access_token"];
                    if (!string.IsNullOrEmpty(token) && (context.HttpContext.Request.Path.StartsWithSegments(EventsHub.Path) || context.HttpContext.Request.Path.StartsWithSegments(WebPhoneEndpoint.Path)))
                    {
                        context.Token = token;
                    }

                    return Task.CompletedTask;
                },
            };
        })
        .AddScheme<AuthenticationSchemeOptions, AgentToolAuthenticationHandler>(AgentToolAuthenticationHandler.SchemeName, null)
        .AddScheme<AuthenticationSchemeOptions, McpTokenAuthenticationHandler>(McpTokenAuthenticationHandler.SchemeName, null);

    builder.Services.AddAuthorizationBuilder()
        .AddPolicy(AgentToolAuthenticationHandler.PolicyName, policy => policy
            .AddAuthenticationSchemes(AgentToolAuthenticationHandler.SchemeName)
            .RequireAuthenticatedUser())
        .AddPolicy(McpTokenAuthenticationHandler.PolicyName, policy => policy
            .AddAuthenticationSchemes(McpTokenAuthenticationHandler.SchemeName)
            .RequireAuthenticatedUser());

    // Servidor MCP para agentes externos. Sin estado: cada petición se resuelve sola, así funciona detrás de
    // nginx y con varias instancias de la API.
    builder.Services.AddMcpServer()
        .WithHttpTransport(o => o.Stateless = true)
        .WithTools<MapacheMcpTools>()
        .WithTokenPermissions();
    builder.Services.AddHttpContextAccessor();

    // Detrás de nginx la IP remota es la del proxy: sin esto el rate limit del login sería uno solo para todos.
    // Solo se habilita con ReverseProxy:Enabled porque confiar en X-Forwarded-For sin proxy permite falsear la IP.
    var behindProxy = builder.Configuration.GetValue<bool>("ReverseProxy:Enabled");
    if (behindProxy)
    {
        builder.Services.Configure<ForwardedHeadersOptions>(o =>
        {
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            // La API no publica puertos en docker-compose: el único que puede llegar es nginx.
            o.KnownIPNetworks.Clear();
            o.KnownProxies.Clear();
        });
    }

    builder.Services.AddRateLimiter(o =>
    {
        o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        o.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
    });

    var app = builder.Build();

    await DbInitializer.InitializeAsync(app.Services);

    // Configure the HTTP request pipeline.
    if (behindProxy)
    {
        app.UseForwardedHeaders();
    }

    app.UseExceptionHandler();
    app.UseSerilogRequestLogging();
    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();
        // Por defecto el dashboard solo acepta requests locales; no se expone fuera de desarrollo.
        app.UseHangfireDashboard("/hangfire");
    }

    app.UseWebSockets();
    app.UseAuthentication();
    app.UseRateLimiter();
    app.UseAuthorization();

    app.MapControllers();
    app.MapHub<EventsHub>(EventsHub.Path);
    app.MapWebPhone();
    app.MapMcp("/api/mcp").RequireAuthorization(McpTokenAuthenticationHandler.PolicyName);
    app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();

    app.Run();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "La API terminó por un error no controlado");
    throw;
}
finally
{
    Log.CloseAndFlush();
}
