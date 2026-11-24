using Microsoft.Extensions.Logging;
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

/// <summary>
/// Connects one source to the Upwork MCP server over Streamable HTTP with OAuth: the source's public client
/// (from <see cref="UpworkClientRegistrar"/>), authorization code with PKCE, tokens in the source's protected file.
/// </summary>
internal sealed class McpUpworkSessionFactory(
    UpworkMcpOptions options,
    IUpworkClientRegistrationStore registrations,
    Guid sourceId,
    ILoggerFactory loggerFactory)
    : IUpworkMcpSessionFactory, IDisposable
{
    private readonly HttpClient _registrationHttp = new() { Timeout = options.ConnectTimeout };

    public string TokenCachePath => Path.Combine(options.TokenCacheDirectory, $"upwork_tokens_{sourceId:N}.bin");

    public async Task<IUpworkMcpSession> ConnectAsync(CancellationToken cancellationToken)
    {
        var o = options;
        var registrar = new UpworkClientRegistrar(
            o, _registrationHttp, registrations, sourceId, loggerFactory.CreateLogger<McpUpworkSessionFactory>());
        var clientId = await registrar.GetClientIdAsync(cancellationToken);

        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = o.Endpoint,
                Name = "upwork",
                ConnectionTimeout = o.ConnectTimeout,
                OAuth = new ClientOAuthOptions
                {
                    // Pre-registered public client: the SDK skips its own registration (which Upwork rejects)
                    // and authenticates at the token endpoint with client_id only, as Upwork advertises "none" first.
                    ClientId = clientId,
                    RedirectUri = o.RedirectUri,
                    AuthorizationCallbackHandler = o.InteractiveSignIn
                        ? UpworkOAuth.SignInWithBrowserAsync
                        : UpworkOAuth.RejectInteractiveSignIn,
                    TokenCache = new ProtectedFileTokenCache(TokenCachePath),
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

    public void Dispose() => _registrationHttp.Dispose();
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
