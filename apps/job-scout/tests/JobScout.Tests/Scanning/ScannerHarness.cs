using System.Text.Json.Nodes;
using JobScout.Contracts;
using JobScout.Tests.Upwork;
using JobScout.Worker.Integrations.Upwork;
using JobScout.Worker.Integrations.Upwork.Models;
using JobScout.Worker.Persistence;
using JobScout.Worker.Persistence.Entities;
using JobScout.Worker.Scanning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JobScout.Tests.Scanning;

/// <summary>A scanner wired to the fake MCP server, a real client and an in-memory database.</summary>
internal sealed class ScannerHarness : IAsyncDisposable
{
    public static readonly DateTimeOffset T0 = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);

    private readonly string _databaseName = Guid.NewGuid().ToString();
    private readonly Lazy<UpworkMcpClient> _client;

    public ScannerHarness()
    {
        _client = new Lazy<UpworkMcpClient>(() => Fake.CreateClient());
    }

    public FakeUpworkMcp Fake { get; } = new();

    public MutableTime Time { get; } = new(T0);

    public Guid SourceId { get; } = Guid.NewGuid();

    public ListLogger<UpworkSourceScanner> Logs { get; } = new();

    public JobScoutScanOptions Options { get; } = new()
    {
        SearchProfiles =
        {
            ["mcp"] = new UpworkSearchProfile
            {
                Search = new() { Title = "MCP", Sort = UpworkJobSort.Recency },
                MaxPages = 3,
                MaxLookback = TimeSpan.FromDays(7),
                Overlap = TimeSpan.FromHours(1),
            },
            ["recommended"] = new UpworkSearchProfile
            {
                SmartSearch = new() { Mode = UpworkSmartSearchMode.MostRecent },
                MaxPages = 3,
                MaxLookback = TimeSpan.FromDays(2),
                Overlap = TimeSpan.FromHours(1),
            },
        },
    };

    public JobScoutDbContext NewDb() =>
        new(new DbContextOptionsBuilder<JobScoutDbContext>().UseInMemoryDatabase(_databaseName).Options);

    public async Task SeedSourceAsync(SourceHealth health = SourceHealth.Healthy)
    {
        await using var db = NewDb();
        db.Sources.Add(new Source { Id = SourceId, Health = health });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    public Task<SourceScanResult> ScanAsync(string profile = "mcp") => ScanAsync(SourceId.ToString(), profile);

    public async Task<SourceScanResult> ScanAsync(string sourceId, string profile)
    {
        await using var db = NewDb();
        var scanner = new UpworkSourceScanner(
            db, _client.Value, new FixedOptionsMonitor<JobScoutScanOptions>(Options), Time, Logs);
        return await scanner.ScanAsync(new SourceScanRequest(sourceId, profile), heartbeat: null, TestContext.Current.CancellationToken);
    }

    /// <summary>Serves search pages by the cursor in the request ("" for the first page).</summary>
    public void ServePages(Dictionary<string, string> pagesByCursor) =>
        Fake.RespondByAction((_, p) =>
        {
            var cursor = p.TryGetProperty("cursor", out var c) ? c.GetString()! : "";
            return FakeUpworkMcp.Text(pagesByCursor[cursor]);
        });

    public static string Page(string? nextCursor, params (string Id, string Title, DateTimeOffset Published)[] jobs)
    {
        var array = new JsonArray();
        foreach (var (id, title, published) in jobs)
        {
            array.Add(new JsonObject
            {
                ["id"] = id,
                ["title"] = title,
                ["url"] = $"https://www.upwork.com/jobs/~02{id}",
                ["published_date"] = published.UtcDateTime.ToString("O"),
                ["description_snippet"] = $"Description of {id}",
                ["job_type"] = "hourly",
                ["proposals_tier"] = "5 to 10",
            });
        }

        var page = new JsonObject
        {
            ["status"] = "ok",
            ["jobs"] = array,
            ["hasMore"] = nextCursor is not null,
            ["trace_id"] = $"trace-{nextCursor ?? "last"}",
        };
        if (nextCursor is not null)
            page["next_cursor"] = nextCursor;
        return page.ToJsonString();
    }

    public async ValueTask DisposeAsync()
    {
        if (_client.IsValueCreated)
            await _client.Value.DisposeAsync();
    }
}

internal sealed class MutableTime(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

internal sealed class FixedOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue => value;

    public T Get(string? name) => value;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

/// <summary>Keeps every log entry with its structured properties, so tests can check what was logged.</summary>
internal sealed class ListLogger<T> : ILogger<T>
{
    public List<LogEntry> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Entries.Add(new LogEntry(
            logLevel,
            exception,
            formatter(state, exception),
            state as IReadOnlyList<KeyValuePair<string, object?>> ?? []));
}

internal sealed record LogEntry(
    LogLevel Level, Exception? Exception, string Message, IReadOnlyList<KeyValuePair<string, object?>> Properties)
{
    public object? Get(string name) => Properties.FirstOrDefault(p => p.Key == name).Value;
}
