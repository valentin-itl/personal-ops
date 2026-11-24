using JobScout.Worker.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace JobScout.Worker.Persistence.Configurations;

public sealed class JobConfiguration : IEntityTypeConfiguration<Job>
{
    public void Configure(EntityTypeBuilder<Job> b)
    {
        b.ToTable("jobs", "job_scout");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();

        b.Property(x => x.SourceId).HasColumnName("source_id").IsRequired();
        b.Property(x => x.ExternalId).HasColumnName("external_id").HasMaxLength(512).IsRequired();
        b.Property(x => x.Availability).HasColumnName("availability").HasConversion<string>().HasMaxLength(24).IsRequired();
        b.Property(x => x.FirstSeenAtUtc).HasColumnName("first_seen_at_utc").HasColumnType("timestamp with time zone");
        b.Property(x => x.LastSeenAtUtc).HasColumnName("last_seen_at_utc").HasColumnType("timestamp with time zone");
        b.Property(x => x.CurrentFingerprint).HasColumnName("current_fingerprint").HasMaxLength(64);

        b.HasOne(x => x.Source).WithMany(x => x.Jobs).HasForeignKey(x => x.SourceId).OnDelete(DeleteBehavior.Cascade);

        b.HasIndex(x => new { x.SourceId, x.ExternalId }).IsUnique().HasDatabaseName("ux_jobs_source_external");
        b.HasIndex(x => x.LastSeenAtUtc).HasDatabaseName("ix_jobs_last_seen");
    }
}
