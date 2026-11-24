using System.Globalization;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using JobScout.Worker.Integrations.Upwork.Models;
using ModelContextProtocol;

namespace JobScout.Worker.Integrations.Upwork;

/// <summary>Turns error results and transport exceptions into the typed <see cref="UpworkMcpException"/> family.</summary>
internal static class UpworkErrorMapper
{
    public const string JobBrowseCooldownCode = "JOB_BROWSE_COOLDOWN";

    /// <summary>Message prefix of the SDK's OAuth handshake failures (ClientOAuthProvider).</summary>
    private const string OAuthFailurePrefix = "Failed to handle unauthorized response";

    /// <summary>Upwork does not send a delay with the job-browse cooldown; its docs say it lasts about a minute.</summary>
    public static readonly TimeSpan JobBrowseCooldown = TimeSpan.FromSeconds(60);

    private static readonly HashSet<string> RateLimitCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "RATE_LIMITED", "RATE_LIMIT", "RATE_LIMIT_EXCEEDED", "TOO_MANY_REQUESTS", "THROTTLED", "QUOTA_EXCEEDED",
        JobBrowseCooldownCode,
    };

    private static readonly HashSet<string> AuthCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "UNAUTHORIZED", "UNAUTHENTICATED", "AUTH", "AUTH_REQUIRED", "AUTHENTICATION_REQUIRED",
        "TOKEN_EXPIRED", "INVALID_TOKEN", "REAUTH_REQUIRED",
    };

    private static readonly HashSet<string> InvalidRequestCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "CLIENT", "VALIDATION", "VALIDATION_ERROR", "INVALID_PARAMS", "INVALID_ARGUMENT", "INVALID_REQUEST", "BAD_REQUEST",
    };

    /// <summary>On <c>find_jobs get</c> these mean "this job cannot be opened" (gone, private, or a per-job browse denial).</summary>
    private static readonly HashSet<string> JobUnavailableCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "NOT_FOUND", "FORBIDDEN", "PRIVATE", "GONE", "JOB_NOT_FOUND", "ACCESS_DENIED",
    };

    private static readonly HashSet<string> TransientCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "SERVER", "SERVER_ERROR", "INTERNAL", "INTERNAL_ERROR", "UPSTREAM", "UPSTREAM_ERROR",
        "UNAVAILABLE", "SERVICE_UNAVAILABLE", "TIMEOUT", "BAD_GATEWAY", "GATEWAY_TIMEOUT",
    };

    /// <summary>Maps an error result (<c>isError</c>, <c>status: "error"</c> or an <c>error_code</c>).</summary>
    public static UpworkMcpException FromToolError(JsonElement? root, UpworkRawExchange raw, string? jobId = null)
    {
        string? code = null, reason = null;
        TimeSpan? retryAfter = null;

        if (root is { ValueKind: JsonValueKind.Object } r)
        {
            code = ReadString(r, "error_code") ?? ReadString(r, "code");
            reason = ReadString(r, "reason") ?? ReadString(r, "message") ?? ReadString(r, "error");
            retryAfter = ReadSeconds(r, "retry_after_seconds");
        }

        reason ??= Truncate(raw.ResponseJson) ?? "no details returned";
        var message = Describe(raw.Tool, raw.Action, code, reason, raw.TraceId);

        if (retryAfter is not null || (code is not null && RateLimitCodes.Contains(code)))
        {
            if (retryAfter is null && string.Equals(code, JobBrowseCooldownCode, StringComparison.OrdinalIgnoreCase))
                retryAfter = JobBrowseCooldown;
            return new UpworkRateLimitedException(message, raw, code) { RetryAfter = retryAfter };
        }

        if (code is null)
            return new UpworkToolErrorException(message, raw);

        if (AuthCodes.Contains(code))
            return new UpworkAuthRequiredException(message, raw, code);

        if (raw.Action == "get" && JobUnavailableCodes.Contains(code))
            return new UpworkJobUnavailableException(message, raw, code) { JobId = jobId };

        if (InvalidRequestCodes.Contains(code) || JobUnavailableCodes.Contains(code))
            return new UpworkInvalidRequestException(message, raw, code);

        if (TransientCodes.Contains(code))
            return new UpworkTransientException(message, raw, code);

        return new UpworkToolErrorException(message, raw, code);
    }

    /// <summary>Maps an exception thrown while connecting or calling (no tool result arrived).</summary>
    public static UpworkMcpException FromException(Exception exception, UpworkRawExchange? raw, string tool, string? action)
    {
        // Already typed, e.g. thrown by the auth callback and wrapped by the SDK.
        if (Find<UpworkMcpException>(exception) is { } typed)
            return typed;

        // The SDK reports every OAuth handshake failure (client registration rejected, token exchange refused,
        // no usable authorization server) as a plain McpException with this prefix and no inner exception.
        // Retrying cannot fix it, so it must not fall through to "transient" below.
        if (Find<McpException>(exception) is { } oauth and not McpProtocolException
            && oauth.Message.StartsWith(OAuthFailurePrefix, StringComparison.Ordinal))
        {
            var code = oauth.Message.Contains("Dynamic client registration failed", StringComparison.Ordinal)
                ? "CLIENT_REGISTRATION_REJECTED"
                : "OAUTH_FAILED";
            return new UpworkAuthRequiredException(
                Describe(tool, action, code, oauth.Message, raw?.TraceId), raw, code, exception)
            {
                Tool = tool,
                Action = action,
            };
        }

        var http = Find<HttpRequestException>(exception);
        if (http?.StatusCode is { } status)
        {
            var statusCode = (int)status;
            var code = $"HTTP_{statusCode}";
            var message = Describe(tool, action, code, http.Message, raw?.TraceId);
            return statusCode switch
            {
                401 or 403 => new UpworkAuthRequiredException(message, raw, code, exception) { Tool = tool, Action = action },
                429 => new UpworkRateLimitedException(message, raw, code, exception) { Tool = tool, Action = action },
                404 or 405 or 410 => new UpworkContractMismatchException(message, raw, code, exception)
                {
                    Tool = tool,
                    Action = action,
                    Issues = [$"endpoint: HTTP {statusCode}; the MCP endpoint moved or no longer exists"],
                },
                >= 500 => new UpworkTransientException(message, raw, code, exception) { Tool = tool, Action = action },
                _ => new UpworkToolErrorException(message, raw, code, exception) { Tool = tool, Action = action },
            };
        }

        if (Find<McpProtocolException>(exception) is { } protocol)
        {
            var code = $"JSONRPC_{(int)protocol.ErrorCode}";
            var message = Describe(tool, action, code, protocol.Message, raw?.TraceId);
            return protocol.ErrorCode switch
            {
                McpErrorCode.MethodNotFound => new UpworkContractMismatchException(message, raw, code, exception)
                {
                    Tool = tool,
                    Action = action,
                    Issues = [$"json-rpc: method not found ({protocol.Message})"],
                },
                McpErrorCode.InvalidParams or McpErrorCode.InvalidRequest =>
                    new UpworkInvalidRequestException(message, raw, code, exception) { Tool = tool, Action = action },
                _ => new UpworkTransientException(message, raw, code, exception) { Tool = tool, Action = action },
            };
        }

        if (http is not null
            || Find<TimeoutException>(exception) is not null
            || Find<OperationCanceledException>(exception) is not null
            || Find<SocketException>(exception) is not null
            || Find<IOException>(exception) is not null
            || Find<JsonException>(exception) is not null
            || Find<McpException>(exception) is not null
            || exception is ObjectDisposedException or InvalidOperationException)
        {
            var message = Describe(tool, action, null, exception.Message, raw?.TraceId);
            return new UpworkTransientException(message, raw, null, exception) { Tool = tool, Action = action };
        }

        return new UpworkToolErrorException(
            Describe(tool, action, null, $"unexpected {exception.GetType().Name}: {exception.Message}", raw?.TraceId),
            raw,
            null,
            exception)
        { Tool = tool, Action = action };
    }

    public static string Describe(string tool, string? action, string? code, string reason, string? traceId)
    {
        var name = action is null ? tool : $"{tool}/{action}";
        var codePart = code is null ? "" : $" [{code}]";
        var tracePart = traceId is null ? "" : $" (trace {traceId})";
        return $"Upwork {name} failed{codePart}: {reason}{tracePart}";
    }

    private static T? Find<T>(Exception exception) where T : Exception
    {
        for (Exception? e = exception; e is not null; e = e.InnerException)
        {
            if (e is T match)
                return match;
            if (e is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    if (Find<T>(inner) is { } nested)
                        return nested;
                }
            }
        }

        return null;
    }

    private static string? ReadString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static TimeSpan? ReadSeconds(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v))
            return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var seconds) && seconds >= 0)
            return TimeSpan.FromSeconds(seconds);
        if (v.ValueKind == JsonValueKind.String
            && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out seconds)
            && seconds >= 0)
            return TimeSpan.FromSeconds(seconds);
        return null;
    }

    private static string? Truncate(string? text, int max = 500) =>
        text is null ? null : text.Length <= max ? text : text[..max] + "…";
}
