using Mapache.Domain.Entities;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Mapache.Infrastructure.Persistence;

public class MapacheDbContext(DbContextOptions<MapacheDbContext> options) : DbContext(options), IDataProtectionKeyContext
{
    public DbSet<User> Users => Set<User>();

    public DbSet<Extension> Extensions => Set<Extension>();

    public DbSet<Call> Calls => Set<Call>();

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
            e.Property(x => x.SipUsername).HasMaxLength(128);
            e.Property(x => x.SipDomain).HasMaxLength(256);
            e.Property(x => x.AuthUsername).HasMaxLength(128);
            e.Property(x => x.DisplayName).HasMaxLength(128);
            e.Property(x => x.Transport).HasConversion<string>().HasMaxLength(8);
            e.Property(x => x.PublicAddress).HasMaxLength(256);
            e.Property(x => x.StunServer).HasMaxLength(256);
            e.Property(x => x.AnswerMode).HasConversion<string>().HasMaxLength(32);
            e.HasIndex(x => new { x.SipServer, x.SipUsername }).IsUnique();
        });

        modelBuilder.Entity<Call>(e =>
        {
            e.Property(c => c.CallerNumber).HasMaxLength(64);
            e.Property(c => c.Status).HasConversion<string>().HasMaxLength(32);
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
}
