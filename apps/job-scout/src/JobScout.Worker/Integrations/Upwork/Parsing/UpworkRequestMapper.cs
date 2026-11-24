using System.Globalization;
using System.Text.Json.Nodes;
using JobScout.Worker.Integrations.Upwork.Models;

namespace JobScout.Worker.Integrations.Upwork.Parsing;

/// <summary>
/// Validates typed requests against the rules the server documents, then writes the <c>params</c> object
/// exactly as <c>find_jobs</c> expects it. A violation throws <see cref="UpworkInvalidRequestException"/> before anything is sent.
/// </summary>
internal static class UpworkRequestMapper
{
    public const string FindJobsTool = "find_jobs";
    private const int MaxLimit = 10;
    private const int MaxSkills = 5;

    public static JsonObject ToParams(UpworkJobSearchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var errors = new List<string>();
        var filters = request.Filters ?? new UpworkJobFilters();

        var title = Clean(request.Title, "title", errors);
        var query = Clean(request.Query, "query", errors);
        if (title is not null && query is not null)
            errors.Add("title and query cannot be combined (query lets jobs without the title words back in)");

        List<string>? skills = null;
        if (request.Skills is not null)
        {
            skills = request.Skills.Select(s => s?.Trim() ?? "").ToList();
            if (skills.Count == 0)
                errors.Add("skills is empty; leave it null to not filter by skills");
            if (skills.Count > MaxSkills)
                errors.Add($"at most {MaxSkills} skills are allowed, got {skills.Count}");
            if (skills.Any(s => s.Length == 0))
                errors.Add("skills contains an empty name");
        }

        var narrowed = title is not null || skills is { Count: > 0 }
            || !string.IsNullOrWhiteSpace(filters.Category) || !string.IsNullOrWhiteSpace(filters.Subcategory);
        if (request.Sort == UpworkJobSort.Relevance && narrowed)
            errors.Add("sort=relevance is refused together with title, skills, category or subcategory (Upwork drops those filters)");

        var sort = request.Sort is { } s ? Wire(s, errors) : null;
        ValidateLimit(request.Limit, errors);
        var cursor = Clean(request.Cursor, "cursor", errors);

        var p = new JsonObject();
        Add(p, "title", title);
        Add(p, "query", query);
        if (skills is { Count: > 0 })
            p["skills"] = new JsonArray(skills.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
        AddFilters(p, filters, errors);
        Add(p, "sort", sort);
        p["limit"] = request.Limit;
        if (request.IncludeFullDetails)
            p["include_full_details"] = true;
        Add(p, "cursor", cursor);

        ThrowIfInvalid("search", errors);
        return p;
    }

    public static JsonObject ToParams(UpworkSmartSearchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var errors = new List<string>();
        var filters = request.Filters ?? new UpworkJobFilters();

        var mode = Wire(request.Mode, errors);
        var hasRange = request.FromDate is not null || request.ToDate is not null;

        if (request.DaysPosted is { } days && days < 1)
            errors.Add($"days_posted must be at least 1, got {days}");
        if (request.DaysPosted is not null && hasRange)
            errors.Add("days_posted cannot be combined with from_date/to_date");
        if (request.FromDate > request.ToDate)
            errors.Add("from_date is after to_date");
        if ((request.DaysPosted is not null || hasRange) && request.Mode != UpworkSmartSearchMode.MostRecent)
            errors.Add("date filters apply only to mode=most_recent (best_match ignores them)");

        ValidateLimit(request.Limit, errors);
        var cursor = Clean(request.Cursor, "cursor", errors);

        var p = new JsonObject();
        Add(p, "mode", mode);
        if (request.DaysPosted is { } d)
            p["days_posted"] = d;
        if (request.FromDate is { } from)
            p["from_date"] = Rfc3339(from);
        if (request.ToDate is { } to)
            p["to_date"] = Rfc3339(to);
        AddFilters(p, filters, errors);
        p["limit"] = request.Limit;
        Add(p, "cursor", cursor);

        ThrowIfInvalid("smart_search", errors);
        return p;
    }

    public static JsonObject ToGetParams(string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId))
            ThrowIfInvalid("get", ["job id is empty"]);
        return new JsonObject { ["id"] = jobId.Trim() };
    }

    public static UpworkInvalidRequestException Invalid(string action, IEnumerable<string> errors) =>
        new($"Upwork {FindJobsTool}/{action} request is invalid: {string.Join("; ", errors)}")
        {
            Tool = FindJobsTool,
            Action = action,
        };

    private static void ThrowIfInvalid(string action, List<string> errors)
    {
        if (errors.Count > 0)
            throw Invalid(action, errors);
    }

    private static void AddFilters(JsonObject p, UpworkJobFilters f, List<string> errors)
    {
        if (f.JobType is { } jobType)
            Add(p, "job_type", Wire(jobType, errors));
        if (f.ExperienceLevel is { } level)
            Add(p, "experience_level", Wire(level, errors));

        AddAmountRange(p, "budget_min", f.BudgetMin, "budget_max", f.BudgetMax, errors);
        AddAmountRange(p, "rate_min", f.RateMin, "rate_max", f.RateMax, errors);

        Add(p, "category", Clean(f.Category, "category", errors));
        Add(p, "subcategory", Clean(f.Subcategory, "subcategory", errors));

        if (f.Workload is { } workload)
            Add(p, "workload", Wire(workload, errors));
        if (f.Duration is { } duration)
            Add(p, "duration", Wire(duration, errors));
        if (f.VerifiedPaymentOnly is { } verified)
            p["verified_payment_only"] = verified;

        AddCountRange(p, "proposals_min", f.ProposalsMin, "proposals_max", f.ProposalsMax, errors);
        AddCountRange(p, "client_hires_min", f.ClientHiresMin, "client_hires_max", f.ClientHiresMax, errors);

        if (f.PreviousClientsOnly is { } previous)
            p["previous_clients_only"] = previous;
        Add(p, "timezone", Clean(f.Timezone, "timezone", errors));
        Add(p, "location", Clean(f.Location, "location", errors));
    }

    /// <summary>Budget and rate bounds: each must be greater than 0.</summary>
    private static void AddAmountRange(
        JsonObject p, string minName, decimal? min, string maxName, decimal? max, List<string> errors)
    {
        foreach (var (name, value) in new[] { (minName, min), (maxName, max) })
        {
            if (value is not { } v)
                continue;
            if (v <= 0)
                errors.Add($"{name} must be greater than 0, got {v}");
            else
                p[name] = v;
        }

        if (min > max)
            errors.Add($"{minName} ({min}) is greater than {maxName} ({max})");
    }

    /// <summary>Count bounds (proposals, client hires): each must be 0 or more.</summary>
    private static void AddCountRange(
        JsonObject p, string minName, int? min, string maxName, int? max, List<string> errors)
    {
        foreach (var (name, value) in new[] { (minName, min), (maxName, max) })
        {
            if (value is not { } v)
                continue;
            if (v < 0)
                errors.Add($"{name} must be 0 or more, got {v}");
            else
                p[name] = v;
        }

        if (min > max)
            errors.Add($"{minName} ({min}) is greater than {maxName} ({max})");
    }

    private static void ValidateLimit(int limit, List<string> errors)
    {
        if (limit is < 1 or > MaxLimit)
            errors.Add($"limit must be between 1 and {MaxLimit}, got {limit}");
    }

    private static string? Clean(string? value, string name, List<string> errors)
    {
        if (value is null)
            return null;
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            errors.Add($"{name} is blank; leave it null to not filter");
            return null;
        }

        return trimmed;
    }

    private static void Add(JsonObject p, string name, string? value)
    {
        if (value is not null)
            p[name] = value;
    }

    private static string Rfc3339(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    internal static string? Wire(UpworkJobType value, List<string> errors) => value switch
    {
        UpworkJobType.Fixed => "fixed",
        UpworkJobType.Hourly => "hourly",
        _ => Bad("job_type", value, errors),
    };

    internal static string? Wire(UpworkExperienceLevel value, List<string> errors) => value switch
    {
        UpworkExperienceLevel.EntryLevel => "entry_level",
        UpworkExperienceLevel.Intermediate => "intermediate",
        UpworkExperienceLevel.Expert => "expert",
        _ => Bad("experience_level", value, errors),
    };

    internal static string? Wire(UpworkWorkload value, List<string> errors) => value switch
    {
        UpworkWorkload.FullTime => "full_time",
        UpworkWorkload.PartTime => "part_time",
        UpworkWorkload.AsNeeded => "as_needed",
        _ => Bad("workload", value, errors),
    };

    internal static string? Wire(UpworkProjectDuration value, List<string> errors) => value switch
    {
        UpworkProjectDuration.LessThanOneMonth => "less_than_1_month",
        UpworkProjectDuration.OneToThreeMonths => "1_to_3_months",
        UpworkProjectDuration.ThreeToSixMonths => "3_to_6_months",
        UpworkProjectDuration.MoreThanSixMonths => "more_than_6_months",
        _ => Bad("duration", value, errors),
    };

    internal static string? Wire(UpworkJobSort value, List<string> errors) => value switch
    {
        UpworkJobSort.Recency => "recency",
        UpworkJobSort.Relevance => "relevance",
        UpworkJobSort.ClientTotalCharge => "client_total_charge",
        UpworkJobSort.ClientRating => "client_rating",
        _ => Bad("sort", value, errors),
    };

    internal static string? Wire(UpworkSmartSearchMode value, List<string> errors) => value switch
    {
        UpworkSmartSearchMode.BestMatch => "best_match",
        UpworkSmartSearchMode.MostRecent => "most_recent",
        _ => Bad("mode", value, errors),
    };

    private static string? Bad<T>(string name, T value, List<string> errors)
    {
        errors.Add($"{name} has no Upwork value for '{value}'");
        return null;
    }
}
