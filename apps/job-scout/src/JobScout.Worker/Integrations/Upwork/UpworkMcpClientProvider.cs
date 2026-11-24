using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JobScout.Worker.Integrations.Upwork;

/// <summary>Hands out the Upwork client of a source: each source has its own OAuth client, sign-in and connection.</summary>
public interface IUpworkMcpClientProvider
{
    IUpworkMcpClient GetClient(Guid sourceId);
}

internal sealed class UpworkMcpClientProvider(
    IOptions<UpworkMcpOptions> options,
    IUpworkClientRegistrationStore registrations,
    ILoggerFactory loggerFactory)
    : IUpworkMcpClientProvider, IAsyncDisposable, IDisposable
{
    private readonly ConcurrentDictionary<Guid, Lazy<(McpUpworkSessionFactory Factory, UpworkMcpClient Client)>> _clients = new();

    public IUpworkMcpClient GetClient(Guid sourceId) =>
        _clients.GetOrAdd(sourceId, id => new Lazy<(McpUpworkSessionFactory, UpworkMcpClient)>(() =>
        {
            var factory = new McpUpworkSessionFactory(options.Value, registrations, id, loggerFactory);
            var client = new UpworkMcpClient(factory, options, loggerFactory.CreateLogger<UpworkMcpClient>());
            return (factory, client);
        })).Value.Client;

    public async ValueTask DisposeAsync()
    {
        foreach (var entry in _clients.Values.Where(e => e.IsValueCreated).Select(e => e.Value))
        {
            await entry.Client.DisposeAsync();
            entry.Factory.Dispose();
        }

        _clients.Clear();
    }

    /// <summary>For containers disposed synchronously (the generic host disposes asynchronously).</summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
