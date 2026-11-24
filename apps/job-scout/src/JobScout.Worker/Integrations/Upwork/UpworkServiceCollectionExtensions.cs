using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace JobScout.Worker.Integrations.Upwork;

public static class UpworkServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IUpworkMcpClientProvider"/> (one client and MCP connection per source), bound to the
    /// given configuration section (normally <c>Upwork</c>). Needs an <see cref="IUpworkClientRegistrationStore"/>.
    /// </summary>
    public static IServiceCollection AddUpworkMcpClient(this IServiceCollection services, IConfiguration section)
    {
        services.AddOptions<UpworkMcpOptions>().Bind(section);
        services.TryAddSingleton<IUpworkMcpClientProvider, UpworkMcpClientProvider>();
        return services;
    }
}
