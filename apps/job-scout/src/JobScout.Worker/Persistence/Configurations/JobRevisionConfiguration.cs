using JobScout.Worker.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace JobScout.Worker.Persistence.Configurations
{
    public sealed class JobRevisionConfiguration : IEntityTypeConfiguration<JobRevision>
    {
        public void Configure(EntityTypeBuilder<JobRevision> b)
        {
            b.ToTable("job_revisions", "job_scout");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();

            b.Property(x => x.JobId).HasColumnName("job_id").IsRequired();
            b.Property(x => x.Fingerprint).HasColumnName("fingerprint").HasMaxLength(64).IsRequired();
            b.Property(x => x.PayloadJson).HasColumnName("payload_json").HasColumnType("jsonb").IsRequired();
            b.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc").HasColumnType("timestamp with time zone");

            b.HasOne(x => x.Job).WithMany(x => x.Revisions).HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Cascade);

            b.HasIndex(x => new { x.JobId, x.Fingerprint }).IsUnique().HasDatabaseName("ux_job_revisions_job_fingerprint");
        }
    }
}