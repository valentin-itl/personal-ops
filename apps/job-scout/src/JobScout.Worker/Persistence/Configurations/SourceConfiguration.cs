using JobScout.Worker.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace JobScout.Worker.Persistence.Configurations;

public sealed class SourceConfiguration : IEntityTypeConfiguration<Source>
{
    public void Configure(EntityTypeBuilder<Source> b)
    {
        b.ToTable("sources", "job_scout");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();

        b.Property(x => x.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
        b.Property(x => x.OAuthClientJson).HasColumnName("oauth_client_json").HasColumnType("jsonb");

        b.Property(x => x.Health).HasColumnName("health").HasConversion<string>().HasMaxLength(24).IsRequired();

        b.Property(x => x.ErrorMessage).HasColumnName("error_message").HasColumnType("text");
    }
}
