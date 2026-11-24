using System.Text.Json;
using System.Text.Json.Nodes;
using JobScout.Worker.Integrations.Upwork;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;

namespace JobScout.Tests.Upwork;

/// <summary>In-memory stand-in for the Upwork MCP server: serves tool definitions and canned results, records calls.</summary>
internal sealed class FakeUpworkMcp : IUpworkMcpSessionFactory
{
    public const string OrgUid = "1000000000000000001";

    public List<Tool> Tools { get; set; } = DefaultTools();

    /// <summary>Handles one call: (server tool name, arguments as JSON, cancellation) → result. May throw.</summary>
    public Func<string, JsonElement, CancellationToken, Task<CallToolResult>> HandlerAsync { get; set; } =
        (_, _, _) => throw new InvalidOperationException("No handler configured");

    public List<(string Tool, string ArgumentsJson)> Calls { get; } = [];

    public int ConnectCount { get; private set; }

    public int DisposeCount { get; private set; }

    public Exception? ConnectException { get; set; }

    /// <summary>Serves list_accounts from the fixture and find_jobs by action.</summary>
    public FakeUpworkMcp RespondByAction(Func<string, JsonElement, CallToolResult> findJobs)
    {
        HandlerAsync = (tool, args, _) => Task.FromResult(
            tool.EndsWith("list_accounts", StringComparison.Ordinal)
                ? Text(Fixtures.Load("list_accounts.json"))
                : findJobs(args.GetProperty("action").GetString()!, args.GetProperty("params")));
        return this;
    }

    public FakeUpworkMcp RespondWith(string findJobsPayload, bool isError = false) =>
        RespondByAction((_, _) => Text(findJobsPayload, isError));

    public UpworkMcpClient CreateClient(Action<UpworkMcpOptions>? configure = null)
    {
        var options = new UpworkMcpOptions { CallTimeout = TimeSpan.FromSeconds(5) };
        configure?.Invoke(options);
        return new UpworkMcpClient(this, Options.Create(options), NullLogger<UpworkMcpClient>.Instance);
    }

    public IEnumerable<JsonNode> FindJobsCalls() =>
        Calls.Where(c => c.Tool.EndsWith("find_jobs", StringComparison.Ordinal)).Select(c => JsonNode.Parse(c.ArgumentsJson)!);

    public Task<IUpworkMcpSession> ConnectAsync(CancellationToken cancellationToken)
    {
        ConnectCount++;
        if (ConnectException is not null)
            throw ConnectException;
        return Task.FromResult<IUpworkMcpSession>(new Session(this));
    }

    public static CallToolResult Text(string payload, bool isError = false) =>
        new() { Content = [new TextContentBlock { Text = payload }], IsError = isError };

    public static CallToolResult Structured(string payload) =>
        new() { StructuredContent = JsonDocument.Parse(payload).RootElement.Clone(), Content = [] };

    public static List<Tool> DefaultTools(string prefix = "") =>
    [
        new Tool
        {
            Name = prefix + "list_accounts",
            InputSchema = JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement.Clone(),
        },
        new Tool
        {
            Name = prefix + "find_jobs",
            InputSchema = JsonDocument.Parse(Fixtures.Load("find_jobs_input_schema.json")).RootElement.Clone(),
        },
    ];

    private sealed class Session(FakeUpworkMcp owner) : IUpworkMcpSession
    {
        public Task<IReadOnlyList<Tool>> ListToolsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Tool>>(owner.Tools);

        public Task<CallToolResult> CallToolAsync(
            string toolName, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken)
        {
            var json = JsonSerializer.Serialize(arguments);
            owner.Calls.Add((toolName, json));
            using var document = JsonDocument.Parse(json);
            return owner.HandlerAsync(toolName, document.RootElement.Clone(), cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            owner.DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }
}

internal static class Fixtures
{
    public static string Load(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Upwork", "Fixtures", name)).TrimEnd();

    /// <summary>Loads a fixture and applies an edit to its JSON (for contract-break tests).</summary>
    public static string Edit(string name, Action<JsonObject> edit)
    {
        var node = JsonNode.Parse(Load(name))!.AsObject();
        edit(node);
        return node.ToJsonString();
    }

    /// <summary>A minimal valid search page with one job per published date.</summary>
    public static string Page(string? nextCursor, bool hasMore, params DateTimeOffset[] published)
    {
        var jobs = new JsonArray();
        var i = 0;
        foreach (var date in published)
        {
            var id = $"{nextCursor ?? "last"}-{i++}";
            jobs.Add(new JsonObject
            {
                ["id"] = id,
                ["title"] = $"Job {id}",
                ["url"] = $"https://www.upwork.com/jobs/~02{id}",
                ["published_date"] = date.UtcDateTime.ToString("O"),
            });
        }

        var page = new JsonObject { ["status"] = "ok", ["jobs"] = jobs, ["hasMore"] = hasMore, ["trace_id"] = "trace-page" };
        if (nextCursor is not null)
            page["next_cursor"] = nextCursor;
        return page.ToJsonString();
    }
}
