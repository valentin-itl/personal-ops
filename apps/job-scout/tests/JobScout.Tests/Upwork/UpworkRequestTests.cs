using System.Text.Json.Nodes;
using JobScout.Worker.Integrations.Upwork;
using JobScout.Worker.Integrations.Upwork.Models;

namespace JobScout.Tests.Upwork;

public class UpworkRequestTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Search_request_is_written_with_upwork_parameter_names_and_values()
    {
        var fake = new FakeUpworkMcp().RespondWith(Fixtures.Load("find_jobs_search.json"));
        await using var client = fake.CreateClient();

        await client.SearchJobsAsync(
            new UpworkJobSearchRequest
            {
                Title = "  .NET developer ",
                Skills = ["C#", "ASP.NET Core"],
                Sort = UpworkJobSort.Recency,
                Limit = 5,
                IncludeFullDetails = true,
                Cursor = "2",
                Filters = new UpworkJobFilters
                {
                    JobType = UpworkJobType.Hourly,
                    ExperienceLevel = UpworkExperienceLevel.EntryLevel,
                    RateMin = 40,
                    RateMax = 90.5m,
                    Workload = UpworkWorkload.PartTime,
                    Duration = UpworkProjectDuration.OneToThreeMonths,
                    VerifiedPaymentOnly = true,
                    ProposalsMax = 10,
                    ClientHiresMin = 0,
                    ClientHiresMax = 0,
                    Location = "Europe",
                },
            },
            Ct);

        var sent = Assert.Single(fake.FindJobsCalls());
        var expected = JsonNode.Parse("""
            {
              "action": "search",
              "org_uid": "1000000000000000001",
              "params": {
                "title": ".NET developer",
                "skills": ["C#", "ASP.NET Core"],
                "job_type": "hourly",
                "experience_level": "entry_level",
                "rate_min": 40,
                "rate_max": 90.5,
                "workload": "part_time",
                "duration": "1_to_3_months",
                "verified_payment_only": true,
                "proposals_max": 10,
                "client_hires_min": 0,
                "client_hires_max": 0,
                "location": "Europe",
                "sort": "recency",
                "limit": 5,
                "include_full_details": true,
                "cursor": "2"
              }
            }
            """);
        Assert.True(JsonNode.DeepEquals(expected, sent), sent.ToJsonString());
    }

    [Fact]
    public async Task Smart_search_request_sends_mode_and_rfc3339_dates()
    {
        var fake = new FakeUpworkMcp().RespondWith(Fixtures.Load("find_jobs_smart_search.json"));
        await using var client = fake.CreateClient();

        await client.SmartSearchJobsAsync(
            new UpworkSmartSearchRequest
            {
                FromDate = new DateTimeOffset(2026, 11, 24, 10, 30, 0, TimeSpan.FromHours(2)),
                Limit = 10,
                Filters = new() { VerifiedPaymentOnly = true },
            },
            Ct);

        var sent = Assert.Single(fake.FindJobsCalls());
        var expected = JsonNode.Parse("""
            {
              "action": "smart_search",
              "org_uid": "1000000000000000001",
              "params": { "mode": "most_recent", "from_date": "2026-11-24T08:30:00Z", "verified_payment_only": true, "limit": 10 }
            }
            """);
        Assert.True(JsonNode.DeepEquals(expected, sent), sent.ToJsonString());
    }

    public static TheoryData<string, UpworkJobSearchRequest> InvalidSearches => new()
    {
        { "title and query", new() { Title = "MCP", Query = "model context protocol" } },
        { "relevance with title", new() { Title = "MCP", Sort = UpworkJobSort.Relevance } },
        { "relevance with category", new() { Query = "x", Sort = UpworkJobSort.Relevance, Filters = new() { Category = "Web" } } },
        { "limit 0", new() { Title = "MCP", Limit = 0 } },
        { "limit 11", new() { Title = "MCP", Limit = 11 } },
        { "6 skills", new() { Skills = ["a", "b", "c", "d", "e", "f"] } },
        { "empty skill", new() { Skills = ["C#", " "] } },
        { "blank title", new() { Title = "  " } },
        { "budget min > max", new() { Filters = new() { BudgetMin = 500, BudgetMax = 100 } } },
        { "budget 0", new() { Filters = new() { BudgetMin = 0 } } },
        { "negative proposals", new() { Filters = new() { ProposalsMax = -1 } } },
        { "unknown job type", new() { Filters = new() { JobType = UpworkJobType.Unknown } } },
        { "undefined enum value", new() { Filters = new() { Workload = (UpworkWorkload)99 } } },
    };

    [Theory]
    [MemberData(nameof(InvalidSearches))]
    public async Task Invalid_search_is_rejected_before_anything_is_sent(string because, UpworkJobSearchRequest request)
    {
        var fake = new FakeUpworkMcp();
        await using var client = fake.CreateClient();

        var ex = await Assert.ThrowsAsync<UpworkInvalidRequestException>(() => client.SearchJobsAsync(request, Ct));

        Assert.Equal(UpworkFailureDisposition.Terminate, ex.Disposition);
        Assert.Equal("search", ex.Action);
        Assert.Null(ex.Raw);
        Assert.Equal(0, fake.ConnectCount);
        Assert.False(string.IsNullOrEmpty(because));
    }

    public static TheoryData<string, UpworkSmartSearchRequest> InvalidSmartSearches => new()
    {
        { "days_posted 0", new() { DaysPosted = 0 } },
        { "days_posted with from_date", new() { DaysPosted = 1, FromDate = DateTimeOffset.UtcNow } },
        { "from after to", new() { FromDate = DateTimeOffset.UtcNow, ToDate = DateTimeOffset.UtcNow.AddDays(-1) } },
        { "dates with best_match", new() { Mode = UpworkSmartSearchMode.BestMatch, DaysPosted = 1 } },
        { "limit 20", new() { Limit = 20 } },
    };

    [Theory]
    [MemberData(nameof(InvalidSmartSearches))]
    public async Task Invalid_smart_search_is_rejected_before_anything_is_sent(string because, UpworkSmartSearchRequest request)
    {
        var fake = new FakeUpworkMcp();
        await using var client = fake.CreateClient();

        var ex = await Assert.ThrowsAsync<UpworkInvalidRequestException>(() => client.SmartSearchJobsAsync(request, Ct));

        Assert.Equal("smart_search", ex.Action);
        Assert.Equal(0, fake.ConnectCount);
        Assert.False(string.IsNullOrEmpty(because));
    }

    [Fact]
    public async Task Empty_job_id_is_rejected()
    {
        var fake = new FakeUpworkMcp();
        await using var client = fake.CreateClient();

        await Assert.ThrowsAsync<UpworkInvalidRequestException>(() => client.GetJobAsync(" ", Ct));
        Assert.Equal(0, fake.ConnectCount);
    }
}
