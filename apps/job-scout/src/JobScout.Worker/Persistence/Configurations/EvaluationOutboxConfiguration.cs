using JobScout.Worker.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace JobScout.Worker.Persistence.Configurations;

public sealed class EvaluationOutboxConfiguration : IEntityTypeConfiguration<EvaluationOutbox>
{
    public void Configure(EntityTypeBuilder<EvaluationOutbox> b)
    {
        b.ToTable("evaluation_outbox", "job_scout");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();

        b.Property(x => x.JobRevisionId).HasColumnName("job_revision_id").IsRequired();
        b.Property(x => x.EvaluationKey).HasColumnName("evaluation_key").HasMaxLength(256).IsRequired();
        b.Property(x => x.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(24).IsRequired();
        b.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc").HasColumnType("timestamp with time zone");
        b.Property(x => x.DispatchedAtUtc).HasColumnName("dispatched_at_utc").HasColumnType("timestamp with time zone");
        b.Property(x => x.AttemptCount).HasColumnName("attempt_count");
        b.Property(x => x.LastError).HasColumnName("last_error").HasColumnType("text");

        b.HasOne(x => x.JobRevision).WithMany(x => x.EvaluationRequests).HasForeignKey(x => x.JobRevisionId).OnDelete(DeleteBehavior.Cascade);

        b.HasIndex(x => x.EvaluationKey).IsUnique().HasDatabaseName("ux_evaluation_outbox_key");
        b.HasIndex(x => new { x.Status, x.CreatedAtUtc }).HasDatabaseName("ix_evaluation_outbox_pending");
    }
}
