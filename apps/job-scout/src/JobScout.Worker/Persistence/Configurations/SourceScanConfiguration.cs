using JobScout.Worker.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace JobScout.Worker.Persistence.Configurations;

public sealed class SourceScanConfiguration : IEntityTypeConfiguration<SourceScan>
{
    public void Configure(EntityTypeBuilder<SourceScan> b)
    {
        b.ToTable("source_scans", "job_scout");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();

        b.Property(x => x.SourceId).HasColumnName("source_id").IsRequired();
        b.Property(x => x.SearchProfileId).HasColumnName("search_profile_id").HasMaxLength(128).IsRequired();
        b.Property(x => x.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(24).IsRequired();
        b.Property(x => x.StartedAtUtc).HasColumnName("started_at_utc").HasColumnType("timestamp with time zone");
        b.Property(x => x.CompletedAtUtc).HasColumnName("completed_at_utc").HasColumnType("timestamp with time zone");

        b.Property(x => x.ObservedCount).HasColumnName("observed_count");
        b.Property(x => x.CreatedCount).HasColumnName("created_count");
        b.Property(x => x.UpdatedCount).HasColumnName("updated_count");
        b.Property(x => x.UnchangedCount).HasColumnName("unchanged_count");
        b.Property(x => x.EvaluationsQueuedCount).HasColumnName("evaluations_queued_count");
        b.Property(x => x.ErrorMessage).HasColumnName("error_message").HasColumnType("text");

        b.HasOne(x => x.Source).WithMany(x => x.SourceScans).HasForeignKey(x => x.SourceId).OnDelete(DeleteBehavior.Cascade);

        b.HasIndex(x => new { x.SourceId, x.SearchProfileId, x.StartedAtUtc }).HasDatabaseName("ix_source_scans_source_profile_started");
    }
}
