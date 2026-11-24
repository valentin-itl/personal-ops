using System.Text.Json;
using System.Text.Json.Nodes;
using JobScout.Worker.Integrations.Upwork;
using JobScout.Worker.Integrations.Upwork.Models;

namespace JobScout.Tests.Upwork;

public class UpworkParsingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Search_maps_jobs_client_activity_and_paging()
    {
        var fake = new FakeUpworkMcp().RespondWith(Fixtures.Load("find_jobs_search.json"));
        await using var client = fake.CreateClient();

        var page = await client.SearchJobsAsync(new UpworkJobSearchRequest { Title = "MCP" }, Ct);

        Assert.Equal(2, page.Jobs.Count);
        Assert.Equal("2", page.NextCursor);
        Assert.True(page.HasMore);
        Assert.False(page.Diagnostics.HasDrift);

        var fixedJob = page.Jobs[0];
        Assert.Equal("2124589079512096981", fixedJob.Id);
        Assert.Equal("AI GTM Engineer | Claude, MCP, n8n & Revenue Automation", fixedJob.Title);
        Assert.Equal(DateTimeOffset.Parse("2026-11-22T19:41:29.831Z"), fixedJob.PublishedAt);
        Assert.Equal(DateTimeOffset.Parse("2026-11-22T19:41:24.643Z"), fixedJob.CreatedAt);
        Assert.Equal(UpworkJobType.Fixed, fixedJob.JobType);
        Assert.Equal(UpworkExperienceLevel.Intermediate, fixedJob.ExperienceLevel);
        Assert.Equal(new UpworkBudget { Raw = "10.00", Min = 10m, Max = 10m, IsHourly = false }, fixedJob.Budget);
        Assert.Equal(["Automation", "n8n"], fixedJob.Skills);
        Assert.Equal("20 to 50", fixedJob.ProposalsTier);
        Assert.Equal(290m, fixedJob.Client!.TotalSpentAmount);
        Assert.Equal(28, fixedJob.Client.TotalPostedJobs);
        Assert.Equal(5m, fixedJob.Client.Rating);
        Assert.Equal(1, fixedJob.Activity!.TotalHired);
        Assert.StartsWith("We need an engineer", fixedJob.DescriptionSnippet);
        Assert.DoesNotContain("untrusted_participant_content", fixedJob.DescriptionSnippet);

        var hourlyJob = page.Jobs[1];
        Assert.Equal(UpworkJobType.Hourly, hourlyJob.JobType);
        Assert.Equal(UpworkExperienceLevel.Expert, hourlyJob.ExperienceLevel);
        Assert.Equal("FULL_TIME", hourlyJob.Engagement);
        Assert.Null(hourlyJob.Budget);
        Assert.Equal(210479.18m, hourlyJob.Client!.TotalSpentAmount);
        Assert.Equal(7, hourlyJob.Activity!.InvitedToInterview);
    }

    [Fact]
    public async Task Search_retains_the_original_request_and_response_json()
    {
        var payload = Fixtures.Load("find_jobs_search.json");
        var fake = new FakeUpworkMcp().RespondWith(payload);
        await using var client = fake.CreateClient();

        var page = await client.SearchJobsAsync(
            new UpworkJobSearchRequest { Title = "MCP", Sort = UpworkJobSort.Recency, Filters = new() { VerifiedPaymentOnly = true } },
            Ct);

        Assert.Equal(payload, page.Raw.ResponseJson);
        Assert.Equal(UpworkPayloadSource.TextContent, page.Raw.PayloadSource);
        Assert.Equal("find_jobs", page.Raw.Tool);
        Assert.Equal("search", page.Raw.Action);
        Assert.Equal("a5d83b0665ee9056956097fec402a153", page.Raw.TraceId);
        Assert.False(page.Raw.IsError);
        Assert.True(page.Raw.CompletedAt >= page.Raw.StartedAt);

        var expectedRequest = JsonNode.Parse(
            """{"action":"search","org_uid":"1000000000000000001","params":{"title":"MCP","verified_payment_only":true,"sort":"recency","limit":10}}""");
        Assert.True(JsonNode.DeepEquals(expectedRequest, JsonNode.Parse(page.Raw.RequestJson)), page.Raw.RequestJson);
        // What was recorded is exactly what was sent.
        Assert.Equal(fake.Calls.Last().ArgumentsJson, page.Raw.RequestJson);

        var originalJobs = JsonNode.Parse(payload)!["jobs"]!.AsArray();
        for (var i = 0; i < page.Jobs.Count; i++)
        {
            Assert.Contains(page.Jobs[i].RawJson, payload); // a verbatim slice, not a re-serialization
            Assert.True(JsonNode.DeepEquals(originalJobs[i], JsonNode.Parse(page.Jobs[i].RawJson)));
        }
    }

    [Fact]
    public async Task Structured_content_is_parsed_and_retained()
    {
        var payload = Fixtures.Load("find_jobs_search.json");
        var fake = new FakeUpworkMcp().RespondByAction((_, _) => FakeUpworkMcp.Structured(payload));
        await using var client = fake.CreateClient();

        var page = await client.SearchJobsAsync(new UpworkJobSearchRequest { Title = "MCP" }, Ct);

        Assert.Equal(UpworkPayloadSource.StructuredContent, page.Raw.PayloadSource);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(payload), JsonNode.Parse(page.Raw.ResponseJson!)));
        Assert.Equal(2, page.Jobs.Count);
    }

    [Fact]
    public async Task Smart_search_normalizes_its_different_field_formats()
    {
        var fake = new FakeUpworkMcp().RespondWith(Fixtures.Load("find_jobs_smart_search.json"));
        await using var client = fake.CreateClient();

        var page = await client.SmartSearchJobsAsync(new UpworkSmartSearchRequest { DaysPosted = 1 }, Ct);

        Assert.Equal("most_recent", page.Mode);
        Assert.Equal(29, page.TotalMatching);
        Assert.Equal("t:1795504976473", page.NextCursor);
        Assert.True(page.HasMore);
        Assert.False(page.Diagnostics.HasDrift, string.Join("; ", page.Diagnostics.Warnings.Concat(page.Diagnostics.UnknownFields)));

        var job = page.Jobs[0];
        Assert.Equal(UpworkExperienceLevel.Intermediate, job.ExperienceLevel); // "Intermediate" here, "intermediate" in search
        Assert.Equal(new UpworkBudget { Raw = "$12.00–$28.00/hr", Min = 12m, Max = 28m, IsHourly = true }, job.Budget);
        Assert.Equal(224, job.Client!.TotalHires);
        Assert.Null(job.Client.TotalPostedJobs);
        Assert.Equal("platform default range", job.HourlyBudgetType);
        Assert.Equal("Less than 30 hrs/week", job.Engagement);
    }

    [Fact]
    public async Task Get_maps_an_hourly_job()
    {
        var payload = Fixtures.Load("find_jobs_get_hourly.json");
        var fake = new FakeUpworkMcp().RespondWith(payload);
        await using var client = fake.CreateClient();

        var job = await client.GetJobAsync("2125150758487828616", Ct);

        Assert.Equal("2125150758487828616", job.Id);
        Assert.Equal("Claude Usage in Development", job.Title);
        Assert.Equal("ACTIVE", job.WorkflowStatus);
        Assert.True(job.IsActive);
        Assert.Equal(UpworkJobType.Hourly, job.JobType);
        Assert.Equal(UpworkExperienceLevel.Intermediate, job.ExperienceLevel);
        Assert.Equal(12m, job.HourlyBudgetMin);
        Assert.Equal(28m, job.HourlyBudgetMax);
        Assert.Equal("PART_TIME", job.EngagementType);
        Assert.Equal("Web, Mobile & Software Dev", job.Category);
        Assert.Equal("Web Development", job.Subcategory);
        Assert.Equal("Norway", job.ClientCountry);
        Assert.Equal("Europe/Berlin", job.ClientTimezone);
        Assert.Equal(66m, job.ClientRecord!.HireRatePercent);
        Assert.Equal(26522.37m, job.ClientRecord.SpendTotalAmount);
        Assert.Equal("Any", job.PreferredQualifications!.EnglishProficiency);
        Assert.Equal(["Are you available for a call this week?"], job.ScreeningQuestions);
        Assert.Equal(20, job.ConnectsCost);
        Assert.False(job.CanApply);
        Assert.Equal(1, job.Activity!.InvitedToInterview);
        Assert.Equal("Freelancer Basic", job.Plan);
        Assert.Equal(payload, job.Raw.ResponseJson);
        Assert.False(job.Diagnostics.HasDrift, string.Join("; ", job.Diagnostics.Warnings.Concat(job.Diagnostics.UnknownFields)));

        var request = JsonNode.Parse(job.Raw.RequestJson)!;
        Assert.Equal("get", (string?)request["action"]);
        Assert.Equal("2125150758487828616", (string?)request["params"]!["id"]);
    }

    [Fact]
    public async Task Get_maps_a_fixed_price_job()
    {
        var fake = new FakeUpworkMcp().RespondWith(Fixtures.Load("find_jobs_get_fixed.json"));
        await using var client = fake.CreateClient();

        var job = await client.GetJobAsync("2124589079512096981", Ct);

        Assert.Equal(UpworkJobType.Fixed, job.JobType);
        Assert.Equal(10.0m, job.FixedPriceAmount);
        Assert.Equal("USD", job.FixedPriceCurrency);
        Assert.Null(job.HourlyBudgetMin);
        Assert.Equal("OR", job.ClientState);
        Assert.False(job.Diagnostics.HasDrift, string.Join("; ", job.Diagnostics.Warnings.Concat(job.Diagnostics.UnknownFields)));
    }

    [Fact]
    public async Task List_accounts_is_typed_and_keeps_raw_json()
    {
        var fake = new FakeUpworkMcp().RespondWith("{}");
        await using var client = fake.CreateClient();

        var list = await client.ListAccountsAsync(Ct);

        var account = Assert.Single(list.Accounts);
        Assert.Equal(FakeUpworkMcp.OrgUid, account.OrgUid);
        Assert.Equal("TALENT", account.Role);
        Assert.Equal(Fixtures.Load("list_accounts.json"), list.Raw.ResponseJson);
        Assert.Null(list.Raw.Action);
    }

    [Fact]
    public async Task Models_round_trip_through_json()
    {
        var fake = new FakeUpworkMcp().RespondByAction((action, _) => FakeUpworkMcp.Text(Fixtures.Load(
            action == "get" ? "find_jobs_get_hourly.json" : "find_jobs_search.json")));
        await using var client = fake.CreateClient();

        var page = await client.SearchJobsAsync(new UpworkJobSearchRequest { Title = "MCP" }, Ct);
        var details = await client.GetJobAsync("2125150758487828616", Ct);
        var request = new UpworkJobSearchRequest
        {
            Title = "MCP",
            Skills = ["C#"],
            Sort = UpworkJobSort.Recency,
            Filters = new() { Duration = UpworkProjectDuration.OneToThreeMonths, BudgetMin = 500 },
        };

        AssertRoundTrip(page);
        AssertRoundTrip(details);
        AssertRoundTrip(request);
    }

    private static void AssertRoundTrip<T>(T value)
    {
        var json = JsonSerializer.Serialize(value, UpworkJson.Options);
        var copy = JsonSerializer.Deserialize<T>(json, UpworkJson.Options);
        Assert.Equal(json, JsonSerializer.Serialize(copy, UpworkJson.Options));
    }
}
