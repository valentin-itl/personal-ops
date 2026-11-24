using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using JobScout.Worker.Integrations.Upwork.Models;
using JobScout.Worker.Integrations.Upwork.Parsing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;

namespace JobScout.Worker.Integrations.Upwork;

/// <summary>
/// Typed access to the Upwork MCP tools job-scout needs. Every response carries the original exchange
/// (<see cref="UpworkToolResponse.Raw"/>) and every failure is an <see cref="UpworkMcpException"/> whose
/// <see cref="UpworkMcpException.Disposition"/> says whether to retry, skip, pause or stop.
/// The client never retries or sleeps on its own; the caller (Temporal) owns retries.
/// Caller cancellation surfaces as <see cref="OperationCanceledException"/>.
/// </summary>
public interface IUpworkMcpClient : IAsyncDisposable
{
    /// <summary>Connects if needed and returns the result of checking the server's tools.</summary>
    Task<UpworkToolCatalog> VerifyToolsAsync(CancellationToken cancellationToken = default);

    Task<UpworkAccountList> ListAccountsAsync(CancellationToken cancellationToken = default);

    /// <summary>One page of <c>find_jobs search</c>.</summary>
    Task<UpworkJobSearchPage> SearchJobsAsync(UpworkJobSearchRequest request, CancellationToken cancellationToken = default);

    /// <summary>One page of <c>find_jobs smart_search</c>.</summary>
    Task<UpworkJobSearchPage> SmartSearchJobsAsync(UpworkSmartSearchRequest request, CancellationToken cancellationToken = default);

    /// <summary><c>find_jobs get</c>. Throws <see cref="UpworkJobUnavailableException"/> for a job that cannot be opened.</summary>
    Task<UpworkJobDetails> GetJobAsync(string jobId, CancellationToken cancellationToken = default);

    /// <summary>Follows cursors for <c>search</c>, yielding whole pages (each with its own raw JSON).</summary>
    IAsyncEnumerable<UpworkJobSearchPage> SearchJobPagesAsync(
        UpworkJobSearchRequest request, UpworkPaging? paging = null, CancellationToken cancellationToken = default);

    /// <summary>Follows cursors for <c>smart_search</c>, yielding whole pages (each with its own raw JSON).</summary>
    IAsyncEnumerable<UpworkJobSearchPage> SmartSearchJobPagesAsync(
        UpworkSmartSearchRequest request, UpworkPaging? paging = null, CancellationToken cancellationToken = default);
}

public sealed class UpworkMcpClient : IUpworkMcpClient
{
    private const string FindJobs = UpworkToolCatalogValidator.FindJobsTool;
    private const string ListAccounts = UpworkToolCatalogValidator.ListAccountsTool;

    private readonly IUpworkMcpSessionFactory _sessionFactory;
    private readonly UpworkMcpOptions _options;
    private readonly ILogger<UpworkMcpClient> _logger;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly SemaphoreSlim _accountGate = new(1, 1);
    private readonly ConcurrentDictionary<string, byte> _reportedDrift = new(StringComparer.Ordinal);
    private Connection? _connection;
    private string? _orgUid;
    private volatile bool _disposed;

    public UpworkMcpClient(
        IUpworkMcpSessionFactory sessionFactory,
        IOptions<UpworkMcpOptions> options,
        ILogger<UpworkMcpClient> logger)
    {
        _sessionFactory = sessionFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<UpworkToolCatalog> VerifyToolsAsync(CancellationToken cancellationToken = default) =>
        (await GetConnectionAsync(cancellationToken)).Catalog;

    public async Task<UpworkAccountList> ListAccountsAsync(CancellationToken cancellationToken = default)
    {
        var context = new UpworkParseContext();
        var (root, raw) = await CallToolAsync(ListAccounts, action: null, new JsonObject(), jobId: null, cancellationToken);
        var accounts = UpworkResponseParser.ParseAccounts(root, raw, context);
        Finish(context, raw);
        return accounts;
    }

    public Task<UpworkJobSearchPage> SearchJobsAsync(
        UpworkJobSearchRequest request, CancellationToken cancellationToken = default) =>
        FindJobsPageAsync("search", UpworkRequestMapper.ToParams(request), cancellationToken);

    public Task<UpworkJobSearchPage> SmartSearchJobsAsync(
        UpworkSmartSearchRequest request, CancellationToken cancellationToken = default) =>
        FindJobsPageAsync("smart_search", UpworkRequestMapper.ToParams(request), cancellationToken);

    public async Task<UpworkJobDetails> GetJobAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var parameters = UpworkRequestMapper.ToGetParams(jobId);
        var (root, raw, context) = await CallFindJobsAsync("get", parameters, jobId, cancellationToken);
        var details = UpworkResponseParser.ParseJobDetails(root, raw, context);
        Finish(context, raw);
        return details;
    }

    public IAsyncEnumerable<UpworkJobSearchPage> SearchJobPagesAsync(
        UpworkJobSearchRequest request, UpworkPaging? paging = null, CancellationToken cancellationToken = default)
    {
        paging ??= new UpworkPaging();
        UpworkRequestMapper.ToParams(request); // fail fast, before the first page is requested
        ValidatePaging(paging, UpworkJobOrdering.IsNewestFirst(request), "search", "use sort=recency");

        return EnumeratePagesAsync(
            (cursor, ct) => SearchJobsAsync(request with { Cursor = cursor }, ct),
            request.Cursor,
            paging,
            cancellationToken);
    }

    public IAsyncEnumerable<UpworkJobSearchPage> SmartSearchJobPagesAsync(
        UpworkSmartSearchRequest request, UpworkPaging? paging = null, CancellationToken cancellationToken = default)
    {
        paging ??= new UpworkPaging();
        UpworkRequestMapper.ToParams(request);
        ValidatePaging(paging, UpworkJobOrdering.IsNewestFirst(request), "smart_search", "use mode=most_recent");

        return EnumeratePagesAsync(
            (cursor, ct) => SmartSearchJobsAsync(request with { Cursor = cursor }, ct),
            request.Cursor,
            paging,
            cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (Interlocked.Exchange(ref _connection, null) is { } connection)
            await DisposeQuietlyAsync(connection.Session);
    }

    private async Task<UpworkJobSearchPage> FindJobsPageAsync(string action, JsonObject parameters, CancellationToken cancellationToken)
    {
        var (root, raw, context) = await CallFindJobsAsync(action, parameters, jobId: null, cancellationToken);
        var parsed = UpworkResponseParser.ParseJobSearchPage(root, raw, context);
        Finish(context, raw);

        if (parsed.FiltersRejected is not null || parsed.FiltersIgnored is not null)
        {
            var details = string.Join(
                "; ",
                new[]
                {
                    parsed.FiltersRejected is null ? null : $"filters_rejected={parsed.FiltersRejected}",
                    parsed.FiltersIgnored is null ? null : $"filters_ignored={parsed.FiltersIgnored}",
                    parsed.Page.ServerWarnings.Count == 0 ? null : $"warning={string.Join(" ", parsed.Page.ServerWarnings)}",
                }.Where(x => x is not null));
            throw new UpworkInvalidRequestException(
                UpworkErrorMapper.Describe(FindJobs, action, "FILTERS_NOT_APPLIED", $"Upwork did not apply every filter: {details}", raw.TraceId),
                raw,
                "FILTERS_NOT_APPLIED");
        }

        return parsed.Page;
    }

    private async Task<(JsonElement Root, UpworkRawExchange Raw, UpworkParseContext Context)> CallFindJobsAsync(
        string action, JsonObject parameters, string? jobId, CancellationToken cancellationToken)
    {
        var orgUid = await GetOrgUidAsync(cancellationToken);
        var connection = await GetConnectionAsync(cancellationToken);

        var context = new UpworkParseContext();
        if (connection.Catalog.FindJobsParamsByAction.TryGetValue(action, out var declared))
        {
            foreach (var (name, _) in parameters)
            {
                if (!declared.Contains(name))
                    context.Warn($"request.params.{name}", $"not declared in the {FindJobs} schema for action '{action}'");
            }
        }

        var arguments = new JsonObject { ["action"] = action, ["org_uid"] = orgUid, ["params"] = parameters };
        var (root, raw) = await CallToolAsync(FindJobs, action, arguments, jobId, cancellationToken);
        return (root, raw, context);
    }

    /// <summary>Sends one tool call and returns the payload root, or throws a typed exception carrying the raw exchange.</summary>
    private async Task<(JsonElement Root, UpworkRawExchange Raw)> CallToolAsync(
        string tool, string? action, JsonObject arguments, string? jobId, CancellationToken cancellationToken)
    {
        var connection = await GetConnectionAsync(cancellationToken);
        var toolName = connection.Catalog.ToolNames[tool];
        var requestJson = arguments.ToJsonString();
        var argumentMap = ToArgumentMap(requestJson);
        var startedAt = DateTimeOffset.UtcNow;

        CallToolResult result;
        try
        {
            result = await WithTimeoutAsync(
                ct => connection.Session.CallToolAsync(toolName, argumentMap, ct),
                _options.CallTimeout,
                action is null ? tool : $"{tool}/{action}",
                cancellationToken);
        }
        catch (Exception ex) when (!IsCallerCancellation(ex, cancellationToken))
        {
            var failedRaw = new UpworkRawExchange
            {
                Tool = tool,
                Action = action,
                RequestJson = requestJson,
                PayloadSource = UpworkPayloadSource.None,
                StartedAt = startedAt,
                CompletedAt = DateTimeOffset.UtcNow,
            };
            var mapped = UpworkErrorMapper.FromException(ex, failedRaw, tool, action);
            _logger.LogWarning(ex, "Upwork {Tool}/{Action} call failed: {Disposition}", tool, action, mapped.Disposition);
            if (mapped is UpworkTransientException or UpworkAuthRequiredException)
                await ResetConnectionAsync(connection);
            throw mapped;
        }

        var payload = UpworkPayload.From(result);
        var raw = new UpworkRawExchange
        {
            Tool = tool,
            Action = action,
            RequestJson = requestJson,
            ResponseJson = payload.Text,
            PayloadSource = payload.Source,
            IsError = result.IsError == true || UpworkResponseParser.LooksLikeError(payload.Json),
            TraceId = UpworkResponseParser.ReadTraceId(payload.Json),
            StartedAt = startedAt,
            CompletedAt = DateTimeOffset.UtcNow,
        };

        _logger.LogDebug(
            "Upwork {Tool}/{Action} returned in {Elapsed} ms (error: {IsError}, trace {TraceId})",
            tool, action, (raw.CompletedAt - raw.StartedAt).TotalMilliseconds, raw.IsError, raw.TraceId);

        if (raw.IsError)
        {
            var error = UpworkErrorMapper.FromToolError(payload.Json, raw, jobId);
            _logger.LogWarning("{Message} -> {Disposition}", error.Message, error.Disposition);
            throw error;
        }

        if (payload.Json is not { ValueKind: JsonValueKind.Object } root)
        {
            var issue = payload.Text is null
                ? "$: no payload (neither structured content nor text content)"
                : payload.Json is null ? "$: payload is not JSON" : "$: payload is not a JSON object";
            throw new UpworkContractMismatchException(
                UpworkErrorMapper.Describe(tool, action, "CONTRACT_MISMATCH", issue, raw.TraceId), raw, "CONTRACT_MISMATCH")
            {
                Issues = [issue],
            };
        }

        return (root, raw);
    }

    /// <summary>Fails on contract errors; reports drift once per process (and fails on it when configured).</summary>
    private void Finish(UpworkParseContext context, UpworkRawExchange raw)
    {
        if (context.HasErrors)
        {
            var issues = context.Errors;
            _logger.LogError(
                "Upwork {Tool}/{Action} response no longer matches the client (trace {TraceId}): {Issues}",
                raw.Tool, raw.Action, raw.TraceId, string.Join("; ", issues));
            throw new UpworkContractMismatchException(
                UpworkErrorMapper.Describe(raw.Tool, raw.Action, "CONTRACT_MISMATCH",
                    $"response no longer matches the client: {string.Join("; ", issues.Take(5))}", raw.TraceId),
                raw,
                "CONTRACT_MISMATCH")
            {
                Issues = issues,
            };
        }

        var diagnostics = context.ToDiagnostics();
        if (!diagnostics.HasDrift)
            return;

        foreach (var field in diagnostics.UnknownFields)
        {
            if (_reportedDrift.TryAdd($"{raw.Tool}/{raw.Action}|unknown|{field}", 0))
            {
                _logger.LogWarning(
                    "Upwork {Tool}/{Action} returned a field the client does not map: {Path} (trace {TraceId})",
                    raw.Tool, raw.Action, field, raw.TraceId);
            }
        }

        foreach (var warning in diagnostics.Warnings)
        {
            if (_reportedDrift.TryAdd($"{raw.Tool}/{raw.Action}|warning|{warning}", 0))
            {
                _logger.LogWarning(
                    "Upwork {Tool}/{Action} drift: {Warning} (trace {TraceId})",
                    raw.Tool, raw.Action, warning, raw.TraceId);
            }
        }

        if (_options.TreatDriftAsError)
        {
            IReadOnlyList<string> issues =
                [.. diagnostics.UnknownFields.Select(f => $"{f}: field not mapped by the client"), .. diagnostics.Warnings];
            throw new UpworkContractMismatchException(
                UpworkErrorMapper.Describe(raw.Tool, raw.Action, "SCHEMA_DRIFT",
                    $"response drifted from the client's model: {string.Join("; ", issues.Take(5))}", raw.TraceId),
                raw,
                "SCHEMA_DRIFT")
            {
                Issues = issues,
            };
        }
    }

    private async Task<string> GetOrgUidAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_options.OrgUid))
            return _options.OrgUid.Trim();
        if (Volatile.Read(ref _orgUid) is { } cached)
            return cached;

        await _accountGate.WaitAsync(cancellationToken);
        try
        {
            if (_orgUid is { } resolved)
                return resolved;

            var list = await ListAccountsAsync(cancellationToken);
            var freelancers = list.Accounts
                .Where(a => string.Equals(a.Role, "TALENT", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var candidates = freelancers.Count > 0 ? freelancers : list.Accounts.ToList();
            if (candidates.Count != 1)
            {
                throw new UpworkInvalidRequestException(
                    $"Cannot choose an Upwork account: list_accounts returned {list.Accounts.Count} account(s), " +
                    $"{freelancers.Count} of them freelancer. Set {UpworkMcpOptions.SectionName}:OrgUid.",
                    list.Raw,
                    "ACCOUNT_NOT_SELECTED");
            }

            Volatile.Write(ref _orgUid, candidates[0].OrgUid);
            return candidates[0].OrgUid;
        }
        finally
        {
            _accountGate.Release();
        }
    }

    private async Task<Connection> GetConnectionAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Volatile.Read(ref _connection) is { } existing)
            return existing;

        await _connectGate.WaitAsync(cancellationToken);
        try
        {
            if (_connection is { } connected)
                return connected;

            var startedAt = DateTimeOffset.UtcNow;
            IUpworkMcpSession session;
            try
            {
                // No extra timeout here: the transport has its own, and interactive sign-in waits for a human.
                session = await _sessionFactory.ConnectAsync(cancellationToken);
            }
            catch (Exception ex) when (!IsCallerCancellation(ex, cancellationToken))
            {
                var mapped = UpworkErrorMapper.FromException(ex, raw: null, "connect", action: null);
                _logger.LogWarning(ex, "Connecting to Upwork MCP failed: {Disposition}", mapped.Disposition);
                throw mapped;
            }

            try
            {
                var tools = await WithTimeoutAsync(
                    ct => session.ListToolsAsync(ct), _options.CallTimeout, UpworkToolCatalogValidator.ToolsListName, cancellationToken);
                var catalog = UpworkToolCatalogValidator.Validate(tools, startedAt, DateTimeOffset.UtcNow);

                foreach (var warning in catalog.Warnings)
                {
                    if (_reportedDrift.TryAdd($"catalog|{warning}", 0))
                        _logger.LogWarning("Upwork MCP tool schema: {Warning}", warning);
                }

                var connection = new Connection(session, catalog);
                Volatile.Write(ref _connection, connection);
                _logger.LogInformation("Connected to Upwork MCP ({Tools})", string.Join(", ", catalog.ToolNames.Values));
                return connection;
            }
            catch (Exception ex)
            {
                await DisposeQuietlyAsync(session);
                if (ex is UpworkMcpException || IsCallerCancellation(ex, cancellationToken))
                    throw;
                throw UpworkErrorMapper.FromException(ex, raw: null, UpworkToolCatalogValidator.ToolsListName, action: null);
            }
        }
        finally
        {
            _connectGate.Release();
        }
    }

    private async Task ResetConnectionAsync(Connection connection)
    {
        if (Interlocked.CompareExchange(ref _connection, null, connection) == connection)
            await DisposeQuietlyAsync(connection.Session);
    }

    private async ValueTask DisposeQuietlyAsync(IUpworkMcpSession session)
    {
        try
        {
            await session.DisposeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Disposing the Upwork MCP session failed");
        }
    }

    private static async IAsyncEnumerable<UpworkJobSearchPage> EnumeratePagesAsync(
        Func<string?, CancellationToken, Task<UpworkJobSearchPage>> fetch,
        string? firstCursor,
        UpworkPaging paging,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var cursor = firstCursor;
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);

        for (var pageNumber = 1; pageNumber <= paging.MaxPages; pageNumber++)
        {
            var page = await fetch(cursor, cancellationToken);
            yield return page;

            if (!page.HasMore || string.IsNullOrEmpty(page.NextCursor))
                yield break;
            if (paging.PublishedAfter is { } cutoff && page.Jobs.Count > 0 && page.Jobs.All(j => j.PublishedAt < cutoff))
                yield break;
            if (page.NextCursor == cursor || !seenCursors.Add(page.NextCursor))
            {
                var issue = $"$.next_cursor: '{page.NextCursor}' was returned before; following it would loop";
                throw new UpworkContractMismatchException(
                    UpworkErrorMapper.Describe(page.Raw.Tool, page.Raw.Action, "CONTRACT_MISMATCH", issue, page.Raw.TraceId),
                    page.Raw,
                    "CONTRACT_MISMATCH")
                {
                    Issues = [issue],
                };
            }

            cursor = page.NextCursor;
        }
    }

    private static void ValidatePaging(UpworkPaging paging, bool newestFirst, string action, string hint)
    {
        var errors = new List<string>();
        if (paging.MaxPages < 1)
            errors.Add($"paging.MaxPages must be at least 1, got {paging.MaxPages}");
        if (paging.PublishedAfter is not null && !newestFirst)
            errors.Add($"paging.PublishedAfter needs newest-first results ({hint})");
        if (errors.Count > 0)
            throw UpworkRequestMapper.Invalid(action, errors);
    }

    private static async Task<T> WithTimeoutAsync<T>(
        Func<CancellationToken, Task<T>> operation, TimeSpan timeout, string what, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            return await operation(timeoutSource.Token);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && timeoutSource.IsCancellationRequested)
        {
            throw new TimeoutException($"Upwork {what} timed out after {timeout.TotalSeconds:0.#} s", ex);
        }
    }

    private static bool IsCallerCancellation(Exception exception, CancellationToken cancellationToken) =>
        exception is OperationCanceledException && cancellationToken.IsCancellationRequested;

    /// <summary>Arguments as JSON elements parsed from the exact request JSON, so what is sent equals what is recorded.</summary>
    private static Dictionary<string, object?> ToArgumentMap(string requestJson)
    {
        using var document = JsonDocument.Parse(requestJson);
        return document.RootElement.EnumerateObject()
            .ToDictionary(p => p.Name, p => (object?)p.Value.Clone(), StringComparer.Ordinal);
    }

    private sealed record Connection(IUpworkMcpSession Session, UpworkToolCatalog Catalog);
}
