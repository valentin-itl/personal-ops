using JobScout.Worker.Integrations.Upwork;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace JobScout.Worker.Scanning;

public static class ScanningServiceCollectionExtensions
{
    /// <summary>
    /// Registers the scanner, binds search profiles from the <c>JobScout</c> section, and keeps each source's
    /// OAuth client registration on its database row.
    /// </summary>
    public static IServiceCollection AddJobScanning(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<JobScoutScanOptions>(configuration.GetSection(JobScoutScanOptions.SectionName));
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<UpworkSourceScanner>();
        services.TryAddSingleton<IUpworkClientRegistrationStore, SourceClientRegistrationStore>();
        return services;
    }
}
