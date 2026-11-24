using JobScout.Worker.Integrations.Upwork.Models;

namespace JobScout.Worker.Integrations.Upwork;

/// <summary>What the caller should do about a failed Upwork call.</summary>
public enum UpworkFailureDisposition
{
    /// <summary>Transient: retry with backoff.</summary>
    Retry = 1,

    /// <summary>Throttled: retry, but not before <see cref="UpworkMcpException.RetryAfter"/>.</summary>
    RetryAfter = 2,

    /// <summary>Only this item failed (e.g. a job that is gone or private): skip it and continue.</summary>
    SkipItem = 3,

    /// <summary>Needs a human (sign-in): stop and wait; retrying will not help.</summary>
    Pause = 4,

    /// <summary>Retrying the same call cannot succeed (broken request or changed server contract).</summary>
    Terminate = 5,
}

/// <summary>Base of every exception the Upwork client throws (except caller cancellation).</summary>
public abstract class UpworkMcpException(
    string message,
    UpworkRawExchange? raw = null,
    string? errorCode = null,
    Exception? innerException = null)
    : Exception(message, innerException)
{
    public abstract UpworkFailureDisposition Disposition { get; }

    /// <summary>Earliest sensible retry, when the server said so.</summary>
    public TimeSpan? RetryAfter { get; init; }

    /// <summary>Logical tool name (<c>find_jobs</c>, <c>list_accounts</c>, <c>tools/list</c>, <c>connect</c>).</summary>
    public string? Tool { get; init; } = raw?.Tool;

    public string? Action { get; init; } = raw?.Action;

    /// <summary>The server's <c>error_code</c>, or an HTTP / JSON-RPC code for transport failures.</summary>
    public string? ErrorCode { get; init; } = errorCode;

    public string? TraceId { get; init; } = raw?.TraceId;

    /// <summary>
    /// The request and whatever came back, verbatim. Null only for failures detected before anything was sent
    /// (local request validation, configuration).
    /// </summary>
    public UpworkRawExchange? Raw { get; init; } = raw;
}

/// <summary>Throttled by Upwork (<c>retry_after_seconds</c>, <c>JOB_BROWSE_COOLDOWN</c>, HTTP 429).</summary>
public sealed class UpworkRateLimitedException(
    string message,
    UpworkRawExchange? raw = null,
    string? errorCode = null,
    Exception? innerException = null)
    : UpworkMcpException(message, raw, errorCode, innerException)
{
    public override UpworkFailureDisposition Disposition =>
        RetryAfter is null ? UpworkFailureDisposition.Retry : UpworkFailureDisposition.RetryAfter;
}

/// <summary>Network failure, timeout, HTTP 5xx, broken MCP session or a server-side error.</summary>
public sealed class UpworkTransientException(
    string message,
    UpworkRawExchange? raw = null,
    string? errorCode = null,
    Exception? innerException = null)
    : UpworkMcpException(message, raw, errorCode, innerException)
{
    public override UpworkFailureDisposition Disposition => UpworkFailureDisposition.Retry;
}

/// <summary>No usable Upwork sign-in: refresh failed, token revoked, or interactive sign-in is disabled.</summary>
public sealed class UpworkAuthRequiredException(
    string message,
    UpworkRawExchange? raw = null,
    string? errorCode = null,
    Exception? innerException = null)
    : UpworkMcpException(message, raw, errorCode, innerException)
{
    public override UpworkFailureDisposition Disposition => UpworkFailureDisposition.Pause;
}

/// <summary>
/// The server's tools or responses no longer match what the client expects (missing tool or action,
/// missing or mistyped required field, non-JSON payload). See <see cref="Issues"/> and <see cref="UpworkMcpException.Raw"/>.
/// </summary>
public sealed class UpworkContractMismatchException(
    string message,
    UpworkRawExchange? raw = null,
    string? errorCode = null,
    Exception? innerException = null)
    : UpworkMcpException(message, raw, errorCode, innerException)
{
    /// <summary>One entry per problem, each starting with the JSON path, e.g. <c>$.jobs[3].id: required string is missing</c>.</summary>
    public IReadOnlyList<string> Issues { get; init; } = [];

    public override UpworkFailureDisposition Disposition => UpworkFailureDisposition.Terminate;
}

/// <summary>
/// The request is wrong: refused by the server (<c>error_code: CLIENT</c>), rejected locally before sending,
/// filters the server ignored or rejected, or missing configuration. Fix the request or search profile.
/// </summary>
public sealed class UpworkInvalidRequestException(
    string message,
    UpworkRawExchange? raw = null,
    string? errorCode = null,
    Exception? innerException = null)
    : UpworkMcpException(message, raw, errorCode, innerException)
{
    public override UpworkFailureDisposition Disposition => UpworkFailureDisposition.Terminate;
}

/// <summary>
/// <c>find_jobs get</c> could not open this job: not found, private, or a per-job browse denial.
/// Mark the job unavailable and continue with the others.
/// </summary>
public sealed class UpworkJobUnavailableException(
    string message,
    UpworkRawExchange? raw = null,
    string? errorCode = null,
    Exception? innerException = null)
    : UpworkMcpException(message, raw, errorCode, innerException)
{
    public string? JobId { get; init; }

    public override UpworkFailureDisposition Disposition => UpworkFailureDisposition.SkipItem;
}

/// <summary>An error result the client has no mapping for. <see cref="UpworkMcpException.ErrorCode"/> keeps the raw code.</summary>
public sealed class UpworkToolErrorException(
    string message,
    UpworkRawExchange? raw = null,
    string? errorCode = null,
    Exception? innerException = null)
    : UpworkMcpException(message, raw, errorCode, innerException)
{
    public override UpworkFailureDisposition Disposition => UpworkFailureDisposition.Retry;
}
