using System.Globalization;
using System.Text.RegularExpressions;
using JobScout.Worker.Integrations.Upwork.Models;

namespace JobScout.Worker.Integrations.Upwork.Parsing;

internal static partial class UpworkText
{
    private const string UntrustedOpen = "<untrusted_participant_content>";
    private const string UntrustedClose = "</untrusted_participant_content>";

    /// <summary>
    /// Removes the markers Upwork wraps around text written by clients. The raw JSON keeps them;
    /// re-wrap the text before handing it to an LLM, since the markers guard against prompt injection.
    /// </summary>
    public static string? Unwrap(string? text) =>
        text?.Replace(UntrustedOpen, "", StringComparison.Ordinal)
            .Replace(UntrustedClose, "", StringComparison.Ordinal)
            .Trim();

    /// <summary>"$210,479.18" → 210479.18; "10.00" → 10.00.</summary>
    public static decimal? ParseMoney(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var match = NumberPattern().Match(text);
        return match.Success ? ParseNumber(match.Value) : null;
    }

    /// <summary>
    /// Best-effort parse of Upwork's budget display strings: "10.00" (fixed), "$12.00–$28.00/hr",
    /// "$25.00+/hr", "Up to $40.00/hr".
    /// </summary>
    public static UpworkBudget? ParseBudget(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var numbers = NumberPattern().Matches(raw).Select(m => ParseNumber(m.Value)).OfType<decimal>().ToList();
        var isHourly = raw.Contains("/hr", StringComparison.OrdinalIgnoreCase);
        decimal? min = null, max = null;

        if (numbers.Count >= 2)
        {
            min = numbers[0];
            max = numbers[1];
        }
        else if (numbers.Count == 1)
        {
            if (raw.Contains("up to", StringComparison.OrdinalIgnoreCase))
                max = numbers[0];
            else if (raw.Contains('+'))
                min = numbers[0];
            else
                min = max = numbers[0];
        }

        return new UpworkBudget { Raw = raw, Min = min, Max = max, IsHourly = isHourly };
    }

    public static UpworkJobType? ParseJobType(string value) => Letters(value) switch
    {
        "fixed" or "fixedprice" => UpworkJobType.Fixed,
        "hourly" => UpworkJobType.Hourly,
        _ => null,
    };

    /// <summary>Accepts "intermediate", "Intermediate", "INTERMEDIATE", "entry_level", "Entry Level".</summary>
    public static UpworkExperienceLevel? ParseExperienceLevel(string value) => Letters(value) switch
    {
        "entrylevel" or "entry" => UpworkExperienceLevel.EntryLevel,
        "intermediate" => UpworkExperienceLevel.Intermediate,
        "expert" => UpworkExperienceLevel.Expert,
        _ => null,
    };

    private static string Letters(string value) =>
        new string(value.Where(char.IsLetter).ToArray()).ToLowerInvariant();

    private static decimal? ParseNumber(string text) =>
        decimal.TryParse(text.Replace(",", "", StringComparison.Ordinal), NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    [GeneratedRegex(@"\d[\d,]*(?:\.\d+)?")]
    private static partial Regex NumberPattern();
}
