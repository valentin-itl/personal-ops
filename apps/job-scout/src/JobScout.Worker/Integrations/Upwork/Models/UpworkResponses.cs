using System.Text.Json.Serialization;

namespace JobScout.Worker.Integrations.Upwork.Models;

/// <summary>Non-fatal differences between what the server returned and what the client expects.</summary>
public sealed record UpworkResponseDiagnostics
{
    public static UpworkResponseDiagnostics None { get; } = new();

    /// <summary>
    /// JSON paths of properties the client does not map, e.g. <c>$.jobs[*].new_field</c>.
    /// Usually new data; it is still in the raw JSON.
    /// </summary>
    public IReadOnlyList<string> UnknownFields { get; init; } = [];

    /// <summary>
    /// Other drift: an optional field with an unexpected type (its value was dropped), an unknown enum value,
    /// a request parameter the tool schema no longer declares.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    [JsonIgnore]
    public bool HasDrift => UnknownFields.Count > 0 || Warnings.Count > 0;
}

/// <summary>Base of every typed tool response: the parsed data plus the original exchange.</summary>
public abstract record UpworkToolResponse
{
    /// <summary>The request and the payload exactly as returned. Store <see cref="UpworkRawExchange.ResponseJson"/> for diagnosis.</summary>
    public required UpworkRawExchange Raw { get; init; }

    public UpworkResponseDiagnostics Diagnostics { get; init; } = UpworkResponseDiagnostics.None;
}

public sealed record UpworkAccount
{
    public required string OrgUid { get; init; }

    public string? Name { get; init; }

    /// <summary>E.g. <c>TALENT</c> for a freelancer account.</summary>
    public string? Role { get; init; }

    public string? RoleLabel { get; init; }
}

public sealed record UpworkAccountList : UpworkToolResponse
{
    public required IReadOnlyList<UpworkAccount> Accounts { get; init; }
}

/// <summary>One page of <c>find_jobs</c> <c>search</c> or <c>smart_search</c> results.</summary>
public sealed record UpworkJobSearchPage : UpworkToolResponse
{
    public required IReadOnlyList<UpworkJobSummary> Jobs { get; init; }

    /// <summary>Cursor for the next page; pass it back with the same filters.</summary>
    public string? NextCursor { get; init; }

    public bool HasMore { get; init; }

    /// <summary>smart_search only.</summary>
    public int? TotalMatching { get; init; }

    /// <summary>smart_search only: <c>best_match</c> or <c>most_recent</c>.</summary>
    public string? Mode { get; init; }

    /// <summary>The server's top-level <c>warning</c> text(s), if any.</summary>
    public IReadOnlyList<string> ServerWarnings { get; init; } = [];
}

/// <summary>A job as it appears in search results.</summary>
public sealed record UpworkJobSummary
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    public required string Url { get; init; }

    public required DateTimeOffset PublishedAt { get; init; }

    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>Truncated description, with Upwork's untrusted-content markers removed.</summary>
    public string? DescriptionSnippet { get; init; }

    /// <summary>Full description (only with <see cref="UpworkJobSearchRequest.IncludeFullDetails"/>), markers removed.</summary>
    public string? Description { get; init; }

    public UpworkJobType JobType { get; init; }

    public UpworkExperienceLevel ExperienceLevel { get; init; }

    public UpworkBudget? Budget { get; init; }

    public string? HourlyBudgetType { get; init; }

    /// <summary>Free text, e.g. "Less than 1 month".</summary>
    public string? Duration { get; init; }

    /// <summary>Free text; its format differs between actions ("FULL_TIME", "30+ hrs/week").</summary>
    public string? Engagement { get; init; }

    public int? FreelancersToHire { get; init; }

    public IReadOnlyList<string> Skills { get; init; } = [];

    /// <summary>E.g. "20 to 50". Basic plans get only the tier.</summary>
    public string? ProposalsTier { get; init; }

    /// <summary>Exact count, Freelancer Plus only.</summary>
    public int? ProposalCount { get; init; }

    /// <summary>True when this freelancer already applied.</summary>
    public bool? Applied { get; init; }

    public bool? Featured { get; init; }

    /// <summary>Connects needed to apply (smart_search only).</summary>
    public int? ConnectPrice { get; init; }

    public UpworkClientSummary? Client { get; init; }

    public UpworkJobActivity? Activity { get; init; }

    public UpworkPreviousClient? PreviousClient { get; init; }

    /// <summary>
    /// This job's JSON object exactly as returned: a verbatim slice of the page's <see cref="UpworkRawExchange.ResponseJson"/>.
    /// Suitable as a per-job revision payload.
    /// </summary>
    public required string RawJson { get; init; }
}

/// <summary>Budget parsed best-effort from Upwork's display string ("10.00", "$12.00–$28.00/hr").</summary>
public sealed record UpworkBudget
{
    /// <summary>The string exactly as returned.</summary>
    public required string Raw { get; init; }

    public decimal? Min { get; init; }

    public decimal? Max { get; init; }

    public bool IsHourly { get; init; }
}

public sealed record UpworkClientSummary
{
    public string? Country { get; init; }

    /// <summary>Average score freelancers gave this client.</summary>
    public decimal? Rating { get; init; }

    public int? TotalReviews { get; init; }

    /// <summary>Display string, e.g. "$210,479.18".</summary>
    public string? TotalSpent { get; init; }

    public decimal? TotalSpentAmount { get; init; }

    /// <summary>Jobs ever posted (search). Not a hire count.</summary>
    public int? TotalPostedJobs { get; init; }

    /// <summary>Hires (smart_search).</summary>
    public int? TotalHires { get; init; }

    public string? VerificationStatus { get; init; }
}

/// <summary>Hiring funnel of one posting.</summary>
public sealed record UpworkJobActivity
{
    public int? InvitesSent { get; init; }

    public int? InvitedToInterview { get; init; }

    public int? TotalHired { get; init; }

    public int? TotalOffered { get; init; }
}

public sealed record UpworkPreviousClient
{
    public string? CompanyName { get; init; }

    public string? LastContractTitle { get; init; }
}

/// <summary><c>find_jobs</c> <c>action=get</c>: full details of one posting.</summary>
public sealed record UpworkJobDetails : UpworkToolResponse
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    /// <summary>Full description, markers removed.</summary>
    public string? Description { get; init; }

    public string? Url { get; init; }

    /// <summary>Upwork workflow state, e.g. <c>ACTIVE</c>.</summary>
    public required string WorkflowStatus { get; init; }

    [JsonIgnore]
    public bool IsActive => string.Equals(WorkflowStatus, "ACTIVE", StringComparison.OrdinalIgnoreCase);

    public UpworkJobType JobType { get; init; }

    public UpworkExperienceLevel ExperienceLevel { get; init; }

    public string? Category { get; init; }

    public string? CategoryId { get; init; }

    public string? Subcategory { get; init; }

    public string? SubcategoryId { get; init; }

    public decimal? HourlyBudgetMin { get; init; }

    public decimal? HourlyBudgetMax { get; init; }

    public string? HourlyBudgetType { get; init; }

    /// <summary>E.g. <c>PART_TIME</c>.</summary>
    public string? EngagementType { get; init; }

    public decimal? FixedPriceAmount { get; init; }

    public string? FixedPriceCurrency { get; init; }

    public int? PersonsToHire { get; init; }

    public string? ClientCompanyId { get; init; }

    public string? ClientCountry { get; init; }

    public string? ClientState { get; init; }

    public string? ClientTimezone { get; init; }

    public UpworkJobActivity? Activity { get; init; }

    public UpworkClientRecord? ClientRecord { get; init; }

    public UpworkPreferredQualifications? PreferredQualifications { get; init; }

    /// <summary>Markers removed.</summary>
    public IReadOnlyList<string> ScreeningQuestions { get; init; } = [];

    public int? ConnectsCost { get; init; }

    public int? ConnectsBalance { get; init; }

    public bool? CanApply { get; init; }

    public bool? Applied { get; init; }

    /// <summary>The account plan, e.g. "Freelancer Basic".</summary>
    public string? Plan { get; init; }

    public string? ProposalsTier { get; init; }

    public int? ProposalCount { get; init; }
}

/// <summary>The client's hiring record, read live for the job.</summary>
public sealed record UpworkClientRecord
{
    public int? ContractsActive { get; init; }

    public int? ContractsTotal { get; init; }

    public int? FeedbackCount { get; init; }

    public decimal? FeedbackScore { get; init; }

    public decimal? HireRatePercent { get; init; }

    public decimal? HoursTotal { get; init; }

    public int? JobsPosted { get; init; }

    public int? JobsWithHires { get; init; }

    public int? OpenJobs { get; init; }

    public string? SpendTotal { get; init; }

    public decimal? SpendTotalAmount { get; init; }
}

public sealed record UpworkPreferredQualifications
{
    public string? EnglishProficiency { get; init; }

    public bool? HasPortfolio { get; init; }

    public decimal? MinHoursWorked { get; init; }

    public decimal? MinJobSuccessScore { get; init; }

    public bool? RisingTalent { get; init; }
}

/// <summary>Result of checking the server's tool list against what the client relies on.</summary>
public sealed record UpworkToolCatalog
{
    /// <summary>Logical name (<c>find_jobs</c>) to the name the server uses (possibly prefixed).</summary>
    public required IReadOnlyDictionary<string, string> ToolNames { get; init; }

    /// <summary>
    /// <c>find_jobs</c> parameter names the schema declares per action. An action is missing when
    /// the schema has no per-action branch for it (parameters then cannot be checked).
    /// </summary>
    public required IReadOnlyDictionary<string, IReadOnlyList<string>> FindJobsParamsByAction { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>The definitions of the tools the client uses, as returned by <c>tools/list</c>.</summary>
    public required string RawToolsJson { get; init; }

    public required DateTimeOffset VerifiedAt { get; init; }
}
