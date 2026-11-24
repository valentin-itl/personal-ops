using System.Text.Json;
using JobScout.Worker.Integrations.Upwork.Models;
using ModelContextProtocol.Protocol;

namespace JobScout.Worker.Integrations.Upwork.Parsing;

/// <summary>The payload of one tool result, verbatim, plus its parsed JSON when it is JSON.</summary>
internal sealed record UpworkPayload(string? Text, UpworkPayloadSource Source, JsonElement? Json)
{
    public static UpworkPayload From(CallToolResult result)
    {
        if (result.StructuredContent is { } structured && structured.ValueKind != JsonValueKind.Undefined)
            return new UpworkPayload(structured.GetRawText(), UpworkPayloadSource.StructuredContent, structured);

        var texts = result.Content?.OfType<TextContentBlock>().Select(b => b.Text).ToList() ?? [];
        if (texts.Count == 0)
            return new UpworkPayload(null, UpworkPayloadSource.None, null);

        var text = string.Concat(texts);
        try
        {
            using var document = JsonDocument.Parse(text);
            return new UpworkPayload(text, UpworkPayloadSource.TextContent, document.RootElement.Clone());
        }
        catch (JsonException)
        {
            return new UpworkPayload(text, UpworkPayloadSource.TextContent, null);
        }
    }
}

/// <summary>Search results plus anything that means the request was not applied as sent.</summary>
internal sealed record ParsedSearchPage(UpworkJobSearchPage Page, string? FiltersIgnored, string? FiltersRejected);

/// <summary>Maps payloads onto the typed models, checking the contract as it goes.</summary>
internal static class UpworkResponseParser
{
    public static string? ReadTraceId(JsonElement? root) =>
        root is { ValueKind: JsonValueKind.Object } r
        && r.TryGetProperty("trace_id", out var v)
        && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    /// <summary>True when a payload without <c>isError</c> still describes an error.</summary>
    public static bool LooksLikeError(JsonElement? root) =>
        root is { ValueKind: JsonValueKind.Object } r
        && ((r.TryGetProperty("status", out var status)
             && status.ValueKind == JsonValueKind.String
             && string.Equals(status.GetString(), "error", StringComparison.OrdinalIgnoreCase))
            || (r.TryGetProperty("error_code", out var code) && code.ValueKind == JsonValueKind.String));

    public static UpworkAccountList ParseAccounts(JsonElement root, UpworkRawExchange raw, UpworkParseContext ctx)
    {
        var accounts = new List<UpworkAccount>();
        if (JsonObjectReader.ForRoot(root, ctx) is { } r)
        {
            ReadStatus(r, required: false);
            r.Known("trace_id");
            foreach (var a in r.RequiredObjectArray("accounts"))
            {
                accounts.Add(new UpworkAccount
                {
                    OrgUid = a.RequiredString("org_uid"),
                    Name = a.String("name"),
                    Role = a.String("role"),
                    RoleLabel = a.String("role_label"),
                });
                a.Complete();
            }

            r.Complete();
        }

        return new UpworkAccountList { Accounts = accounts, Raw = raw, Diagnostics = ctx.ToDiagnostics() };
    }

    public static ParsedSearchPage ParseJobSearchPage(JsonElement root, UpworkRawExchange raw, UpworkParseContext ctx)
    {
        var jobs = new List<UpworkJobSummary>();
        string? nextCursor = null, mode = null, filtersIgnored = null, filtersRejected = null;
        var hasMore = false;
        int? totalMatching = null;
        IReadOnlyList<string> warnings = [];

        if (JsonObjectReader.ForRoot(root, ctx) is { } r)
        {
            ReadStatus(r, required: true);
            r.Known("trace_id", "client_rating_basis", "mode_note");

            foreach (var job in r.RequiredObjectArray("jobs"))
                jobs.Add(ParseJobSummary(job));

            // search reports paging in pageInfo (and next_cursor); smart_search only in next_cursor/hasMore.
            var pageInfo = r.Object("pageInfo");
            var endCursor = pageInfo?.String("endCursor");
            var hasNextPage = pageInfo?.Bool("hasNextPage");
            pageInfo?.Complete();

            var flatCursor = r.String("next_cursor");
            var flatHasMore = r.Bool("hasMore");

            nextCursor = !string.IsNullOrEmpty(endCursor) ? endCursor : flatCursor;
            if (hasNextPage is null && flatHasMore is null)
                ctx.Error("$.hasMore", "paging flag is missing (neither hasMore nor pageInfo.hasNextPage)");
            hasMore = hasNextPage ?? flatHasMore ?? false;

            if (hasMore && string.IsNullOrEmpty(nextCursor))
                ctx.Error("$.next_cursor", "more pages are announced but no cursor was returned");
            if (!string.IsNullOrEmpty(endCursor) && !string.IsNullOrEmpty(flatCursor) && endCursor != flatCursor)
                ctx.Warn("$.next_cursor", $"differs from pageInfo.endCursor ('{flatCursor}' vs '{endCursor}'); using endCursor");
            if (hasNextPage is not null && flatHasMore is not null && hasNextPage != flatHasMore)
                ctx.Warn("$.hasMore", "differs from pageInfo.hasNextPage; using hasNextPage");

            totalMatching = r.Int("total_matching");
            mode = r.String("mode");
            warnings = ReadWarnings(r, "warning");
            filtersIgnored = r.RawIfNotEmpty("filters_ignored");
            filtersRejected = r.RawIfNotEmpty("filters_rejected");
            r.Complete();
        }

        var page = new UpworkJobSearchPage
        {
            Jobs = jobs,
            NextCursor = nextCursor,
            HasMore = hasMore,
            TotalMatching = totalMatching,
            Mode = mode,
            ServerWarnings = warnings,
            Raw = raw,
            Diagnostics = ctx.ToDiagnostics(),
        };
        return new ParsedSearchPage(page, filtersIgnored, filtersRejected);
    }

    public static UpworkJobDetails ParseJobDetails(JsonElement root, UpworkRawExchange raw, UpworkParseContext ctx)
    {
        var r = JsonObjectReader.ForRoot(root, ctx);
        if (r is null)
            return new UpworkJobDetails { Id = "", Title = "", WorkflowStatus = "", Raw = raw, Diagnostics = ctx.ToDiagnostics() };

        ReadStatus(r, required: true);
        r.Known(
            "trace_id",
            "bid_stats_available", "bid_stats_note", "bid_stats_basis",
            "client_feedback", "client_work_history", "client_open_jobs",
            "proposals_tier_unavailable", "unanswered_invites_unavailable", "connects_cost_unavailable",
            "preferred_locations", "your_location");

        var data = r.RequiredObject("data");
        var posting = data?.RequiredObject("marketplaceJobPosting");
        data?.Complete();

        string id = "", title = "", status = "";
        string? description = null, url = null;
        string? category = null, categoryId = null, subcategory = null, subcategoryId = null;
        string? hourlyBudgetType = null, engagementType = null, fixedCurrency = null;
        string? clientCompanyId = null, clientCountry = null, clientState = null, clientTimezone = null;
        decimal? hourlyMin = null, hourlyMax = null, fixedAmount = null;
        int? personsToHire = null;
        var jobType = UpworkJobType.Unknown;
        var experience = UpworkExperienceLevel.Unknown;
        UpworkJobActivity? activity = null;

        if (posting is not null)
        {
            id = posting.RequiredString("id");
            url = posting.String("url");

            if (posting.RequiredObject("content") is { } content)
            {
                title = UpworkText.Unwrap(content.RequiredString("title")) ?? "";
                description = UpworkText.Unwrap(content.String("description"));
                content.Complete();
            }

            if (posting.RequiredObject("workFlowState") is { } workflow)
            {
                status = workflow.RequiredString("status");
                workflow.Complete();
            }

            if (posting.Object("classification") is { } classification)
            {
                if (classification.Object("category") is { } c)
                {
                    category = c.String("preferredLabel");
                    categoryId = c.String("id");
                    c.Complete();
                }

                if (classification.Object("subCategory") is { } s)
                {
                    subcategory = s.String("preferredLabel");
                    subcategoryId = s.String("id");
                    s.Complete();
                }

                classification.Complete();
            }

            if (posting.Object("contractTerms") is { } terms)
            {
                jobType = terms.Enum("contractType", UpworkText.ParseJobType);
                experience = terms.Enum("experienceLevel", UpworkText.ParseExperienceLevel);
                personsToHire = terms.Int("personsToHire");

                if (terms.Object("hourlyContractTerms") is { } hourly)
                {
                    engagementType = hourly.String("engagementType");
                    hourlyMin = hourly.Decimal("hourlyBudgetMin");
                    hourlyMax = hourly.Decimal("hourlyBudgetMax");
                    hourlyBudgetType = hourly.String("hourlyBudgetType");
                    hourly.Complete();
                }

                if (terms.Object("fixedPriceContractTerms") is { } fixedTerms)
                {
                    if (fixedTerms.Object("amount") is { } amount)
                    {
                        fixedAmount = amount.Decimal("rawValue");
                        fixedCurrency = amount.String("currency");
                        amount.Known("displayValue");
                        amount.Complete();
                    }

                    fixedTerms.Complete();
                }

                terms.Complete();
            }

            if (posting.Object("clientCompanyPublic") is { } company)
            {
                clientCompanyId = company.String("id");
                clientState = company.String("state");
                clientTimezone = company.String("timezone");
                if (company.Object("country") is { } country)
                {
                    clientCountry = country.String("name");
                    country.Complete();
                }

                company.Complete();
            }

            if (posting.Object("activityStat") is { } stats)
            {
                if (stats.Object("jobActivity") is { } a)
                {
                    activity = new UpworkJobActivity
                    {
                        InvitesSent = a.Int("invitesSent"),
                        InvitedToInterview = a.Int("totalInvitedToInterview"),
                        TotalHired = a.Int("totalHired"),
                        TotalOffered = a.Int("totalOffered"),
                    };
                    a.Complete();
                }

                stats.Known("applicationsBidStats");
                stats.Complete();
            }

            posting.Complete();
        }

        UpworkClientRecord? clientRecord = null;
        if (r.Object("client_record") is { } record)
        {
            var spend = record.StringOrNumber("spend_total");
            clientRecord = new UpworkClientRecord
            {
                ContractsActive = record.Int("contracts_active"),
                ContractsTotal = record.Int("contracts_total"),
                FeedbackCount = record.Int("feedback_count"),
                FeedbackScore = record.Decimal("feedback_score"),
                HireRatePercent = record.Decimal("hire_rate_percent"),
                HoursTotal = record.Decimal("hours_total"),
                JobsPosted = record.Int("jobs_posted"),
                JobsWithHires = record.Int("jobs_with_hires"),
                OpenJobs = record.Int("open_jobs"),
                SpendTotal = spend,
                SpendTotalAmount = UpworkText.ParseMoney(spend),
            };
            record.Complete();
        }

        UpworkPreferredQualifications? qualifications = null;
        if (r.Object("preferred_qualifications") is { } q)
        {
            qualifications = new UpworkPreferredQualifications
            {
                EnglishProficiency = q.String("english_proficiency"),
                HasPortfolio = q.Bool("has_portfolio"),
                MinHoursWorked = q.Decimal("min_hours_worked"),
                MinJobSuccessScore = q.Decimal("min_job_success_score"),
                RisingTalent = q.Bool("rising_talent"),
            };
            // Documented but conditional; kept in the raw JSON.
            q.Known("contractor_type", "earnings", "languages", "tests", "location", "locations");
            q.Complete();
        }

        var details = new UpworkJobDetails
        {
            Id = id,
            Title = title,
            Description = description,
            Url = url,
            WorkflowStatus = status,
            JobType = jobType,
            ExperienceLevel = experience,
            Category = category,
            CategoryId = categoryId,
            Subcategory = subcategory,
            SubcategoryId = subcategoryId,
            HourlyBudgetMin = hourlyMin,
            HourlyBudgetMax = hourlyMax,
            HourlyBudgetType = hourlyBudgetType,
            EngagementType = engagementType,
            FixedPriceAmount = fixedAmount,
            FixedPriceCurrency = fixedCurrency,
            PersonsToHire = personsToHire,
            ClientCompanyId = clientCompanyId,
            ClientCountry = clientCountry,
            ClientState = clientState,
            ClientTimezone = clientTimezone,
            Activity = activity,
            ClientRecord = clientRecord,
            PreferredQualifications = qualifications,
            ScreeningQuestions = r.StringArray("screening_questions").Select(t => UpworkText.Unwrap(t)!).ToList(),
            ConnectsCost = r.Int("connects_cost"),
            ConnectsBalance = r.Int("connects_balance"),
            CanApply = r.Bool("can_apply"),
            Applied = r.Bool("applied"),
            Plan = r.String("plan"),
            ProposalsTier = r.String("proposals_tier"),
            ProposalCount = r.Int("proposal_count"),
            Raw = raw,
        };
        r.Complete();

        return details with { Diagnostics = ctx.ToDiagnostics() };
    }

    private static UpworkJobSummary ParseJobSummary(JsonObjectReader j)
    {
        var budget = j.StringOrNumber("budget");

        UpworkClientSummary? client = null;
        if (j.Object("client") is { } c)
        {
            var spent = c.StringOrNumber("total_spent");
            client = new UpworkClientSummary
            {
                Country = c.String("country"),
                Rating = c.Decimal("rating"),
                TotalReviews = c.Int("total_reviews"),
                TotalSpent = spent,
                TotalSpentAmount = UpworkText.ParseMoney(spent),
                TotalPostedJobs = c.Int("total_posted_jobs"),
                TotalHires = c.Int("total_hires"),
                VerificationStatus = c.String("verification_status"),
            };
            c.Complete();
        }

        UpworkJobActivity? activity = null;
        if (j.Object("job_activity") is { } a)
        {
            activity = new UpworkJobActivity
            {
                InvitesSent = a.Int("invites_sent"),
                InvitedToInterview = a.Int("invited_to_interview"),
                TotalHired = a.Int("total_hired"),
                TotalOffered = a.Int("total_offered"),
            };
            a.Complete();
        }

        UpworkPreviousClient? previous = null;
        if (j.Object("previous_client") is { } p)
        {
            previous = new UpworkPreviousClient
            {
                CompanyName = p.String("company_name"),
                LastContractTitle = p.String("last_contract_title"),
            };
            p.Complete();
        }

        var summary = new UpworkJobSummary
        {
            Id = j.RequiredString("id"),
            Title = UpworkText.Unwrap(j.RequiredString("title")) ?? "",
            Url = j.RequiredString("url"),
            PublishedAt = j.RequiredDate("published_date"),
            CreatedAt = j.Date("created_date"),
            DescriptionSnippet = UpworkText.Unwrap(j.String("description_snippet")),
            Description = UpworkText.Unwrap(j.String("description")),
            JobType = j.Enum("job_type", UpworkText.ParseJobType),
            ExperienceLevel = j.Enum("experience_level", UpworkText.ParseExperienceLevel),
            Budget = UpworkText.ParseBudget(budget),
            HourlyBudgetType = j.String("hourly_budget_type"),
            Duration = j.String("duration"),
            Engagement = j.String("engagement"),
            FreelancersToHire = j.Int("freelancers_to_hire"),
            Skills = j.StringArray("skills"),
            ProposalsTier = j.String("proposals_tier"),
            ProposalCount = j.Int("proposal_count"),
            Applied = j.Bool("applied"),
            Featured = j.Bool("featured"),
            ConnectPrice = j.Int("connect_price"),
            Client = client,
            Activity = activity,
            PreviousClient = previous,
            RawJson = j.RawText,
        };

        j.Known("job_activity_note");
        j.Complete();
        return summary;
    }

    private static void ReadStatus(JsonObjectReader r, bool required)
    {
        var status = required ? r.RequiredString("status") : r.String("status");
        if (!string.IsNullOrEmpty(status) && !string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase))
            r.Context.Error($"{r.Path}.status", $"expected 'ok', got '{status}'");
    }

    private static IReadOnlyList<string> ReadWarnings(JsonObjectReader r, string name)
    {
        return r.KindOf(name) switch
        {
            null => [],
            JsonValueKind.Array => r.StringArray(name),
            _ => r.StringOrNumber(name) is { } single ? [single] : [],
        };
    }
}
