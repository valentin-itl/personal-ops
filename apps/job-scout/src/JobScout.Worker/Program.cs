using JobScout.Worker.Activities;
using JobScout.Worker.Integrations.Upwork;
using JobScout.Worker.Persistence;
using JobScout.Worker.Scanning;
using JobScout.Worker.Workflows;
using Microsoft.EntityFrameworkCore;
using Temporalio.Extensions.Hosting;

namespace JobScout.Worker
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = Host.CreateApplicationBuilder(args);

            builder.Services.AddDbContext<JobScoutDbContext>(
                options => options.UseNpgsql(
                    builder.Configuration.GetConnectionString("JobScout"),
                    npgsql => npgsql.MigrationsHistoryTable(
                        "_ef_migration_history",
                        "job_scout")));

            builder.Services.AddUpworkMcpClient(
                builder.Configuration.GetSection(UpworkMcpOptions.SectionName));
            builder.Services.AddJobScanning(builder.Configuration);

            var address =
                builder.Configuration["Temporal:Address"]
                ?? "localhost:7233";

            builder.Services.AddTemporalClient(
                address,
                "default");

            builder.Services
                .AddHostedTemporalWorker("job-scout")
                .AddWorkflow<SourceScanWorkflow>()
                .AddScopedActivities<SourceScanActivities>();

            var host = builder.Build();
            host.Run();
        }
    }
}
