using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace JobScout.Worker.Scanning;

public static class ScanningServiceCollectionExtensions
{
    /// <summary>Registers the scanner and binds search profiles from the <c>JobScout</c> section.</summary>
    public static IServiceCollection AddJobScanning(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<JobScoutScanOptions>(configuration.GetSection(JobScoutScanOptions.SectionName));
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<UpworkSourceScanner>();
        return services;
    }
}
