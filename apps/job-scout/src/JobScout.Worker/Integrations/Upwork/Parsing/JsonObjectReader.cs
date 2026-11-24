using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using JobScout.Worker.Integrations.Upwork.Models;

namespace JobScout.Worker.Integrations.Upwork.Parsing;

/// <summary>
/// Collects problems found while reading one payload. Errors (required data missing or mistyped) make the
/// call fail with <see cref="UpworkContractMismatchException"/>; drift (unknown fields, optional fields with
/// an unexpected type, unknown enum values) is reported but does not fail the call.
/// </summary>
internal sealed partial class UpworkParseContext
{
    private const int MaxErrors = 25;

    private readonly List<string> _errors = [];
    private readonly SortedSet<string> _unknown = new(StringComparer.Ordinal);
    private readonly List<string> _warnings = [];
    private int _suppressedErrors;

    public bool HasErrors => _errors.Count > 0;

    public IReadOnlyList<string> Errors =>
        _suppressedErrors == 0 ? _errors : [.. _errors, $"... and {_suppressedErrors} more"];

    public void Error(string path, string message)
    {
        if (_errors.Count < MaxErrors)
            _errors.Add($"{path}: {message}");
        else
            _suppressedErrors++;
    }

    public void Warn(string path, string message)
    {
        var entry = $"{NormalizePath(path)}: {message}";
        if (!_warnings.Contains(entry))
            _warnings.Add(entry);
    }

    public void Unknown(string path) => _unknown.Add(NormalizePath(path));

    public UpworkResponseDiagnostics ToDiagnostics() =>
        _unknown.Count == 0 && _warnings.Count == 0
            ? UpworkResponseDiagnostics.None
            : new UpworkResponseDiagnostics { UnknownFields = [.. _unknown], Warnings = [.. _warnings] };

    /// <summary><c>$.jobs[3].id</c> becomes <c>$.jobs[*].id</c> so one drift is reported once, not once per item.</summary>
    internal static string NormalizePath(string path) => IndexPattern().Replace(path, "[*]");

    [GeneratedRegex(@"\[\d+\]")]
    private static partial Regex IndexPattern();
}

/// <summary>
/// Reads one JSON object, recording every property it touches so that <see cref="Complete"/> can report
/// properties nobody asked for. Required readers record errors; optional readers record drift and return null.
/// JSON <c>null</c> is treated as absent.
/// </summary>
internal sealed class JsonObjectReader
{
    private readonly JsonElement _object;
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    private JsonObjectReader(JsonElement obj, string path, UpworkParseContext context)
    {
        _object = obj;
        Path = path;
        Context = context;
    }

    public string Path { get; }

    public UpworkParseContext Context { get; }

    /// <summary>The object's JSON exactly as it appeared in the payload.</summary>
    public string RawText => _object.GetRawText();

    public static JsonObjectReader? ForRoot(JsonElement root, UpworkParseContext context)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            context.Error("$", $"expected a JSON object, got {Describe(root)}");
            return null;
        }

        return new JsonObjectReader(root, "$", context);
    }

    public static JsonObjectReader? ForElement(JsonElement element, string path, UpworkParseContext context)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            context.Error(path, $"expected an object, got {Describe(element)}");
            return null;
        }

        return new JsonObjectReader(element, path, context);
    }

    /// <summary>Marks properties as known without mapping them (they stay available in the raw JSON).</summary>
    public void Known(params string[] names)
    {
        foreach (var name in names)
            _seen.Add(name);
    }

    /// <summary>Reports every property of this object that no reader touched.</summary>
    public void Complete()
    {
        foreach (var property in _object.EnumerateObject())
        {
            if (!_seen.Contains(property.Name))
                Context.Unknown($"{Path}.{property.Name}");
        }
    }

    public bool Has(string name) => TryGet(name, out _);

    /// <summary>The JSON kind of a property, or null when it is absent or null.</summary>
    public JsonValueKind? KindOf(string name) => TryGet(name, out var value) ? value.ValueKind : null;

    public string RequiredString(string name)
    {
        if (!TryGet(name, out var value))
        {
            Context.Error(Child(name), "required string is missing");
            return "";
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            Context.Error(Child(name), $"expected string, got {Describe(value)}");
            return "";
        }

        var text = value.GetString()!;
        if (string.IsNullOrWhiteSpace(text))
            Context.Error(Child(name), "required string is empty");
        return text;
    }

    public string? String(string name)
    {
        if (!TryGet(name, out var value))
            return null;
        if (value.ValueKind == JsonValueKind.String)
            return value.GetString();
        Context.Warn(Child(name), $"expected string, got {Describe(value)}; value ignored");
        return null;
    }

    /// <summary>A display value that is sometimes a number (e.g. a budget): returns the number's raw text.</summary>
    public string? StringOrNumber(string name)
    {
        if (!TryGet(name, out var value))
            return null;
        if (value.ValueKind == JsonValueKind.String)
            return value.GetString();
        if (value.ValueKind == JsonValueKind.Number)
            return value.GetRawText();
        Context.Warn(Child(name), $"expected string or number, got {Describe(value)}; value ignored");
        return null;
    }

    public DateTimeOffset RequiredDate(string name)
    {
        var text = RequiredString(name);
        if (text.Length == 0)
            return default;
        if (TryParseDate(text, out var date))
            return date;
        Context.Error(Child(name), $"'{text}' is not a valid timestamp");
        return default;
    }

    public DateTimeOffset? Date(string name)
    {
        var text = String(name);
        if (text is null)
            return null;
        if (TryParseDate(text, out var date))
            return date;
        Context.Warn(Child(name), $"'{text}' is not a valid timestamp; value ignored");
        return null;
    }

    public int? Int(string name)
    {
        var number = Decimal(name);
        if (number is null)
            return null;
        if (number == Math.Truncate(number.Value) && number >= int.MinValue && number <= int.MaxValue)
            return (int)number.Value;
        Context.Warn(Child(name), $"expected an integer, got {number}; value ignored");
        return null;
    }

    /// <summary>A JSON number, or a string holding one.</summary>
    public decimal? Decimal(string name)
    {
        if (!TryGet(name, out var value))
            return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number))
            return number;
        if (value.ValueKind == JsonValueKind.String
            && decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out number))
            return number;
        Context.Warn(Child(name), $"expected a number, got {Describe(value)}; value ignored");
        return null;
    }

    public bool? Bool(string name)
    {
        if (!TryGet(name, out var value))
            return null;
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return value.GetBoolean();
        Context.Warn(Child(name), $"expected boolean, got {Describe(value)}; value ignored");
        return null;
    }

    /// <summary>Optional string array; non-string items are dropped with a warning.</summary>
    public IReadOnlyList<string> StringArray(string name)
    {
        if (!TryGet(name, out var value))
            return [];
        if (value.ValueKind != JsonValueKind.Array)
        {
            Context.Warn(Child(name), $"expected array, got {Describe(value)}; value ignored");
            return [];
        }

        var items = new List<string>();
        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
                items.Add(item.GetString()!);
            else
                Context.Warn($"{Child(name)}[{index}]", $"expected string, got {Describe(item)}; item ignored");
            index++;
        }

        return items;
    }

    /// <summary>Parses a string into an enum; unknown values become the default (Unknown) with a warning.</summary>
    public TEnum Enum<TEnum>(string name, Func<string, TEnum?> parse) where TEnum : struct, System.Enum
    {
        var text = String(name);
        if (text is null)
            return default;
        if (parse(text) is { } parsed)
            return parsed;
        Context.Warn(Child(name), $"unknown value '{text}'");
        return default;
    }

    public JsonObjectReader? Object(string name)
    {
        if (!TryGet(name, out var value))
            return null;
        if (value.ValueKind == JsonValueKind.Object)
            return new JsonObjectReader(value, Child(name), Context);
        Context.Warn(Child(name), $"expected object, got {Describe(value)}; value ignored");
        return null;
    }

    public JsonObjectReader? RequiredObject(string name)
    {
        if (!TryGet(name, out var value))
        {
            Context.Error(Child(name), "required object is missing");
            return null;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            Context.Error(Child(name), $"expected object, got {Describe(value)}");
            return null;
        }

        return new JsonObjectReader(value, Child(name), Context);
    }

    /// <summary>Required array of objects; each non-object item is an error.</summary>
    public IReadOnlyList<JsonObjectReader> RequiredObjectArray(string name)
    {
        if (!TryGet(name, out var value))
        {
            Context.Error(Child(name), "required array is missing");
            return [];
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            Context.Error(Child(name), $"expected array, got {Describe(value)}");
            return [];
        }

        var readers = new List<JsonObjectReader>();
        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            if (ForElement(item, $"{Child(name)}[{index}]", Context) is { } reader)
                readers.Add(reader);
            index++;
        }

        return readers;
    }

    /// <summary>The raw JSON of a property that is present and not empty (an empty array, object or string counts as absent).</summary>
    public string? RawIfNotEmpty(string name)
    {
        if (!TryGet(name, out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.Array when value.GetArrayLength() == 0 => null,
            JsonValueKind.Object when !value.EnumerateObject().Any() => null,
            JsonValueKind.String when string.IsNullOrWhiteSpace(value.GetString()) => null,
            JsonValueKind.False => null,
            _ => value.GetRawText(),
        };
    }

    private bool TryGet(string name, out JsonElement value)
    {
        _seen.Add(name);
        if (_object.TryGetProperty(name, out value) && value.ValueKind != JsonValueKind.Null)
            return true;
        value = default;
        return false;
    }

    private string Child(string name) => $"{Path}.{name}";

    private static bool TryParseDate(string text, out DateTimeOffset date) =>
        DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out date);

    private static string Describe(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => "object",
        JsonValueKind.Array => "array",
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Null => "null",
        _ => value.ValueKind.ToString(),
    };
}
