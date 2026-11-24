using System.Text.Json.Nodes;
using JobScout.Tests.Upwork;
using JobScout.Worker.Integrations.Upwork;
using JobScout.Worker.Persistence.Entities;
using JobScout.Worker.Scanning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Temporalio.Exceptions;
using static JobScout.Tests.Scanning.ScannerHarness;

namespace JobScout.Tests.Scanning;

public class UpworkSourceScannerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task First_scan_stores_jobs_revisions_evaluations_and_raw_exchanges()
    {
        await using var h = new ScannerHarness();
        await h.SeedSourceAsync(SourceHealth.Degraded);
        var firstPage = Fixtures.Load("find_jobs_search.json"); // 2 jobs, cursor "2"
        var lastPage = Page(null, ("3001", "Third job", T0.AddHours(-1)));
        h.ServePages(new() { [""] = firstPage, ["2"] = lastPage });

        var result = await h.ScanAsync();

        Assert.Equal(3, result.NewJobs);
        Assert.Equal(0, result.UpdatedJobs);

        await using var db = h.NewDb();
        var scan = await db.SourceScans.SingleAsync(Ct);
        Assert.Equal(SourceScanStatus.Completed, scan.Status);
        Assert.Equal((3, 3, 0, 0, 3), (scan.ObservedCount, scan.CreatedCount, scan.UpdatedCount, scan.UnchangedCount, scan.EvaluationsQueuedCount));
        Assert.Equal("mcp", scan.SearchProfileId);

        var jobs = await db.Jobs.OrderBy(j => j.ExternalId).ToListAsync(Ct);
        Assert.Equal(["2123839976608365666", "2124589079512096981", "3001"], jobs.Select(j => j.ExternalId));
        Assert.All(jobs, j => Assert.Equal(JobAvailability.Open, j.Availability));
        Assert.All(jobs, j => Assert.Equal(64, j.CurrentFingerprint!.Length));

        // Each revision holds the job's JSON exactly as Upwork returned it.
        var originals = JsonNode.Parse(firstPage)!["jobs"]!.AsArray();
        var revision = await db.JobRevisions.Include(r => r.Job).SingleAsync(r => r.Job.ExternalId == "2124589079512096981", Ct);
        Assert.Contains(revision.PayloadJson, firstPage);
        Assert.True(JsonNode.DeepEquals(originals[0], JsonNode.Parse(revision.PayloadJson)));
        Assert.Equal(revision.Job.CurrentFingerprint, revision.Fingerprint);

        var outbox = await db.EvaluationOutbox.ToListAsync(Ct);
        Assert.Equal(3, outbox.Count);
        Assert.All(outbox, o => Assert.Equal(EvaluationDispatchStatus.Pending, o.Status));
        Assert.Contains(outbox, o => o.EvaluationKey == $"{h.SourceId:N}/2124589079512096981/{revision.Fingerprint}");

        Assert.DoesNotContain(h.Logs.Entries, e => e.Level >= LogLevel.Warning);

        var source = await db.Sources.SingleAsync(Ct);
        Assert.Equal(SourceHealth.Healthy, source.Health);
        Assert.Null(source.ErrorMessage);
    }

    [Fact]
    public async Task Rescan_of_unchanged_jobs_only_moves_last_seen()
    {
        await using var h = new ScannerHarness();
        await h.SeedSourceAsync();
        h.ServePages(new() { [""] = Page(null, ("a", "Job A", T0.AddMinutes(-10)), ("b", "Job B", T0.AddMinutes(-20))) });
        await h.ScanAsync();

        h.Time.Now = T0.AddMinutes(30);
        var result = await h.ScanAsync();

        Assert.Equal((0, 0), (result.NewJobs, result.UpdatedJobs));
        await using var db = h.NewDb();
        var second = await db.SourceScans.OrderBy(s => s.StartedAtUtc).LastAsync(Ct);
        Assert.Equal(2, second.UnchangedCount);
        Assert.Equal(0, second.EvaluationsQueuedCount);
        Assert.Equal(2, await db.JobRevisions.CountAsync(Ct));
        Assert.All(await db.Jobs.ToListAsync(Ct), j => Assert.Equal(T0.AddMinutes(30).UtcDateTime, j.LastSeenAtUtc));
        Assert.All(await db.Jobs.ToListAsync(Ct), j => Assert.Equal(T0.UtcDateTime, j.FirstSeenAtUtc));
    }

    [Fact]
    public async Task Changed_content_creates_a_revision_and_an_evaluation()
    {
        await using var h = new ScannerHarness();
        await h.SeedSourceAsync();
        h.ServePages(new() { [""] = Page(null, ("a", "Job A", T0.AddMinutes(-10))) });
        await h.ScanAsync();

        h.Time.Now = T0.AddMinutes(30);
        h.ServePages(new() { [""] = Page(null, ("a", "Job A (budget raised)", T0.AddMinutes(-10))) });
        var result = await h.ScanAsync();

        Assert.Equal((0, 1), (result.NewJobs, result.UpdatedJobs));
        await using var db = h.NewDb();
        var job = await db.Jobs.Include(j => j.Revisions).SingleAsync(Ct);
        Assert.Equal(2, job.Revisions.Count);
        Assert.Equal(job.CurrentFingerprint, job.Revisions.OrderBy(r => r.CreatedAtUtc).Last().Fingerprint);
        Assert.Equal(2, await db.EvaluationOutbox.CountAsync(Ct));
    }

    [Fact]
    public async Task Content_changing_back_reuses_the_earlier_revision()
    {
        await using var h = new ScannerHarness();
        await h.SeedSourceAsync();
        foreach (var (minutes, title) in new[] { (0, "Original"), (30, "Edited"), (60, "Original") })
        {
            h.Time.Now = T0.AddMinutes(minutes);
            h.ServePages(new() { [""] = Page(null, ("a", title, T0.AddMinutes(-10))) });
            await h.ScanAsync();
        }

        await using var db = h.NewDb();
        Assert.Equal(2, await db.JobRevisions.CountAsync(Ct));
        Assert.Equal(2, await db.EvaluationOutbox.CountAsync(Ct));
        var last = await db.SourceScans.OrderBy(s => s.StartedAtUtc).LastAsync(Ct);
        Assert.Equal((1, 0), (last.UpdatedCount, last.EvaluationsQueuedCount));
    }

    [Fact]
    public async Task Job_repeated_on_the_next_page_is_counted_once()
    {
        await using var h = new ScannerHarness();
        await h.SeedSourceAsync();
        h.ServePages(new()
        {
            [""] = Page("p2", ("a", "Job A", T0.AddMinutes(-1)), ("b", "Job B", T0.AddMinutes(-2))),
            ["p2"] = Page(null, ("b", "Job B", T0.AddMinutes(-2)), ("c", "Job C", T0.AddMinutes(-3))),
        });

        var result = await h.ScanAsync();

        Assert.Equal(3, result.NewJobs);
        await using var db = h.NewDb();
        Assert.Equal(3, (await db.SourceScans.SingleAsync(Ct)).ObservedCount);
    }

    [Fact]
    public async Task Window_starts_at_the_last_completed_scan_minus_overlap()
    {
        await using var h = new ScannerHarness();
        await h.SeedSourceAsync();
        h.ServePages(new() { [""] = Page(null, ("a", "Job A", T0.AddMinutes(-5)), ("old", "Too old", T0.AddDays(-3))) });

        await h.ScanAsync("recommended");
        h.Time.Now = T0.AddMinutes(30);
        await h.ScanAsync("recommended");

        var fromDates = h.Fake.FindJobsCalls().Select(c => (string?)c["params"]!["from_date"]).ToList();
        Assert.Equal(["2026-11-22T12:00:00Z", "2026-11-24T11:00:00Z"], fromDates); // MaxLookback 2 days, then T0 - 1h overlap
        Assert.All(h.Fake.FindJobsCalls(), c => Assert.Equal("most_recent", (string?)c["params"]!["mode"]));

        await using var db = h.NewDb();
        Assert.Equal(["a"], await db.Jobs.Select(j => j.ExternalId).ToListAsync(Ct)); // older than the window: not stored
    }

    [Fact]
    public async Task Contract_mismatch_keeps_earlier_pages_and_logs_the_received_payload()
    {
        await using var h = new ScannerHarness();
        await h.SeedSourceAsync();
        var broken = """{"status":"ok","hasMore":false,"trace_id":"trace-broken","items":[]}""";
        h.ServePages(new() { [""] = Page("p2", ("a", "Job A", T0.AddMinutes(-1))), ["p2"] = broken });

        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(() => h.ScanAsync());

        Assert.Equal(nameof(UpworkContractMismatchException), failure.ErrorType);
        Assert.True(failure.NonRetryable);

        await using var db = h.NewDb();
        var scan = await db.SourceScans.SingleAsync(Ct);
        Assert.Equal(SourceScanStatus.Failed, scan.Status);
        Assert.NotNull(scan.CompletedAtUtc);
        Assert.Contains("$.jobs", scan.ErrorMessage);
        Assert.Equal(1, scan.CreatedCount);
        Assert.Equal(["a"], await db.Jobs.Select(j => j.ExternalId).ToListAsync(Ct));

        var logged = Assert.Single(h.Logs.Entries, e => e.Level == LogLevel.Error);
        Assert.Equal(broken, logged.Get("ResponseJson")); // verbatim, ready to export
        Assert.Equal("trace-broken", logged.Get("TraceId"));
        Assert.Contains("$.jobs: required array is missing", (IEnumerable<string>)logged.Get("Issues")!);
        Assert.Contains("\"cursor\":\"p2\"", (string)logged.Get("RequestJson")!);
        Assert.IsType<UpworkContractMismatchException>(logged.Exception);

        var source = await db.Sources.SingleAsync(Ct);
        Assert.Equal(SourceHealth.ContractMismatch, source.Health);
        Assert.NotNull(source.ErrorMessage);
    }

    [Fact]
    public async Task Throttling_is_retryable_with_the_server_delay()
    {
        await using var h = new ScannerHarness();
        await h.SeedSourceAsync();
        h.Fake.RespondWith("""{"error_code":"RATE_LIMITED","retry_after_seconds":30,"status":"error","trace_id":"t-429"}""", isError: true);

        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(() => h.ScanAsync());

        Assert.False(failure.NonRetryable);
        Assert.Equal(TimeSpan.FromSeconds(30), failure.NextRetryDelay);
        await using var db = h.NewDb();
        Assert.Equal(SourceHealth.Degraded, (await db.Sources.SingleAsync(Ct)).Health);
        var logged = Assert.Single(h.Logs.Entries, e => e.Level == LogLevel.Warning); // retryable: warning, not error
        Assert.Contains("RATE_LIMITED", (string)logged.Get("ResponseJson")!);
        Assert.Equal("t-429", logged.Get("TraceId"));
    }

    [Fact]
    public async Task Sign_in_problem_pauses_and_marks_the_source()
    {
        await using var h = new ScannerHarness();
        await h.SeedSourceAsync();
        h.Fake.ConnectException = new UpworkAuthRequiredException("Upwork sign-in is required");

        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(() => h.ScanAsync());

        Assert.Equal(nameof(UpworkAuthRequiredException), failure.ErrorType);
        Assert.True(failure.NonRetryable);
        await using var db = h.NewDb();
        Assert.Equal(SourceHealth.AuthRequired, (await db.Sources.SingleAsync(Ct)).Health);
        Assert.Equal(SourceScanStatus.Failed, (await db.SourceScans.SingleAsync(Ct)).Status);
        Assert.Null(Assert.Single(h.Logs.Entries, e => e.Level == LogLevel.Error).Get("ResponseJson")); // nothing was sent
    }

    [Fact]
    public async Task Refused_request_fails_without_blaming_the_source()
    {
        await using var h = new ScannerHarness();
        await h.SeedSourceAsync();
        h.Fake.RespondWith(Fixtures.Load("find_jobs_error_client.json"), isError: true);

        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(() => h.ScanAsync());

        Assert.Equal(nameof(UpworkInvalidRequestException), failure.ErrorType);
        await using var db = h.NewDb();
        Assert.Equal(SourceHealth.Healthy, (await db.Sources.SingleAsync(Ct)).Health);
        Assert.Equal(
            Fixtures.Load("find_jobs_error_client.json"),
            Assert.Single(h.Logs.Entries, e => e.Level == LogLevel.Error).Get("ResponseJson"));
    }

    [Theory]
    [InlineData("not-a-guid", "mcp", SourceScanFailureTypes.InvalidSourceId)]
    [InlineData("00000000-0000-0000-0000-000000000001", "mcp", SourceScanFailureTypes.SourceNotFound)]
    [InlineData(null, "nope", SourceScanFailureTypes.SearchProfileNotFound)]
    [InlineData(null, "broken", SourceScanFailureTypes.SearchProfileInvalid)]
    public async Task Configuration_problems_are_non_retryable(string? sourceId, string profile, string errorType)
    {
        await using var h = new ScannerHarness();
        await h.SeedSourceAsync();
        h.Options.SearchProfiles["broken"] = new UpworkSearchProfile(); // neither Search nor SmartSearch

        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(
            () => h.ScanAsync(sourceId ?? h.SourceId.ToString(), profile));

        Assert.Equal(errorType, failure.ErrorType);
        Assert.True(failure.NonRetryable);
        Assert.Equal(0, h.Fake.ConnectCount);
        await using var db = h.NewDb();
        Assert.Empty(await db.SourceScans.ToListAsync(Ct));
    }

    [Fact]
    public async Task Disabled_source_is_skipped()
    {
        await using var h = new ScannerHarness();
        await h.SeedSourceAsync(SourceHealth.Disabled);

        var result = await h.ScanAsync();

        Assert.Equal((0, 0), (result.NewJobs, result.UpdatedJobs));
        Assert.Equal(0, h.Fake.ConnectCount);
        await using var db = h.NewDb();
        Assert.Empty(await db.SourceScans.ToListAsync(Ct));
    }

    [Fact]
    public async Task Profile_lookup_ignores_case_and_stores_the_configured_id()
    {
        await using var h = new ScannerHarness();
        await h.SeedSourceAsync();
        h.ServePages(new() { [""] = Page(null) });

        await h.ScanAsync("MCP");

        await using var db = h.NewDb();
        Assert.Equal("mcp", (await db.SourceScans.SingleAsync(Ct)).SearchProfileId);
    }
}
