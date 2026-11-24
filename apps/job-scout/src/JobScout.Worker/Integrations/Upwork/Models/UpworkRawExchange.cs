namespace JobScout.Worker.Integrations.Upwork.Models;

/// <summary>Where the payload of a tool call came from inside the MCP result.</summary>
public enum UpworkPayloadSource
{
    /// <summary>Nothing came back (transport failure before a result arrived).</summary>
    None = 0,

    /// <summary>The result's <c>structuredContent</c> object.</summary>
    StructuredContent = 1,

    /// <summary>The concatenated text content blocks of the result.</summary>
    TextContent = 2,
}

/// <summary>
/// The exact request sent to, and payload returned by, one MCP tool call.
/// Every typed response and every <see cref="UpworkMcpException"/> carries one,
/// so the original JSON can be stored and compared when a field goes missing or a new one appears.
/// </summary>
public sealed record UpworkRawExchange
{
    /// <summary>Logical tool name, e.g. <c>find_jobs</c> (without any server prefix).</summary>
    public required string Tool { get; init; }

    /// <summary>Tool action, e.g. <c>search</c>; null for tools without actions.</summary>
    public string? Action { get; init; }

    /// <summary>The tool arguments exactly as sent, as JSON.</summary>
    public required string RequestJson { get; init; }

    /// <summary>
    /// The payload exactly as the server returned it (not re-serialized). Usually a JSON object;
    /// it can be plain text when the server broke its contract. Null when no result arrived.
    /// </summary>
    public string? ResponseJson { get; init; }

    public UpworkPayloadSource PayloadSource { get; init; }

    /// <summary>True when the server flagged the result as an error (<c>isError</c> or <c>status: "error"</c>).</summary>
    public bool IsError { get; init; }

    /// <summary>The server's <c>trace_id</c>, when present. Quote it when reporting problems to Upwork.</summary>
    public string? TraceId { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public required DateTimeOffset CompletedAt { get; init; }
}
