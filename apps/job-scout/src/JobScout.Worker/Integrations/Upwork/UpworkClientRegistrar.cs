using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using JobScout.Worker.Integrations.Upwork.Models;
using Microsoft.Extensions.Logging;

namespace JobScout.Worker.Integrations.Upwork;

/// <summary>
/// Provides the OAuth client ID a source signs in with: the registration saved on the source, or, when there is
/// none (or it was made for another redirect URI), a new registration with Upwork that is then saved.
/// The SDK's built-in registration cannot be used: it always sends <c>application_type</c> and asks for a
/// confidential client (<c>client_secret_post</c>), and Upwork rejects that with <c>invalid_client_metadata</c>.
/// A public client (<c>token_endpoint_auth_method: none</c>, PKCE) is accepted.
/// </summary>
internal sealed class UpworkClientRegistrar(
    UpworkMcpOptions options,
    HttpClient http,
    IUpworkClientRegistrationStore store,
    Guid sourceId,
    ILogger logger)
{
    public const string RegisterTool = "oauth/register";
    public const string DiscoveryTool = "oauth/discovery";

    public async Task<string> GetClientIdAsync(CancellationToken cancellationToken)
    {
        if (await store.LoadAsync(sourceId, cancellationToken) is { } saved)
        {
            if (saved.AllowsRedirect(options.RedirectUri))
                return saved.ClientId;

            logger.LogWarning(
                "OAuth client {ClientId} of source {SourceId} is registered for {Registered}, not {Configured}; registering a new one",
                saved.ClientId, sourceId, string.Join(", ", saved.RedirectUris), options.RedirectUri);
        }

        var endpoint = await DiscoverRegistrationEndpointAsync(cancellationToken);
        var registration = await RegisterAsync(endpoint, options.RedirectUri.ToString(), cancellationToken);
        await store.SaveAsync(sourceId, registration, cancellationToken);

        logger.LogInformation(
            "Registered OAuth client {ClientId} with Upwork for source {SourceId} and saved it on the source",
            registration.ClientId, sourceId);
        return registration.ClientId;
    }

    /// <summary>MCP resource metadata (RFC 9728) → authorization server metadata (RFC 8414) → registration_endpoint.</summary>
    private async Task<Uri> DiscoverRegistrationEndpointAsync(CancellationToken cancellationToken)
    {
        var resource = options.Endpoint;
        var resourceMetadataUri = new Uri(resource, "/.well-known/oauth-protected-resource" + resource.AbsolutePath.TrimEnd('/'));
        var resourceMetadata = await GetJsonAsync(resourceMetadataUri, cancellationToken);
        if (resourceMetadata["authorization_servers"] is not JsonArray { Count: > 0 } servers
            || servers[0]?.GetValueKind() != JsonValueKind.String
            || !Uri.TryCreate(servers[0]!.GetValue<string>(), UriKind.Absolute, out var authServer))
        {
            throw Mismatch(resourceMetadataUri, resourceMetadata, "$.authorization_servers: missing or not a list of URLs");
        }

        var authMetadataUri = new Uri(authServer, "/.well-known/oauth-authorization-server" + authServer.AbsolutePath.TrimEnd('/'));
        var authMetadata = await GetJsonAsync(authMetadataUri, cancellationToken);
        if (authMetadata["registration_endpoint"]?.GetValueKind() != JsonValueKind.String
            || !Uri.TryCreate(authMetadata["registration_endpoint"]!.GetValue<string>(), UriKind.Absolute, out var endpoint))
        {
            throw Mismatch(authMetadataUri, authMetadata, "$.registration_endpoint: missing; Upwork no longer offers client registration");
        }

        return endpoint;
    }

    private async Task<UpworkClientRegistration> RegisterAsync(Uri endpoint, string redirectUri, CancellationToken cancellationToken)
    {
        var requestJson = new JsonObject
        {
            ["client_name"] = options.ClientName,
            ["redirect_uris"] = new JsonArray(redirectUri),
            ["grant_types"] = new JsonArray("authorization_code", "refresh_token"),
            ["response_types"] = new JsonArray("code"),
            ["token_endpoint_auth_method"] = "none",
        }.ToJsonString();

        var startedAt = DateTimeOffset.UtcNow;
        using var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
        using var response = await http.PostAsync(endpoint, content, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        var raw = new UpworkRawExchange
        {
            Tool = RegisterTool,
            RequestJson = requestJson,
            ResponseJson = responseText,
            PayloadSource = UpworkPayloadSource.TextContent,
            IsError = !response.IsSuccessStatusCode,
            StartedAt = startedAt,
            CompletedAt = DateTimeOffset.UtcNow,
        };

        if (!response.IsSuccessStatusCode)
        {
            var code = $"HTTP_{(int)response.StatusCode}";
            var message = UpworkErrorMapper.Describe(RegisterTool, null, code, $"Upwork refused the client registration: {Truncate(responseText)}", null);
            if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests)
                throw new UpworkTransientException(message, raw, code);
            throw new UpworkAuthRequiredException(message, raw, "CLIENT_REGISTRATION_REJECTED");
        }

        try
        {
            return UpworkClientRegistration.Parse(responseText);
        }
        catch (FormatException ex)
        {
            throw new UpworkContractMismatchException(
                UpworkErrorMapper.Describe(RegisterTool, null, "CONTRACT_MISMATCH", $"unusable registration response: {ex.Message}", null),
                raw,
                "CONTRACT_MISMATCH")
            {
                Issues = [$"$: {ex.Message}"],
            };
        }
    }

    private async Task<JsonObject> GetJsonAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(uri, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if ((int)response.StatusCode >= 500)
            throw new UpworkTransientException(UpworkErrorMapper.Describe(DiscoveryTool, null, $"HTTP_{(int)response.StatusCode}", uri.ToString(), null));
        try
        {
            if (response.IsSuccessStatusCode && JsonNode.Parse(text) is JsonObject json)
                return json;
        }
        catch (JsonException)
        {
        }

        throw Mismatch(uri, text, $"$: expected OAuth metadata JSON, got HTTP {(int)response.StatusCode}");
    }

    private static UpworkContractMismatchException Mismatch(Uri uri, object payload, string issue)
    {
        var text = payload is JsonNode node ? node.ToJsonString() : payload.ToString();
        var raw = new UpworkRawExchange
        {
            Tool = DiscoveryTool,
            RequestJson = JsonSerializer.Serialize(new { url = uri.ToString() }),
            ResponseJson = text,
            PayloadSource = UpworkPayloadSource.TextContent,
            StartedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow,
        };
        return new UpworkContractMismatchException(
            UpworkErrorMapper.Describe(DiscoveryTool, null, "CONTRACT_MISMATCH", $"{uri}: {issue}", null), raw, "CONTRACT_MISMATCH")
        {
            Issues = [issue],
        };
    }

    private static string Truncate(string text) => text.Length <= 500 ? text : text[..500] + "…";
}
