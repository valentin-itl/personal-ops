using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace JobScout.Worker.Integrations.Upwork;

/// <summary>One live MCP connection. The seam between <see cref="UpworkMcpClient"/> and the SDK (fake it in tests).</summary>
public interface IUpworkMcpSession : IAsyncDisposable
{
    Task<IReadOnlyList<Tool>> ListToolsAsync(CancellationToken cancellationToken);

    Task<CallToolResult> CallToolAsync(
        string toolName, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken);
}

public interface IUpworkMcpSessionFactory
{
    Task<IUpworkMcpSession> ConnectAsync(CancellationToken cancellationToken);
}

/// <summary>Connects to the Upwork MCP server over Streamable HTTP with OAuth (dynamic client registration).</summary>
internal sealed class McpUpworkSessionFactory(IOptions<UpworkMcpOptions> options, ILoggerFactory loggerFactory)
    : IUpworkMcpSessionFactory
{
    public async Task<IUpworkMcpSession> ConnectAsync(CancellationToken cancellationToken)
    {
        var o = options.Value;
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = o.Endpoint,
                Name = "upwork",
                ConnectionTimeout = o.ConnectTimeout,
                OAuth = new ClientOAuthOptions
                {
                    // No ClientId: Upwork advertises a registration endpoint, so the SDK registers the app itself.
                    DynamicClientRegistration = new DynamicClientRegistrationOptions { ClientName = o.ClientName },
                    RedirectUri = o.RedirectUri,
                    AuthorizationCallbackHandler = o.InteractiveSignIn
                        ? UpworkOAuth.SignInWithBrowserAsync
                        : UpworkOAuth.RejectInteractiveSignIn,
                    TokenCache = new ProtectedFileTokenCache(o.TokenCachePath),
                },
            },
            loggerFactory);

        try
        {
            var client = await McpClient.CreateAsync(transport, loggerFactory: loggerFactory, cancellationToken: cancellationToken);
            return new McpUpworkSession(client, transport);
        }
        catch
        {
            await transport.DisposeAsync();
            throw;
        }
    }
}

internal sealed class McpUpworkSession(McpClient client, HttpClientTransport transport) : IUpworkMcpSession
{
    public async Task<IReadOnlyList<Tool>> ListToolsAsync(CancellationToken cancellationToken)
    {
        var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
        return tools.Select(t => t.ProtocolTool).ToList();
    }

    public Task<CallToolResult> CallToolAsync(
        string toolName, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken) =>
        client.CallToolAsync(toolName, arguments, cancellationToken: cancellationToken).AsTask();

    public async ValueTask DisposeAsync()
    {
        await client.DisposeAsync();
        await transport.DisposeAsync();
    }
}
