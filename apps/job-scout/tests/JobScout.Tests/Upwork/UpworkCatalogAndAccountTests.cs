using System.Text.Json;
using System.Text.Json.Nodes;
using JobScout.Worker.Integrations.Upwork;
using JobScout.Worker.Integrations.Upwork.Models;
using ModelContextProtocol.Protocol;

namespace JobScout.Tests.Upwork;

public class UpworkCatalogAndAccountTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly UpworkJobSearchRequest Search = new() { Title = "MCP" };

    private static Tool FindJobsTool(Action<JsonObject> edit)
    {
        var schema = JsonNode.Parse(Fixtures.Load("find_jobs_input_schema.json"))!.AsObject();
        edit(schema);
        return new Tool { Name = "find_jobs", InputSchema = JsonDocument.Parse(schema.ToJsonString()).RootElement.Clone() };
    }

    [Fact]
    public async Task Catalog_is_verified_and_its_raw_definitions_kept()
    {
        var fake = new FakeUpworkMcp();
        await using var client = fake.CreateClient();

        var catalog = await client.VerifyToolsAsync(Ct);

        Assert.Equal("find_jobs", catalog.ToolNames["find_jobs"]);
        Assert.Contains("workload", catalog.FindJobsParamsByAction["search"]);
        Assert.Contains("days_posted", catalog.FindJobsParamsByAction["smart_search"]);
        Assert.Empty(catalog.Warnings);
        Assert.Contains("\"find_jobs\"", catalog.RawToolsJson);
        Assert.Contains("\"list_accounts\"", catalog.RawToolsJson);
    }

    [Fact]
    public async Task Missing_tool_fails_with_the_tool_list_attached()
    {
        var fake = new FakeUpworkMcp { Tools = [FakeUpworkMcp.DefaultTools()[0]] };
        await using var client = fake.CreateClient();

        var ex = await Assert.ThrowsAsync<UpworkContractMismatchException>(() => client.SearchJobsAsync(Search, Ct));

        Assert.Contains("tools/list: tool 'find_jobs' is missing", ex.Issues);
        Assert.Equal("tools/list", ex.Raw!.Tool);
        Assert.Contains("list_accounts", ex.Raw.ResponseJson);
        Assert.Equal(1, fake.DisposeCount); // the unusable session is closed
    }

    [Fact]
    public async Task Removed_action_fails()
    {
        var fake = new FakeUpworkMcp
        {
            Tools =
            [
                FakeUpworkMcp.DefaultTools()[0],
                FindJobsTool(s => s["properties"]!["action"]!["enum"] = new JsonArray("search", "get")),
            ],
        };
        await using var client = fake.CreateClient();

        var ex = await Assert.ThrowsAsync<UpworkContractMismatchException>(() => client.VerifyToolsAsync(Ct));
        Assert.Contains("find_jobs.inputSchema.properties.action.enum no longer offers 'smart_search'", ex.Issues);
    }

    [Fact]
    public async Task New_required_argument_fails()
    {
        var fake = new FakeUpworkMcp
        {
            Tools =
            [
                FakeUpworkMcp.DefaultTools()[0],
                FindJobsTool(s => s["required"] = new JsonArray("action", "org_uid", "workspace_id")),
            ],
        };
        await using var client = fake.CreateClient();

        var ex = await Assert.ThrowsAsync<UpworkContractMismatchException>(() => client.VerifyToolsAsync(Ct));
        Assert.Contains("find_jobs.inputSchema.required now includes 'workspace_id', which the client does not send", ex.Issues);
    }

    [Fact]
    public async Task Prefixed_tool_names_are_resolved()
    {
        var fake = new FakeUpworkMcp { Tools = FakeUpworkMcp.DefaultTools(prefix: "upwork__") }
            .RespondWith(Fixtures.Load("find_jobs_search.json"));
        await using var client = fake.CreateClient();

        var page = await client.SearchJobsAsync(Search, Ct);

        Assert.Equal("find_jobs", page.Raw.Tool); // logical name in the raw record
        Assert.Equal(["upwork__list_accounts", "upwork__find_jobs"], fake.Calls.Select(c => c.Tool));
    }

    [Fact]
    public async Task Parameter_the_schema_no_longer_declares_is_reported_as_drift()
    {
        var fake = new FakeUpworkMcp
        {
            Tools =
            [
                FakeUpworkMcp.DefaultTools()[0],
                FindJobsTool(s => s["allOf"]![0]!["then"]!["properties"]!["params"]!["properties"]!.AsObject().Remove("workload")),
            ],
        }.RespondWith(Fixtures.Load("find_jobs_search.json"));
        await using var client = fake.CreateClient();

        var page = await client.SearchJobsAsync(Search with { Filters = new() { Workload = UpworkWorkload.FullTime } }, Ct);

        Assert.Contains("request.params.workload: not declared in the find_jobs schema for action 'search'", page.Diagnostics.Warnings);
    }

    [Fact]
    public async Task Schema_without_per_action_branches_only_warns()
    {
        var fake = new FakeUpworkMcp { Tools = [FakeUpworkMcp.DefaultTools()[0], FindJobsTool(s => s.Remove("allOf"))] };
        await using var client = fake.CreateClient();

        var catalog = await client.VerifyToolsAsync(Ct);

        Assert.Empty(catalog.FindJobsParamsByAction);
        Assert.Equal(3, catalog.Warnings.Count);
    }

    [Fact]
    public async Task Configured_org_uid_skips_list_accounts()
    {
        var fake = new FakeUpworkMcp().RespondWith(Fixtures.Load("find_jobs_search.json"));
        await using var client = fake.CreateClient(o => o.OrgUid = "999");

        await client.SearchJobsAsync(Search, Ct);

        var call = Assert.Single(fake.Calls);
        Assert.Equal("999", (string?)JsonNode.Parse(call.ArgumentsJson)!["org_uid"]);
    }

    [Fact]
    public async Task Org_uid_is_resolved_once()
    {
        var fake = new FakeUpworkMcp().RespondWith(Fixtures.Load("find_jobs_search.json"));
        await using var client = fake.CreateClient();

        await client.SearchJobsAsync(Search, Ct);
        await client.SearchJobsAsync(Search, Ct);

        Assert.Single(fake.Calls, c => c.Tool == "list_accounts");
        Assert.Equal(1, fake.ConnectCount);
    }

    [Fact]
    public async Task Ambiguous_accounts_require_configuration()
    {
        var fake = new FakeUpworkMcp
        {
            HandlerAsync = (_, _, _) => Task.FromResult(FakeUpworkMcp.Text(
                """{"accounts":[{"org_uid":"1","role":"TALENT"},{"org_uid":"2","role":"TALENT"}]}""")),
        };
        await using var client = fake.CreateClient();

        var ex = await Assert.ThrowsAsync<UpworkInvalidRequestException>(() => client.SearchJobsAsync(Search, Ct));

        Assert.Equal("ACCOUNT_NOT_SELECTED", ex.ErrorCode);
        Assert.NotNull(ex.Raw?.ResponseJson);
    }
}
