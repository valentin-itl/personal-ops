using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace JobScout.Worker.Integrations.Upwork;

public static class UpworkServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IUpworkMcpClient"/> as a singleton (one MCP connection per process),
    /// bound to the given configuration section (normally <c>Upwork</c>).
    /// </summary>
    public static IServiceCollection AddUpworkMcpClient(this IServiceCollection services, IConfiguration section)
    {
        services.AddOptions<UpworkMcpOptions>().Bind(section);
        services.TryAddSingleton<IUpworkMcpSessionFactory, McpUpworkSessionFactory>();
        services.TryAddSingleton<IUpworkMcpClient, UpworkMcpClient>();
        return services;
    }
}
