using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using JobScout.Worker.Integrations.Upwork.Models;

namespace JobScout.Worker.Scanning;

/// <summary>
/// Identifies the content of a posting that is worth re-evaluating when it changes.
/// Deliberately leaves out what changes on its own (proposal counts, hiring activity, client spend, applied)
/// and what <c>search</c> and <c>smart_search</c> format differently for the same job (engagement text,
/// hourly budget presence, full vs truncated description), so seeing a job through two profiles does not
/// create a new revision each time.
/// </summary>
public static partial class JobFingerprint
{
    private const int Version = 1;

    /// <summary>64 lowercase hex characters (SHA-256).</summary>
    public static string Compute(UpworkJobSummary job)
    {
        var canonical = JsonSerializer.Serialize(new
        {
            v = Version,
            title = Normalize(job.Title),
            snippet = Normalize(job.DescriptionSnippet ?? job.Description),
            jobType = job.JobType.ToString(),
            experience = job.ExperienceLevel.ToString(),
            skills = job.Skills.Select(Normalize).Order(StringComparer.Ordinal).ToArray(),
            fixedBudget = job.JobType == UpworkJobType.Fixed ? job.Budget?.Max ?? job.Budget?.Min : null,
        });

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static string Normalize(string? text) =>
        text is null ? "" : Whitespace().Replace(text, " ").Trim();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
