using System.Net;
using JobScout.Worker.Integrations.Upwork;
using JobScout.Worker.Integrations.Upwork.Models;
using ModelContextProtocol;

namespace JobScout.Tests.Upwork;

public class UpworkErrorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly UpworkJobSearchRequest Search = new() { Title = "MCP" };

    [Fact]
    public async Task Client_error_is_an_invalid_request_with_raw_payload()
    {
        var payload = Fixtures.Load("find_jobs_error_client.json");
        var fake = new FakeUpworkMcp().RespondWith(payload, isError: true);
        await using var client = fake.CreateClient();

        var ex = await Assert.ThrowsAsync<UpworkInvalidRequestException>(() => client.SearchJobsAsync(Search, Ct));

        Assert.Equal(UpworkFailureDisposition.Terminate, ex.Disposition);
        Assert.Equal("CLIENT", ex.ErrorCode);
        Assert.Equal("2cec314b62e0780457108c445243f73d", ex.TraceId);
        Assert.Equal("find_jobs", ex.Tool);
        Assert.Equal("search", ex.Action);
        Assert.Contains("sort=relevance discards the title filter", ex.Message);
        Assert.True(ex.Raw!.IsError);
        Assert.Equal(payload, ex.Raw.ResponseJson);
    }

    [Fact]
    public async Task Status_error_without_is_error_flag_is_still_an_error()
    {
        var fake = new FakeUpworkMcp().RespondWith(Fixtures.Load("find_jobs_error_client.json"), isError: false);
        await using var client = fake.CreateClient();

        var ex = await Assert.ThrowsAsync<UpworkInvalidRequestException>(() => client.SearchJobsAsync(Search, Ct));
        Assert.True(ex.Raw!.IsError);
    }

    [Fact]
    public async Task Job_not_found_on_get_is_skip_item()
    {
        var fake = new FakeUpworkMcp().RespondWith(Fixtures.Load("find_jobs_error_not_found.json"), isError: true);
        await using var client = fake.CreateClient();

        var ex = await Assert.ThrowsAsync<UpworkJobUnavailableException>(() => client.GetJobAsync("1999999999999999999", Ct));

        Assert.Equal(UpworkFailureDisposition.SkipItem, ex.Disposition);
        Assert.Equal("1999999999999999999", ex.JobId);
        Assert.Equal("NOT_FOUND", ex.ErrorCode);
    }

    [Fact]
    public async Task Job_browse_cooldown_is_retry_after_a_minute()
    {
        var fake = new FakeUpworkMcp().RespondWith(
            """{"error_code":"JOB_BROWSE_COOLDOWN","reason":"Too many job opens.","status":"error","trace_id":"t1"}""",
            isError: true);
        await using var client = fake.CreateClient();

        var ex = await Assert.ThrowsAsync<UpworkRateLimitedException>(() => client.GetJobAsync("1", Ct));

        Assert.Equal(UpworkFailureDisposition.RetryAfter, ex.Disposition);
        Assert.Equal(TimeSpan.FromSeconds(60), ex.RetryAfter);
    }

    [Fact]
    public async Task Retry_after_seconds_is_honoured()
    {
        var fake = new FakeUpworkMcp().RespondWith(
            """{"error_code":"RATE_LIMITED","retry_after_seconds":12,"status":"error"}""", isError: true);
        await using var client = fake.CreateClient();

        var ex = await Assert.ThrowsAsync<UpworkRateLimitedException>(() => client.SearchJobsAsync(Search, Ct));

        Assert.Equal(TimeSpan.FromSeconds(12), ex.RetryAfter);
        Assert.Equal(UpworkFailureDisposition.RetryAfter, ex.Disposition);
    }

    [Fact]
    public async Task Auth_error_code_is_pause()
    {
        var fake = new FakeUpworkMcp().RespondWith("""{"error_code":"UNAUTHORIZED","status":"error"}""", isError: true);
        await using var client = fake.CreateClient();

        var ex = await Assert.ThrowsAsync<UpworkAuthRequiredException>(() => client.SearchJobsAsync(Search, Ct));
        Assert.Equal(UpworkFailureDisposition.Pause, ex.Disposition);
    }

    [Fact]
    public async Task Unknown_error_code_is_kept_and_retryable()
    {
        var fake = new FakeUpworkMcp().RespondWith("""{"error_code":"SOMETHING_NEW","reason":"?","status":"error"}""", isError: true);
        await using var client = fake.CreateClient();

        var ex = await Assert.ThrowsAsync<UpworkToolErrorException>(() => client.SearchJobsAsync(Search, Ct));

        Assert.Equal("SOMETHING_NEW", ex.ErrorCode);
        Assert.Equal(UpworkFailureDisposition.Retry, ex.Disposition);
    }

    [Fact]
    public async Task Error_without_json_body_is_retryable_and_keeps_text()
    {
        var fake = new FakeUpworkMcp().RespondWith("upstream exploded", isError: true);
        await using var client = fake.CreateClient();

        var ex = await Assert.ThrowsAsync<UpworkToolErrorException>(() => client.SearchJobsAsync(Search, Ct));

        Assert.Equal("upstream exploded", ex.Raw!.ResponseJson);
        Assert.Contains("upstream exploded", ex.Message);
    }

    [Fact]
    public async Task Http_5xx_is_transient_keeps_request_and_reconnects()
    {
        var fake = new FakeUpworkMcp();
        var calls = 0;
        fake.HandlerAsync = (tool, _, _) => tool.EndsWith("list_accounts", StringComparison.Ordinal)
            ? Task.FromResult(FakeUpworkMcp.Text(Fixtures.Load("list_accounts.json")))
            : ++calls == 1
                ? throw new HttpRequestException("bad gateway", null, HttpStatusCode.BadGateway)
                : Task.FromResult(FakeUpworkMcp.Text(Fixtures.Load("find_jobs_search.json")));
        await using var client = fake.CreateClient();

        var ex = await Assert.ThrowsAsync<UpworkTransientException>(() => client.SearchJobsAsync(Search, Ct));

        Assert.Equal(UpworkFailureDisposition.Retry, ex.Disposition);
        Assert.Equal("HTTP_502", ex.ErrorCode);
        Assert.Null(ex.Raw!.ResponseJson);
        Assert.Equal(UpworkPayloadSource.None, ex.Raw.PayloadSource);
        Assert.Contains("\"title\":\"MCP\"", ex.Raw.RequestJson);

        var page = await client.SearchJobsAsync(Search, Ct);
        Assert.Equal(2, page.Jobs.Count);
        Assert.Equal(2, fake.ConnectCount);
        Assert.Equal(1, fake.DisposeCount);
    }

    [Fact]
    public async Task Http_401_is_auth_required()
    {
        var fake = new FakeUpworkMcp
        {
            HandlerAsync = (_, _, _) => throw new HttpRequestException("unauthorized", null, HttpStatusCode.Unauthorized),
        };
        await using var client = fake.CreateClient(o => o.OrgUid = FakeUpworkMcp.OrgUid);

        var ex = await Assert.ThrowsAsync<UpworkAuthRequiredException>(() => client.SearchJobsAsync(Search, Ct));
        Assert.Equal("HTTP_401", ex.ErrorCode);
    }

    [Fact]
    public async Task Auth_exception_wrapped_by_the_sdk_is_unwrapped()
    {
        var fake = new FakeUpworkMcp
        {
            ConnectException = new McpException("connect failed", new UpworkAuthRequiredException("sign in again")),
        };
        await using var client = fake.CreateClient(o => o.OrgUid = FakeUpworkMcp.OrgUid);

        var ex = await Assert.ThrowsAsync<UpworkAuthRequiredException>(() => client.SearchJobsAsync(Search, Ct));
        Assert.Equal("sign in again", ex.Message);
    }

    [Fact]
    public async Task Network_failure_on_connect_is_transient()
    {
        var fake = new FakeUpworkMcp { ConnectException = new HttpRequestException("no route to host") };
        await using var client = fake.CreateClient(o => o.OrgUid = FakeUpworkMcp.OrgUid);

        var ex = await Assert.ThrowsAsync<UpworkTransientException>(() => client.SearchJobsAsync(Search, Ct));
        Assert.Equal("connect", ex.Tool);
    }

    [Fact]
    public async Task Call_timeout_is_transient()
    {
        var fake = new FakeUpworkMcp
        {
            HandlerAsync = async (_, _, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                throw new InvalidOperationException("unreachable");
            },
        };
        await using var client = fake.CreateClient(o =>
        {
            o.OrgUid = FakeUpworkMcp.OrgUid;
            o.CallTimeout = TimeSpan.FromMilliseconds(100);
        });

        var ex = await Assert.ThrowsAsync<UpworkTransientException>(() => client.SearchJobsAsync(Search, Ct));
        Assert.IsType<TimeoutException>(ex.InnerException);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_wrapped()
    {
        var fake = new FakeUpworkMcp
        {
            HandlerAsync = async (_, _, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                throw new InvalidOperationException("unreachable");
            },
        };
        await using var client = fake.CreateClient(o => o.OrgUid = FakeUpworkMcp.OrgUid);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(100);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SearchJobsAsync(Search, cts.Token));
    }

    [Fact]
    public void Temporal_failure_keeps_retry_semantics()
    {
        var rateLimited = new UpworkRateLimitedException("slow down") { RetryAfter = TimeSpan.FromSeconds(12) }.ToApplicationFailure();
        Assert.False(rateLimited.NonRetryable);
        Assert.Equal(TimeSpan.FromSeconds(12), rateLimited.NextRetryDelay);
        Assert.Equal(nameof(UpworkRateLimitedException), rateLimited.ErrorType);

        var auth = new UpworkAuthRequiredException("sign in").ToApplicationFailure();
        Assert.True(auth.NonRetryable);
        Assert.Equal(nameof(UpworkAuthRequiredException), auth.ErrorType);

        var mismatch = new UpworkContractMismatchException("changed") { Issues = ["$.jobs: missing"] };
        var details = UpworkFailureDetails.From(mismatch);
        Assert.Equal(UpworkFailureDisposition.Terminate, details.Disposition);
        Assert.Equal(["$.jobs: missing"], details.Issues);
        Assert.True(mismatch.ToApplicationFailure().NonRetryable);
    }
}
