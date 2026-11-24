using JobScout.Worker.Integrations.Upwork;
using JobScout.Worker.Integrations.Upwork.Models;

namespace JobScout.Tests.Upwork;

public class UpworkPagingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Now = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);

    private static readonly UpworkJobSearchRequest Recent = new() { Title = "developer", Sort = UpworkJobSort.Recency };

    private static FakeUpworkMcp ServePages(Dictionary<string, string> pagesByCursor) =>
        new FakeUpworkMcp().RespondByAction((_, p) =>
        {
            var cursor = p.TryGetProperty("cursor", out var c) ? c.GetString()! : "";
            return FakeUpworkMcp.Text(pagesByCursor[cursor]);
        });

    private static async Task<List<UpworkJobSearchPage>> Collect(IAsyncEnumerable<UpworkJobSearchPage> pages)
    {
        var list = new List<UpworkJobSearchPage>();
        await foreach (var page in pages)
            list.Add(page);
        return list;
    }

    [Fact]
    public async Task Follows_cursors_until_the_last_page()
    {
        var fake = ServePages(new()
        {
            [""] = Fixtures.Page("p2", true, Now, Now.AddMinutes(-5)),
            ["p2"] = Fixtures.Page(null, false, Now.AddMinutes(-10)),
        });
        await using var client = fake.CreateClient();

        var pages = await Collect(client.SearchJobPagesAsync(Recent, cancellationToken: Ct));

        Assert.Equal(2, pages.Count);
        Assert.Equal(3, pages.Sum(p => p.Jobs.Count));
        Assert.All(pages, p => Assert.NotNull(p.Raw.ResponseJson)); // each page keeps its own raw JSON
        Assert.Equal("p2", (string?)fake.FindJobsCalls().Last()["params"]!["cursor"]);
    }

    [Fact]
    public async Task Stops_at_max_pages()
    {
        var fake = ServePages(new()
        {
            [""] = Fixtures.Page("p2", true, Now),
            ["p2"] = Fixtures.Page("p3", true, Now),
        });
        await using var client = fake.CreateClient();

        var pages = await Collect(client.SearchJobPagesAsync(Recent, new UpworkPaging { MaxPages = 2 }, Ct));

        Assert.Equal(2, pages.Count);
    }

    [Fact]
    public async Task Stops_after_the_first_page_older_than_the_cutoff()
    {
        var fake = ServePages(new()
        {
            [""] = Fixtures.Page("p2", true, Now, Now.AddHours(-1)),
            ["p2"] = Fixtures.Page("p3", true, Now.AddHours(-3), Now.AddHours(-4)),
            ["p3"] = Fixtures.Page(null, false, Now.AddHours(-5)),
        });
        await using var client = fake.CreateClient();

        var pages = await Collect(client.SearchJobPagesAsync(
            Recent, new UpworkPaging { PublishedAfter = Now.AddHours(-2) }, Ct));

        Assert.Equal(2, pages.Count);
    }

    [Fact]
    public async Task Repeated_cursor_is_a_contract_mismatch()
    {
        var fake = ServePages(new()
        {
            [""] = Fixtures.Page("p2", true, Now),
            ["p2"] = Fixtures.Page("p2", true, Now),
        });
        await using var client = fake.CreateClient();

        var ex = await Assert.ThrowsAsync<UpworkContractMismatchException>(
            () => Collect(client.SearchJobPagesAsync(Recent, cancellationToken: Ct)));

        Assert.Contains(ex.Issues, i => i.Contains("was returned before", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cutoff_needs_newest_first_order()
    {
        var fake = new FakeUpworkMcp();
        await using var client = fake.CreateClient();
        var paging = new UpworkPaging { PublishedAfter = Now };

        Assert.Throws<UpworkInvalidRequestException>(
            () => client.SearchJobPagesAsync(Recent with { Sort = UpworkJobSort.ClientRating }, paging, Ct));
        Assert.Throws<UpworkInvalidRequestException>(
            () => client.SearchJobPagesAsync(new UpworkJobSearchRequest { Query = "mcp" }, paging, Ct));
        Assert.Throws<UpworkInvalidRequestException>(
            () => client.SmartSearchJobPagesAsync(new UpworkSmartSearchRequest { Mode = UpworkSmartSearchMode.BestMatch }, paging, Ct));
        Assert.Equal(0, fake.ConnectCount);
    }

    [Fact]
    public async Task Smart_search_pages_follow_next_cursor()
    {
        var fake = ServePages(new()
        {
            [""] = Fixtures.Load("find_jobs_smart_search.json"),
            ["t:1795504976473"] = Fixtures.Page(null, false, Now.AddDays(-1)),
        });
        await using var client = fake.CreateClient();

        var pages = await Collect(client.SmartSearchJobPagesAsync(new UpworkSmartSearchRequest { DaysPosted = 2 }, cancellationToken: Ct));

        Assert.Equal(2, pages.Count);
        Assert.Equal("t:1795504976473", (string?)fake.FindJobsCalls().Last()["params"]!["cursor"]);
    }
}
