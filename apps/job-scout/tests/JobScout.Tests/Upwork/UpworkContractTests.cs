using System.Text.Json.Nodes;
using JobScout.Worker.Integrations.Upwork;
using JobScout.Worker.Integrations.Upwork.Models;

namespace JobScout.Tests.Upwork;

/// <summary>Format changes: breaking ones fail with the raw payload attached; additive ones are reported as drift.</summary>
public class UpworkContractTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<UpworkContractMismatchException> SearchExpectingMismatch(string payload)
    {
        var fake = new FakeUpworkMcp().RespondWith(payload);
        await using var client = fake.CreateClient();
        var ex = await Assert.ThrowsAsync<UpworkContractMismatchException>(
            () => client.SearchJobsAsync(new UpworkJobSearchRequest { Title = "MCP" }, Ct));
        Assert.Equal(UpworkFailureDisposition.Terminate, ex.Disposition);
        Assert.Equal(payload, ex.Raw!.ResponseJson);
        Assert.NotNull(ex.Raw.RequestJson);
        return ex;
    }

    [Fact]
    public async Task Missing_jobs_array_is_a_contract_mismatch()
    {
        var ex = await SearchExpectingMismatch(Fixtures.Edit("find_jobs_search.json", o => o.Remove("jobs")));
        Assert.Contains("$.jobs: required array is missing", ex.Issues);
        Assert.Equal("a5d83b0665ee9056956097fec402a153", ex.TraceId);
    }

    [Fact]
    public async Task Job_without_id_is_reported_with_its_path()
    {
        var ex = await SearchExpectingMismatch(
            Fixtures.Edit("find_jobs_search.json", o => o["jobs"]![1]!.AsObject().Remove("id")));
        Assert.Contains("$.jobs[1].id: required string is missing", ex.Issues);
    }

    [Fact]
    public async Task Renamed_or_retyped_required_field_is_a_contract_mismatch()
    {
        var ex = await SearchExpectingMismatch(Fixtures.Edit("find_jobs_search.json", o =>
        {
            o["jobs"]![0]!["published_date"] = "yesterday";
            o["jobs"]![1]!["title"] = 42;
        }));
        Assert.Contains("$.jobs[0].published_date: 'yesterday' is not a valid timestamp", ex.Issues);
        Assert.Contains("$.jobs[1].title: expected string, got number", ex.Issues);
    }

    [Fact]
    public async Task Missing_status_is_a_contract_mismatch()
    {
        var ex = await SearchExpectingMismatch(Fixtures.Edit("find_jobs_search.json", o => o.Remove("status")));
        Assert.Contains("$.status: required string is missing", ex.Issues);
    }

    [Fact]
    public async Task More_pages_without_a_cursor_is_a_contract_mismatch()
    {
        var ex = await SearchExpectingMismatch(Fixtures.Edit("find_jobs_search.json", o =>
        {
            o.Remove("next_cursor");
            o["pageInfo"]!.AsObject().Remove("endCursor");
        }));
        Assert.Contains(ex.Issues, i => i.StartsWith("$.next_cursor:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Missing_paging_flags_is_a_contract_mismatch()
    {
        var ex = await SearchExpectingMismatch(Fixtures.Edit("find_jobs_search.json", o =>
        {
            o.Remove("hasMore");
            o.Remove("pageInfo");
        }));
        Assert.Contains(ex.Issues, i => i.StartsWith("$.hasMore:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Non_json_payload_is_a_contract_mismatch_and_kept_verbatim()
    {
        var ex = await SearchExpectingMismatch("Service temporarily degraded, please try later");
        Assert.Equal(["$: payload is not JSON"], ex.Issues);
        Assert.Equal(UpworkPayloadSource.TextContent, ex.Raw!.PayloadSource);
    }

    [Fact]
    public async Task Get_without_posting_is_a_contract_mismatch()
    {
        var payload = Fixtures.Edit("find_jobs_get_hourly.json", o => o["data"]!.AsObject().Remove("marketplaceJobPosting"));
        var fake = new FakeUpworkMcp().RespondWith(payload);
        await using var client = fake.CreateClient();

        var ex = await Assert.ThrowsAsync<UpworkContractMismatchException>(() => client.GetJobAsync("1", Ct));

        Assert.Contains("$.data.marketplaceJobPosting: required object is missing", ex.Issues);
        Assert.Equal(payload, ex.Raw!.ResponseJson);
    }

    [Fact]
    public async Task New_fields_and_unknown_values_are_reported_as_drift_not_failures()
    {
        var payload = Fixtures.Edit("find_jobs_search.json", o =>
        {
            o["brand_new_top_level"] = true;
            o["jobs"]![0]!["brand_new_field"] = "x";
            o["jobs"]![1]!["brand_new_field"] = "y";
            o["jobs"]![0]!["experience_level"] = "Guru";
            o["jobs"]![1]!["client"]!["rating"] = "five stars";
        });
        var fake = new FakeUpworkMcp().RespondWith(payload);
        await using var client = fake.CreateClient();

        var page = await client.SearchJobsAsync(new UpworkJobSearchRequest { Title = "MCP" }, Ct);

        Assert.True(page.Diagnostics.HasDrift);
        Assert.Equal(["$.brand_new_top_level", "$.jobs[*].brand_new_field"], page.Diagnostics.UnknownFields);
        Assert.Contains("$.jobs[*].experience_level: unknown value 'Guru'", page.Diagnostics.Warnings);
        Assert.Contains(page.Diagnostics.Warnings, w => w.StartsWith("$.jobs[*].client.rating: expected a number", StringComparison.Ordinal));
        Assert.Equal(UpworkExperienceLevel.Unknown, page.Jobs[0].ExperienceLevel);
        Assert.Null(page.Jobs[1].Client!.Rating);
        Assert.Equal(payload, page.Raw.ResponseJson); // the dropped values are still in the raw JSON
    }

    [Fact]
    public async Task Drift_fails_the_call_when_configured()
    {
        var payload = Fixtures.Edit("find_jobs_search.json", o => o["jobs"]![0]!["brand_new_field"] = "x");
        var fake = new FakeUpworkMcp().RespondWith(payload);
        await using var client = fake.CreateClient(o => o.TreatDriftAsError = true);

        var ex = await Assert.ThrowsAsync<UpworkContractMismatchException>(
            () => client.SearchJobsAsync(new UpworkJobSearchRequest { Title = "MCP" }, Ct));

        Assert.Equal("SCHEMA_DRIFT", ex.ErrorCode);
        Assert.Contains("$.jobs[*].brand_new_field: field not mapped by the client", ex.Issues);
    }

    [Theory]
    [InlineData("filters_rejected", """[{"field":"location","value":"USA","accepted":"United States"}]""")]
    [InlineData("filters_ignored", """["Rust Lang"]""")]
    public async Task Filters_the_server_did_not_apply_fail_as_invalid_request(string field, string value)
    {
        var payload = Fixtures.Edit("find_jobs_search.json", o =>
        {
            o[field] = JsonNode.Parse(value);
            o["warning"] = "Some filters were not applied.";
        });
        var fake = new FakeUpworkMcp().RespondWith(payload);
        await using var client = fake.CreateClient();

        var ex = await Assert.ThrowsAsync<UpworkInvalidRequestException>(
            () => client.SearchJobsAsync(new UpworkJobSearchRequest { Title = "MCP" }, Ct));

        Assert.Equal("FILTERS_NOT_APPLIED", ex.ErrorCode);
        Assert.Contains(field, ex.Message);
        Assert.Equal(payload, ex.Raw!.ResponseJson);
    }

    [Fact]
    public async Task Empty_filter_lists_are_not_treated_as_problems()
    {
        var payload = Fixtures.Edit("find_jobs_search.json", o =>
        {
            o["filters_ignored"] = new JsonArray();
            o["filters_rejected"] = new JsonArray();
        });
        var fake = new FakeUpworkMcp().RespondWith(payload);
        await using var client = fake.CreateClient();

        var page = await client.SearchJobsAsync(new UpworkJobSearchRequest { Title = "MCP" }, Ct);

        Assert.Equal(2, page.Jobs.Count);
    }
}
