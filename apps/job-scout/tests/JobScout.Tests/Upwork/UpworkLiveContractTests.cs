using JobScout.Worker.Integrations.Upwork;
using JobScout.Worker.Integrations.Upwork.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace JobScout.Tests.Upwork;

/// <summary>
/// Runs the client's contract checks against the real Upwork MCP server. Skipped unless
/// <c>JOBSCOUT_UPWORK_LIVE=1</c>. Set <c>JOBSCOUT_UPWORK_CLIENT_ID</c> to an already registered OAuth client
/// (otherwise a new one is registered) and <c>JOBSCOUT_UPWORK_INTERACTIVE=1</c> to allow a browser sign-in when
/// no token is cached. Tokens go to the normal token folder under a fixed test source id. Read-only calls only.
/// </summary>
public class UpworkLiveContractTests
{
    [Fact]
    public async Task Live_server_matches_the_client_contract()
    {
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable("JOBSCOUT_UPWORK_LIVE") == "1",
            "Set JOBSCOUT_UPWORK_LIVE=1 to check the real Upwork MCP server.");

        var ct = TestContext.Current.CancellationToken;
        var output = TestContext.Current.TestOutputHelper;
        var options = new UpworkMcpOptions
        {
            InteractiveSignIn = Environment.GetEnvironmentVariable("JOBSCOUT_UPWORK_INTERACTIVE") == "1",
        };
        var liveSourceId = new Guid("00000000-0000-0000-0000-00000000fade");
        var registrations = new InMemoryRegistrationStore();
        if (Environment.GetEnvironmentVariable("JOBSCOUT_UPWORK_CLIENT_ID") is { Length: > 0 } clientId)
            registrations.Saved[liveSourceId] = UpworkClientRegistration.Parse($$"""{"client_id":"{{clientId}}"}""");

        using var factory = new McpUpworkSessionFactory(options, registrations, liveSourceId, NullLoggerFactory.Instance);
        await using var client = new UpworkMcpClient(factory, Options.Create(options), NullLogger<UpworkMcpClient>.Instance);

        var catalog = await client.VerifyToolsAsync(ct);
        Report("catalog", catalog.Warnings);

        var search = await client.SearchJobsAsync(
            new UpworkJobSearchRequest { Title = "developer", Sort = UpworkJobSort.Recency, Limit = 3 }, ct);
        Assert.NotEmpty(search.Jobs);
        Report("search", search.Diagnostics);

        var smart = await client.SmartSearchJobsAsync(new UpworkSmartSearchRequest { DaysPosted = 3, Limit = 3 }, ct);
        Report("smart_search", smart.Diagnostics);

        try
        {
            var details = await client.GetJobAsync(search.Jobs[0].Id, ct);
            Report("get", details.Diagnostics);
        }
        catch (UpworkJobUnavailableException ex)
        {
            output?.WriteLine($"get skipped: {ex.Message}");
        }

        void Report(string what, object diagnostics)
        {
            var lines = diagnostics switch
            {
                UpworkResponseDiagnostics d => d.UnknownFields.Select(f => $"unknown field {f}").Concat(d.Warnings),
                IEnumerable<string> warnings => warnings,
                _ => [],
            };
            foreach (var line in lines)
                output?.WriteLine($"{what}: {line}");
        }
    }
}
