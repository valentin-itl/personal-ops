using JobScout.Worker.Integrations.Upwork.Models;
using JobScout.Worker.Scanning;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace JobScout.Tests.Scanning;

public class ScanConfigurationAndFingerprintTests
{
    [Fact]
    public void Shipped_appsettings_profiles_bind_to_the_typed_requests()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"))
            .Build();
        using var provider = new ServiceCollection().AddJobScanning(configuration).BuildServiceProvider();

        var profiles = provider.GetRequiredService<IOptions<JobScoutScanOptions>>().Value.SearchProfiles;

        var recommended = profiles["upwork-recommended"];
        Assert.Null(recommended.Search);
        Assert.Equal(UpworkSmartSearchMode.MostRecent, recommended.SmartSearch!.Mode);
        Assert.True(recommended.SmartSearch.Filters.VerifiedPaymentOnly);
        Assert.Equal(TimeSpan.FromDays(2), recommended.MaxLookback);
        Assert.Empty(recommended.Validate());

        var mcp = profiles["upwork-mcp"];
        Assert.Equal("MCP", mcp.Search!.Title);
        Assert.Equal(UpworkJobSort.Recency, mcp.Search.Sort);
        Assert.True(mcp.Search.IncludeFullDetails);
        Assert.Equal(3, mcp.MaxPages);
        Assert.Empty(mcp.Validate());
    }

    [Fact]
    public void Lists_enums_and_amounts_bind_from_configuration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["JobScout:SearchProfiles:dotnet:Search:Title"] = ".NET",
                ["JobScout:SearchProfiles:dotnet:Search:Skills:0"] = "C#",
                ["JobScout:SearchProfiles:dotnet:Search:Skills:1"] = "ASP.NET Core",
                ["JobScout:SearchProfiles:dotnet:Search:Filters:JobType"] = "Hourly",
                ["JobScout:SearchProfiles:dotnet:Search:Filters:Duration"] = "OneToThreeMonths",
                ["JobScout:SearchProfiles:dotnet:Search:Filters:RateMin"] = "45.5",
                ["JobScout:SearchProfiles:dotnet:Overlap"] = "00:30:00",
            })
            .Build();
        using var provider = new ServiceCollection().AddJobScanning(configuration).BuildServiceProvider();

        var profile = provider.GetRequiredService<IOptions<JobScoutScanOptions>>().Value.SearchProfiles["DOTNET"];

        Assert.Equal(["C#", "ASP.NET Core"], profile.Search!.Skills);
        Assert.Equal(UpworkJobType.Hourly, profile.Search.Filters.JobType);
        Assert.Equal(UpworkProjectDuration.OneToThreeMonths, profile.Search.Filters.Duration);
        Assert.Equal(45.5m, profile.Search.Filters.RateMin);
        Assert.Equal(TimeSpan.FromMinutes(30), profile.Overlap);
    }

    private static readonly UpworkJobSummary Job = new()
    {
        Id = "1",
        Title = "Senior .NET engineer",
        Url = "https://www.upwork.com/jobs/~021",
        PublishedAt = DateTimeOffset.Parse("2026-11-24T08:00:00Z"),
        DescriptionSnippet = "Build an MCP server in C#. ...",
        JobType = UpworkJobType.Hourly,
        ExperienceLevel = UpworkExperienceLevel.Expert,
        Skills = ["C#", "MCP"],
        RawJson = "{}",
    };

    [Fact]
    public void Fingerprint_ignores_counters_and_format_differences_between_actions()
    {
        var seenViaSmartSearch = Job with
        {
            Description = "Build an MCP server in C#. Full text only search with full details returns.",
            Engagement = "30+ hrs/week",
            Budget = new UpworkBudget { Raw = "$40.00–$80.00/hr", Min = 40, Max = 80, IsHourly = true },
            ProposalsTier = "20 to 50",
            Applied = true,
            Client = new UpworkClientSummary { TotalSpentAmount = 1000 },
            Activity = new UpworkJobActivity { TotalHired = 1 },
            Skills = ["MCP", "C#"],
            RawJson = """{"different":"payload"}""",
        };

        var fingerprint = JobFingerprint.Compute(Job);

        Assert.Equal(fingerprint, JobFingerprint.Compute(seenViaSmartSearch));
        Assert.Matches("^[0-9a-f]{64}$", fingerprint);
    }

    [Fact]
    public void Fingerprint_changes_with_the_content()
    {
        var fingerprint = JobFingerprint.Compute(Job);

        Assert.NotEqual(fingerprint, JobFingerprint.Compute(Job with { Title = "Lead .NET engineer" }));
        Assert.NotEqual(fingerprint, JobFingerprint.Compute(Job with { DescriptionSnippet = "Build two MCP servers. ..." }));
        Assert.NotEqual(fingerprint, JobFingerprint.Compute(Job with { Skills = ["C#"] }));
        Assert.NotEqual(fingerprint, JobFingerprint.Compute(Job with { ExperienceLevel = UpworkExperienceLevel.Intermediate }));

        var fixedJob = Job with { JobType = UpworkJobType.Fixed, Budget = new UpworkBudget { Raw = "500.00", Min = 500, Max = 500 } };
        Assert.NotEqual(
            JobFingerprint.Compute(fixedJob),
            JobFingerprint.Compute(fixedJob with { Budget = new UpworkBudget { Raw = "800.00", Min = 800, Max = 800 } }));
    }
}
