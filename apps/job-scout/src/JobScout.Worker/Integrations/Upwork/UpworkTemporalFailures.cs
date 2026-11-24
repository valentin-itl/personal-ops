using JobScout.Worker.Integrations.Upwork.Models;
using Temporalio.Exceptions;

namespace JobScout.Worker.Integrations.Upwork;

/// <summary>Failure details attached to the Temporal failure so the workflow can decide without the exception type.</summary>
public sealed record UpworkFailureDetails
{
    public required UpworkFailureDisposition Disposition { get; init; }

    public string? Tool { get; init; }

    public string? Action { get; init; }

    public string? ErrorCode { get; init; }

    public string? TraceId { get; init; }

    public TimeSpan? RetryAfter { get; init; }

    public IReadOnlyList<string> Issues { get; init; } = [];

    /// <summary>Only when requested: the raw exchange can be large and it lands in workflow history.</summary>
    public UpworkRawExchange? Raw { get; init; }

    public static UpworkFailureDetails From(UpworkMcpException exception, bool includeRaw = false) => new()
    {
        Disposition = exception.Disposition,
        Tool = exception.Tool,
        Action = exception.Action,
        ErrorCode = exception.ErrorCode,
        TraceId = exception.TraceId,
        RetryAfter = exception.RetryAfter,
        Issues = exception is UpworkContractMismatchException mismatch ? mismatch.Issues : [],
        Raw = includeRaw ? exception.Raw : null,
    };
}

public static class UpworkTemporalFailures
{
    /// <summary>
    /// Converts to a Temporal application failure: Retry/RetryAfter stay retryable (RetryAfter becomes the next
    /// retry delay); SkipItem, Pause and Terminate become non-retryable, for the workflow to handle.
    /// <see cref="ApplicationFailureException.ErrorType"/> is the exception class name, e.g. <c>UpworkAuthRequiredException</c>.
    /// Persist <see cref="UpworkMcpException.Raw"/> yourself before throwing; it is left out of the failure by default.
    /// </summary>
    public static ApplicationFailureException ToApplicationFailure(this UpworkMcpException exception, bool includeRaw = false)
    {
        var retryable = exception.Disposition is UpworkFailureDisposition.Retry or UpworkFailureDisposition.RetryAfter;
        return new ApplicationFailureException(
            exception.Message,
            exception,
            errorType: exception.GetType().Name,
            nonRetryable: !retryable,
            details: [UpworkFailureDetails.From(exception, includeRaw)],
            nextRetryDelay: retryable ? exception.RetryAfter : null);
    }
}
