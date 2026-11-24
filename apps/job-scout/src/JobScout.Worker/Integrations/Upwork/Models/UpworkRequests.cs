namespace JobScout.Worker.Integrations.Upwork.Models;

/// <summary>Filters shared by <c>find_jobs</c> <c>search</c> and <c>smart_search</c>. Null means "not filtered".</summary>
public sealed record UpworkJobFilters
{
    public UpworkJobType? JobType { get; init; }

    public UpworkExperienceLevel? ExperienceLevel { get; init; }

    /// <summary>Fixed-price budget floor (&gt; 0). Does not filter hourly jobs.</summary>
    public decimal? BudgetMin { get; init; }

    /// <summary>Fixed-price budget ceiling (&gt; 0). Does not filter hourly jobs.</summary>
    public decimal? BudgetMax { get; init; }

    /// <summary>Hourly overlap: keeps jobs whose posted maximum is at least this value.</summary>
    public decimal? RateMin { get; init; }

    /// <summary>Hourly overlap: keeps jobs whose posted minimum is at most this value.</summary>
    public decimal? RateMax { get; init; }

    /// <summary>Top-level category name ("Web, Mobile &amp; Software Dev") or ontology id.</summary>
    public string? Category { get; init; }

    public string? Subcategory { get; init; }

    public UpworkWorkload? Workload { get; init; }

    public UpworkProjectDuration? Duration { get; init; }

    public bool? VerifiedPaymentOnly { get; init; }

    public int? ProposalsMin { get; init; }

    public int? ProposalsMax { get; init; }

    public int? ClientHiresMin { get; init; }

    public int? ClientHiresMax { get; init; }

    public bool? PreviousClientsOnly { get; init; }

    /// <summary>Client timezone, IANA name such as <c>America/New_York</c>.</summary>
    public string? Timezone { get; init; }

    /// <summary>Client country or region, e.g. "United States" or "Europe".</summary>
    public string? Location { get; init; }
}

/// <summary>
/// <c>find_jobs</c> <c>action=search</c>: the whole marketplace, no date filter.
/// For fresh jobs use <see cref="UpworkJobSort.Recency"/> and a <see cref="UpworkPaging.PublishedAfter"/> cutoff.
/// </summary>
public sealed record UpworkJobSearchRequest
{
    /// <summary>Words that must all appear in the job title (1-3 words). Cannot be combined with <see cref="Query"/>.</summary>
    public string? Title { get; init; }

    /// <summary>Broad semantic match across the whole posting. Cannot be combined with <see cref="Title"/>.</summary>
    public string? Query { get; init; }

    /// <summary>Exact Upwork skill names, at most 5, ANDed.</summary>
    public IReadOnlyList<string>? Skills { get; init; }

    public UpworkJobFilters Filters { get; init; } = new();

    /// <summary>Null means Upwork's default: newest first, or relevance for a query-only search.</summary>
    public UpworkJobSort? Sort { get; init; }

    /// <summary>1-10.</summary>
    public int Limit { get; init; } = 10;

    /// <summary>Return each job's full description in the same response (larger payload).</summary>
    public bool IncludeFullDetails { get; init; }

    /// <summary>Page cursor from <see cref="UpworkJobSearchPage.NextCursor"/>; null for the first page.</summary>
    public string? Cursor { get; init; }
}

/// <summary>
/// <c>find_jobs</c> <c>action=smart_search</c>: Upwork's recommendation feeds for the signed-in freelancer.
/// Only <see cref="UpworkSmartSearchMode.MostRecent"/> honours the date filters.
/// </summary>
public sealed record UpworkSmartSearchRequest
{
    public UpworkSmartSearchMode Mode { get; init; } = UpworkSmartSearchMode.MostRecent;

    /// <summary>Only jobs posted in the last N days (&gt;= 1). Cannot be combined with <see cref="FromDate"/>/<see cref="ToDate"/>.</summary>
    public int? DaysPosted { get; init; }

    /// <summary>Only jobs posted at or after this instant. Sent as an RFC 3339 UTC timestamp.</summary>
    public DateTimeOffset? FromDate { get; init; }

    /// <summary>Only jobs posted at or before this instant. Sent as an RFC 3339 UTC timestamp.</summary>
    public DateTimeOffset? ToDate { get; init; }

    public UpworkJobFilters Filters { get; init; } = new();

    /// <summary>1-10.</summary>
    public int Limit { get; init; } = 10;

    /// <summary>Page cursor from <see cref="UpworkJobSearchPage.NextCursor"/>; null for the first page.</summary>
    public string? Cursor { get; init; }
}

public static class UpworkJobOrdering
{
    /// <summary>
    /// True when <c>search</c> returns newest first: sort=recency, or no sort unless the search is query-only
    /// (Upwork then defaults to relevance).
    /// </summary>
    public static bool IsNewestFirst(UpworkJobSearchRequest request)
    {
        var narrowed = request.Title is not null || request.Skills is { Count: > 0 }
            || request.Filters?.Category is not null || request.Filters?.Subcategory is not null;
        return request.Sort == UpworkJobSort.Recency || (request.Sort is null && (request.Query is null || narrowed));
    }

    /// <summary>True for <c>smart_search</c> mode=most_recent, the only feed ordered by date.</summary>
    public static bool IsNewestFirst(UpworkSmartSearchRequest request) => request.Mode == UpworkSmartSearchMode.MostRecent;
}

/// <summary>How far the page enumerators follow cursors.</summary>
public sealed record UpworkPaging
{
    /// <summary>Upper bound on pages fetched (each page holds up to 10 jobs).</summary>
    public int MaxPages { get; init; } = 5;

    /// <summary>
    /// Stop after the first page on which every job was published before this instant.
    /// Pages are yielded whole (so their raw JSON stays intact): filter older jobs out yourself.
    /// Requires a newest-first order.
    /// </summary>
    public DateTimeOffset? PublishedAfter { get; init; }
}
