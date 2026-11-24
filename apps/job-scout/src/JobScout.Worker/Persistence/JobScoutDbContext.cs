using JobScout.Worker.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace JobScout.Worker.Persistence
{
    public class JobScoutDbContext : DbContext
    {
        public DbSet<Source> Sources => Set<Source>();
        public DbSet<SourceScan> SourceScans => Set<SourceScan>();
        public DbSet<Job> Jobs => Set<Job>();
        public DbSet<JobRevision> JobRevisions => Set<JobRevision>();
        public DbSet<EvaluationOutbox> EvaluationOutbox => Set<EvaluationOutbox>();

        public JobScoutDbContext()
        {

        }

        public JobScoutDbContext(DbContextOptions<JobScoutDbContext> context) : base(context)
        {

        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.HasDefaultSchema("job_scout");
            modelBuilder.ApplyConfigurationsFromAssembly(typeof(JobScoutDbContext).Assembly);
        }
    }
}
