using JobScout.Worker.Integrations.Upwork.Models;

namespace JobScout.Worker.Scanning;

/// <summary>Bound from the <c>JobScout</c> configuration section.</summary>
public sealed class JobScoutScanOptions
{
    public const string SectionName = "JobScout";

    /// <summary>Search profiles by id (the <c>SearchProfileId</c> a scan is started with). Ids are case-insensitive.</summary>
    public Dictionary<string, UpworkSearchProfile> SearchProfiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>What one scan asks Upwork for. Set exactly one of <see cref="Search"/> or <see cref="SmartSearch"/>.</summary>
public sealed class UpworkSearchProfile
{
    /// <summary>Marketplace search. Use <c>Sort: Recency</c> so the scan can stop at already-seen postings.</summary>
    public UpworkJobSearchRequest? Search { get; set; }

    /// <summary>Recommendation feed. <c>Mode: MostRecent</c> (the default) is filtered by date on the server.</summary>
    public UpworkSmartSearchRequest? SmartSearch { get; set; }

    /// <summary>Upper bound on pages per scan (10 jobs each).</summary>
    public int MaxPages { get; set; } = 3;

    /// <summary>Never look further back than this, even on the first scan or after a long outage.</summary>
    public TimeSpan MaxLookback { get; set; } = TimeSpan.FromDays(2);

    /// <summary>Re-read this much before the last completed scan, to catch postings published while it ran.</summary>
    public TimeSpan Overlap { get; set; } = TimeSpan.FromHours(1);

    internal IEnumerable<string> Validate()
    {
        if ((Search is null) == (SmartSearch is null))
            yield return "set exactly one of Search or SmartSearch";
        if (MaxPages < 1)
            yield return $"MaxPages must be at least 1, got {MaxPages}";
        if (MaxLookback <= TimeSpan.Zero)
            yield return "MaxLookback must be positive";
        if (Overlap < TimeSpan.Zero)
            yield return "Overlap cannot be negative";
    }
}
