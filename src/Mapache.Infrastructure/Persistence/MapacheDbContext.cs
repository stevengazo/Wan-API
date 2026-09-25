using System.Text.Json;
using System.Text.Json.Serialization;
using Mapache.Domain.Entities;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using NpgsqlTypes;

namespace Mapache.Infrastructure.Persistence;

public class MapacheDbContext(DbContextOptions<MapacheDbContext> options) : DbContext(options), IDataProtectionKeyContext
{
    public DbSet<User> Users => Set<User>();

    public DbSet<Extension> Extensions => Set<Extension>();

    public DbSet<Call> Calls => Set<Call>();

    public DbSet<LlmSettings> LlmSettings => Set<LlmSettings>();

    public DbSet<DirectoryEntry> DirectoryEntries => Set<DirectoryEntry>();

    public DbSet<FormTemplate> FormTemplates => Set<FormTemplate>();

    public DbSet<FormSubmission> FormSubmissions => Set<FormSubmission>();

    public DbSet<FormAction> FormActions => Set<FormAction>();

    public DbSet<ActionExecution> ActionExecutions => Set<ActionExecution>();

    public DbSet<SmtpSettings> SmtpSettings => Set<SmtpSettings>();

    public DbSet<CallMessage> CallMessages => Set<CallMessage>();

    public DbSet<HttpTool> HttpTools => Set<HttpTool>();

    public DbSet<McpServerConnection> McpServers => Set<McpServerConnection>();

    public DbSet<McpAccessToken> McpAccessTokens => Set<McpAccessToken>();

    public DbSet<KnowledgeBase> KnowledgeBases => Set<KnowledgeBase>();

    public DbSet<KnowledgeDocument> KnowledgeDocuments => Set<KnowledgeDocument>();

    public DbSet<KnowledgeChunk> KnowledgeChunks => Set<KnowledgeChunk>();

    public DbSet<Recording> Recordings => Set<Recording>();

    public DbSet<CallSettings> CallSettings => Set<CallSettings>();

    public DbSet<SiteSettings> SiteSettings => Set<SiteSettings>();

    public DbSet<StorageSettings> StorageSettings => Set<StorageSettings>();

    public DbSet<Campaign> Campaigns => Set<Campaign>();

    public DbSet<CampaignContact> CampaignContacts => Set<CampaignContact>();

    public DbSet<VoiceSettings> VoiceSettings => Set<VoiceSettings>();

    public DbSet<LlmProviderSettings> LlmProviderSettings => Set<LlmProviderSettings>();

    public DbSet<CallTranscript> CallTranscripts => Set<CallTranscript>();

    /// <summary>Llaves de Data Protection: si se pierden, las contraseñas SIP cifradas quedan ilegibles.</summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // pgvector se habilita ya para la base de conocimiento (Fase 5).
        modelBuilder.HasPostgresExtension("vector");

        modelBuilder.Entity<User>(e =>
        {
            e.Property(u => u.Email).HasMaxLength(256);
            e.HasIndex(u => u.Email).IsUnique();
            e.Property(u => u.DisplayName).HasMaxLength(128);
            e.Property(u => u.Role).HasConversion<string>().HasMaxLength(32);
        });

        modelBuilder.Entity<Extension>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(128);
            e.Property(x => x.SipServer).HasMaxLength(256);
            e.Property(x => x.SipProxy).HasMaxLength(256);
            e.Property(x => x.SecondarySipServer).HasMaxLength(256);
            e.Property(x => x.MediaEncryption).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.DtmfMode).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.VoicemailNumber).HasMaxLength(64);
            e.Property(x => x.DialPrefix).HasMaxLength(32);
            e.Property(x => x.SipUsername).HasMaxLength(128);
            e.Property(x => x.SipDomain).HasMaxLength(256);
            e.Property(x => x.AuthUsername).HasMaxLength(128);
            e.Property(x => x.DisplayName).HasMaxLength(128);
            e.Property(x => x.Transport).HasConversion<string>().HasMaxLength(8);
            e.Property(x => x.PublicAddress).HasMaxLength(256);
            e.Property(x => x.StunServer).HasMaxLength(256);
            e.Property(x => x.AnswerMode).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.BusinessHours).HasColumnType("jsonb").HasConversion(Json<BusinessHours>());
            e.HasIndex(x => new { x.SipServer, x.SipUsername }).IsUnique();
        });

        modelBuilder.Entity<DirectoryEntry>(e =>
        {
            e.Property(d => d.Name).HasMaxLength(128);
            e.Property(d => d.Department).HasMaxLength(128);
            e.Property(d => d.Target).HasMaxLength(256);
            e.Property(d => d.Description).HasMaxLength(1000);
        });

        modelBuilder.Entity<FormTemplate>(e =>
        {
            e.Property(f => f.Name).HasMaxLength(128);
            e.Property(f => f.Description).HasMaxLength(1000);
            e.Property(f => f.Fields).HasColumnType("jsonb").HasConversion(Json<List<FormField>>());
        });

        modelBuilder.Entity<FormSubmission>(e =>
        {
            e.Property(s => s.Values).HasColumnType("jsonb").HasConversion(Json<Dictionary<string, string>>());
            e.Property(s => s.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(s => s.ConversationId).HasMaxLength(128);
            e.Property(s => s.CallerNumber).HasMaxLength(64);
            e.HasIndex(s => s.CreatedAt);
            e.HasOne(s => s.FormTemplate).WithMany().OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<FormAction>(e =>
        {
            e.Property(a => a.Type).HasConversion<string>().HasMaxLength(16);
            e.Property(a => a.Name).HasMaxLength(128);
            e.Property(a => a.Target).HasMaxLength(2000);
            e.Property(a => a.Method).HasMaxLength(8);
            e.Property(a => a.BodyTemplate).HasMaxLength(8000);
            e.HasOne(a => a.FormTemplate).WithMany().OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ActionExecution>(e =>
        {
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.LastError).HasMaxLength(1000);
            e.HasIndex(x => x.FormSubmissionId);
            e.HasOne(x => x.FormAction).WithMany().OnDelete(DeleteBehavior.Cascade);
            e.HasOne<FormSubmission>().WithMany().HasForeignKey(x => x.FormSubmissionId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SmtpSettings>(e =>
        {
            e.Property(s => s.Id).ValueGeneratedNever();
            e.Property(s => s.Host).HasMaxLength(256);
            e.Property(s => s.Username).HasMaxLength(256);
            e.Property(s => s.From).HasMaxLength(256);
        });

        modelBuilder.Entity<HttpTool>(e =>
        {
            e.Property(t => t.Name).HasMaxLength(64);
            e.HasIndex(t => t.Name).IsUnique();
            e.Property(t => t.Description).HasMaxLength(1000);
            e.Property(t => t.Method).HasMaxLength(8);
            e.Property(t => t.UrlTemplate).HasMaxLength(2000);
            e.Property(t => t.Parameters).HasColumnType("jsonb").HasConversion(Json<List<HttpToolParameter>>());
        });

        modelBuilder.Entity<McpServerConnection>(e =>
        {
            e.ToTable("McpServers");
            e.Property(s => s.Name).HasMaxLength(64);
            e.Property(s => s.Url).HasMaxLength(2000);
            e.Property(s => s.LastError).HasMaxLength(1000);
            e.Property(s => s.Tools).HasColumnType("jsonb").HasConversion(Json<List<McpToolInfo>>());
        });

        modelBuilder.Entity<McpAccessToken>(e =>
        {
            e.Property(t => t.Name).HasMaxLength(128);
            e.Property(t => t.TokenHash).HasMaxLength(64);
            e.HasIndex(t => t.TokenHash).IsUnique();
            e.Property(t => t.Prefix).HasMaxLength(32);
            e.Property(t => t.AllowedTools).HasColumnType("jsonb").HasConversion(Json<List<string>>());
        });

        modelBuilder.Entity<KnowledgeBase>(e =>
        {
            e.Property(b => b.Name).HasMaxLength(128);
            e.Property(b => b.Description).HasMaxLength(1000);
        });

        modelBuilder.Entity<KnowledgeDocument>(e =>
        {
            e.Property(d => d.FileName).HasMaxLength(256);
            e.Property(d => d.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(d => d.Error).HasMaxLength(1000);
            e.HasOne<KnowledgeBase>().WithMany().HasForeignKey(d => d.KnowledgeBaseId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<KnowledgeChunk>(e =>
        {
            e.HasOne(c => c.Document).WithMany().HasForeignKey(c => c.DocumentId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(c => c.KnowledgeBaseId);
            // Vector de búsqueda que calcula Postgres sobre el texto sin tildes; no existe en la entidad.
            e.Property<NpgsqlTsVector>("SearchVector")
                .HasComputedColumnSql("to_tsvector('spanish', \"SearchText\")", stored: true);
            e.HasIndex("SearchVector").HasMethod("GIN");
        });

        modelBuilder.Entity<CallMessage>(e =>
        {
            e.Property(m => m.Recipient).HasMaxLength(128);
            e.Property(m => m.CallerName).HasMaxLength(128);
            e.Property(m => m.CallbackNumber).HasMaxLength(64);
            e.Property(m => m.Body).HasMaxLength(4000);
            e.Property(m => m.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(m => m.ConversationId).HasMaxLength(128);
            e.HasIndex(m => m.CreatedAt);
            // Borrar una entrada del directorio no borra sus recados: se quedan con el nombre en texto.
            e.HasOne(m => m.DirectoryEntry).WithMany().OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Recording>(e =>
        {
            e.Property(r => r.ConversationId).HasMaxLength(128);
            e.HasIndex(r => r.ConversationId).IsUnique();
            e.Property(r => r.FileName).HasMaxLength(256);
            e.Property(r => r.Storage).HasConversion<string>().HasMaxLength(16);
            e.Property(r => r.ContentType).HasMaxLength(64);
            e.HasIndex(r => r.CreatedAt);
        });

        modelBuilder.Entity<StorageSettings>(e =>
        {
            e.Property(s => s.Id).ValueGeneratedNever();
            e.Property(s => s.Provider).HasConversion<string>().HasMaxLength(16);
            e.Property(s => s.S3Bucket).HasMaxLength(128);
            e.Property(s => s.S3Region).HasMaxLength(64);
            e.Property(s => s.S3ServiceUrl).HasMaxLength(256);
            e.Property(s => s.S3AccessKeyId).HasMaxLength(128);
            e.Property(s => s.AzureContainer).HasMaxLength(128);
            e.Property(s => s.Prefix).HasMaxLength(256);
        });

        modelBuilder.Entity<VoiceSettings>(e =>
        {
            e.Property(s => s.Id).ValueGeneratedNever();
            e.Property(s => s.AgentId).HasMaxLength(128);
            e.Property(s => s.Provider).HasConversion<string>().HasMaxLength(32);
            e.Property(s => s.OpenAiModel).HasMaxLength(64);
            e.Property(s => s.OpenAiVoice).HasMaxLength(32);
            e.Property(s => s.OpenAiTranscriptionModel).HasMaxLength(64);
            e.Property(s => s.DeepgramLanguage).HasMaxLength(16);
            e.Property(s => s.DeepgramListenModel).HasMaxLength(64);
            e.Property(s => s.DeepgramSpeakModel).HasMaxLength(64);
            e.Property(s => s.Greeting).HasMaxLength(500);
        });

        modelBuilder.Entity<CallSettings>(e =>
        {
            e.Property(s => s.Id).ValueGeneratedNever();
            e.Property(s => s.RecordingNotice).HasMaxLength(1000);
        });

        modelBuilder.Entity<Campaign>(e =>
        {
            e.Property(c => c.Name).HasMaxLength(128);
            e.Property(c => c.Instructions).HasMaxLength(8000);
            e.Property(c => c.OpeningLine).HasMaxLength(500);
            e.Property(c => c.Outcomes).HasColumnType("jsonb").HasConversion(Json<List<string>>());
            e.Property(c => c.Status).HasConversion<string>().HasMaxLength(16);
            e.HasOne(c => c.Extension).WithMany().OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CampaignContact>(e =>
        {
            e.Property(c => c.PhoneNumber).HasMaxLength(64);
            e.Property(c => c.Name).HasMaxLength(128);
            e.Property(c => c.Variables).HasColumnType("jsonb").HasConversion(Json<Dictionary<string, string>>());
            e.Property(c => c.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(c => c.LastResult).HasMaxLength(256);
            e.Property(c => c.Outcome).HasMaxLength(64);
            e.Property(c => c.Notes).HasMaxLength(2000);
            e.HasOne(c => c.Campaign).WithMany().OnDelete(DeleteBehavior.Cascade);
            // El marcador busca los pendientes de cada campaña por fecha de reintento.
            e.HasIndex(c => new { c.CampaignId, c.Status, c.NextAttemptAt });
        });

        modelBuilder.Entity<SiteSettings>(e =>
        {
            e.Property(s => s.Id).ValueGeneratedNever();
            e.Property(s => s.PublicUrl).HasMaxLength(512);
        });

        modelBuilder.Entity<LlmSettings>(e =>
        {
            e.Property(s => s.Id).ValueGeneratedNever();
            e.Property(s => s.ActiveProvider).HasConversion<string>().HasMaxLength(32);
        });

        modelBuilder.Entity<LlmProviderSettings>(e =>
        {
            e.HasKey(p => p.Provider);
            e.Property(p => p.Provider).HasConversion<string>().HasMaxLength(32);
            e.Property(p => p.Model).HasMaxLength(128);
        });

        modelBuilder.Entity<Call>(e =>
        {
            e.Property(c => c.CallerNumber).HasMaxLength(64);
            e.Property(c => c.Status).HasConversion<string>().HasMaxLength(32);
            e.Property(c => c.Direction).HasConversion<string>().HasMaxLength(16);
            e.Property(c => c.EndReason).HasMaxLength(256);
            e.Property(c => c.ElevenLabsConversationId).HasMaxLength(128);
            e.HasIndex(c => c.StartedAt);
            e.HasOne(c => c.Extension).WithMany().OnDelete(DeleteBehavior.Restrict);
            e.HasMany(c => c.Transcript).WithOne().HasForeignKey(t => t.CallId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CallTranscript>(e =>
        {
            e.Property(t => t.Speaker).HasConversion<string>().HasMaxLength(16);
        });
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Columna jsonb para un objeto que se serializa entero. EF compara por referencia: al editar hay que
    /// asignar una lista o diccionario nuevos, no modificar el existente.
    /// </summary>
    private static Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<T, string> Json<T>() where T : new() =>
        new(v => JsonSerializer.Serialize(v, JsonOptions), v => JsonSerializer.Deserialize<T>(v, JsonOptions) ?? new T());
}
