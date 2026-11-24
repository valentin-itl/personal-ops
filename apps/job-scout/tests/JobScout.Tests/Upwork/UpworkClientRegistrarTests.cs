using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using JobScout.Worker.Integrations.Upwork;
using Microsoft.Extensions.Logging.Abstractions;

namespace JobScout.Tests.Upwork;

public sealed class UpworkClientRegistrarTests
{
    // Upwork's metadata as served on 2026-11-24.
    private const string ResourceMetadata =
        """{"resource":"https://mcp.upwork.com/mcp","authorization_servers":["https://mcp.upwork.com"],"bearer_methods_supported":["header"],"resource_name":"Upwork MCP"}""";

    private const string AuthServerMetadata =
        """{"issuer":"https://mcp.upwork.com","authorization_endpoint":"https://www.upwork.com/ab/account-security/oauth2/authorize","token_endpoint":"https://www.upwork.com/api/v3/oauth2/token","registration_endpoint":"https://www.upwork.com/register","response_types_supported":["code"],"grant_types_supported":["authorization_code","refresh_token","client_credentials"],"token_endpoint_auth_methods_supported":["none","client_secret_basic","client_secret_post","private_key_jwt"],"code_challenge_methods_supported":["S256"],"client_id_metadata_document_supported":true}""";

    // Shape of Upwork's 201 response to the accepted registration (token value invented).
    private const string Accepted =
        """{"client_id":"7e4cb25799f6a5c3d1196850fabcce95","client_id_issued_at":1795529807,"client_secret_expires_at":0,"registration_access_token":"secret-token","registration_client_uri":"https://www.upwork.com/register/7e4cb25799f6a5c3d1196850fabcce95","client_name":"Personal Upwork job scanner","redirect_uris":["http://localhost:53682/callback"],"grant_types":["authorization_code","refresh_token"],"response_types":["code"],"token_endpoint_auth_method":"none"}""";

    private const string Rejected =
        """{"error_description":"The client registration metadata is invalid","error":"invalid_client_metadata"}""";

    private static readonly Guid SourceId = Guid.NewGuid();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<string> GetClientId(InMemoryRegistrationStore store, FakeHandler handler, UpworkMcpOptions? options = null) =>
        new UpworkClientRegistrar(options ?? new UpworkMcpOptions(), new HttpClient(handler), store, SourceId, NullLogger.Instance)
            .GetClientIdAsync(Ct);

    [Fact]
    public async Task Registers_as_a_public_client_and_saves_it_on_the_source()
    {
        var store = new InMemoryRegistrationStore();
        var handler = FakeHandler.Upwork(_ => Json(HttpStatusCode.Created, Accepted));

        var clientId = await GetClientId(store, handler);

        Assert.Equal("7e4cb25799f6a5c3d1196850fabcce95", clientId);
        var register = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post);
        Assert.Equal("https://www.upwork.com/register", register.Url);
        var expected = JsonNode.Parse("""
            {
              "client_name": "Personal Upwork job scanner",
              "redirect_uris": ["http://localhost:53682/callback"],
              "grant_types": ["authorization_code", "refresh_token"],
              "response_types": ["code"],
              "token_endpoint_auth_method": "none"
            }
            """);
        Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(register.Body!)), register.Body); // no application_type

        var saved = store.Saved[SourceId];
        Assert.Equal(clientId, saved.ClientId);
        Assert.Equal(["http://localhost:53682/callback"], saved.RedirectUris);
        Assert.DoesNotContain("registration_access_token", saved.Json); // secrets stay out of the database
        Assert.Contains("\"registration_client_uri\"", saved.Json);
    }

    [Fact]
    public async Task Saved_registration_is_reused()
    {
        var store = new InMemoryRegistrationStore();
        store.Saved[SourceId] = UpworkClientRegistration.Parse(Accepted);
        var handler = FakeHandler.Upwork(_ => throw new InvalidOperationException("must not register"));

        Assert.Equal("7e4cb25799f6a5c3d1196850fabcce95", await GetClientId(store, handler));
        Assert.Empty(handler.Requests);
        Assert.Equal(0, store.SaveCount);
    }

    [Fact]
    public async Task Hand_entered_client_id_is_used_as_is()
    {
        var store = new InMemoryRegistrationStore();
        store.Saved[SourceId] = UpworkClientRegistration.Parse("""{"client_id":"7e4cb25799f6a5c3d1196850fabcce95"}""");
        var handler = FakeHandler.Upwork(_ => throw new InvalidOperationException("must not register"));

        Assert.Equal("7e4cb25799f6a5c3d1196850fabcce95", await GetClientId(store, handler));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Registration_for_another_redirect_uri_is_replaced()
    {
        var store = new InMemoryRegistrationStore();
        store.Saved[SourceId] = UpworkClientRegistration.Parse(Accepted);
        var options = new UpworkMcpOptions { RedirectUri = new Uri("http://localhost:53999/callback") };
        var handler = FakeHandler.Upwork(_ => Json(HttpStatusCode.Created, """{"client_id":"second-id","redirect_uris":["http://localhost:53999/callback"]}"""));

        Assert.Equal("second-id", await GetClientId(store, handler, options));
        Assert.Contains("53999", Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post).Body);
        Assert.Equal("second-id", store.Saved[SourceId].ClientId);
    }

    [Fact]
    public async Task Rejected_registration_is_auth_required_and_nothing_is_saved()
    {
        var store = new InMemoryRegistrationStore();

        var ex = await Assert.ThrowsAsync<UpworkAuthRequiredException>(
            () => GetClientId(store, FakeHandler.Upwork(_ => Json(HttpStatusCode.BadRequest, Rejected))));

        Assert.Equal("CLIENT_REGISTRATION_REJECTED", ex.ErrorCode);
        Assert.Equal(UpworkFailureDisposition.Pause, ex.Disposition);
        Assert.Equal(Rejected, ex.Raw!.ResponseJson);
        Assert.Contains("\"token_endpoint_auth_method\":\"none\"", ex.Raw.RequestJson);
        Assert.Empty(store.Saved);
    }

    [Fact]
    public async Task Missing_registration_endpoint_is_a_contract_mismatch()
    {
        var withoutRegistration = JsonNode.Parse(AuthServerMetadata)!.AsObject();
        withoutRegistration.Remove("registration_endpoint");
        var handler = new FakeHandler((request, _) => request.RequestUri!.AbsoluteUri.Contains("oauth-protected-resource")
            ? Json(HttpStatusCode.OK, ResourceMetadata)
            : Json(HttpStatusCode.OK, withoutRegistration.ToJsonString()));

        var ex = await Assert.ThrowsAsync<UpworkContractMismatchException>(() => GetClientId(new InMemoryRegistrationStore(), handler));

        Assert.Contains(ex.Issues, i => i.StartsWith("$.registration_endpoint", StringComparison.Ordinal));
        Assert.Contains("authorization_endpoint", ex.Raw!.ResponseJson);
    }

    [Theory]
    [InlineData("""{"redirect_uris":["http://localhost:53682/callback"]}""", "client_id is missing")]
    [InlineData("""{"client_id":42}""", "client_id is missing")]
    [InlineData("""["7e4cb25799f6a5c3d1196850fabcce95"]""", "expected a JSON object")]
    [InlineData("7e4cb25799f6a5c3d1196850fabcce95", "not valid JSON")]
    public void Unusable_registration_json_is_rejected(string json, string reason)
    {
        var ex = Assert.Throws<FormatException>(() => UpworkClientRegistration.Parse(json));
        Assert.Contains(reason, ex.Message);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class FakeHandler(Func<HttpRequestMessage, string?, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Url, string? Body)> Requests { get; } = [];

        /// <summary>Serves Upwork's discovery documents; <paramref name="register"/> answers the registration POST.</summary>
        public static FakeHandler Upwork(Func<string, HttpResponseMessage> register) =>
            new((request, body) => request.RequestUri!.AbsoluteUri switch
            {
                "https://mcp.upwork.com/.well-known/oauth-protected-resource/mcp" => Json(HttpStatusCode.OK, ResourceMetadata),
                "https://mcp.upwork.com/.well-known/oauth-authorization-server" => Json(HttpStatusCode.OK, AuthServerMetadata),
                "https://www.upwork.com/register" => register(body!),
                var other => throw new InvalidOperationException($"unexpected request to {other}"),
            });

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method, request.RequestUri!.ToString(), body));
            return respond(request, body);
        }
    }
}

internal sealed class InMemoryRegistrationStore : IUpworkClientRegistrationStore
{
    public Dictionary<Guid, UpworkClientRegistration> Saved { get; } = [];

    public int SaveCount { get; private set; }

    public Task<UpworkClientRegistration?> LoadAsync(Guid sourceId, CancellationToken cancellationToken) =>
        Task.FromResult(Saved.GetValueOrDefault(sourceId));

    public Task SaveAsync(Guid sourceId, UpworkClientRegistration registration, CancellationToken cancellationToken)
    {
        Saved[sourceId] = registration;
        SaveCount++;
        return Task.CompletedTask;
    }
}
