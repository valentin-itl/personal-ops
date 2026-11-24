using System.Text.Json;
using System.Text.Json.Nodes;
using JobScout.Worker.Integrations.Upwork.Models;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace JobScout.Worker.Integrations.Upwork.Parsing;

/// <summary>
/// Checks <c>tools/list</c> against what the client relies on: both tools exist, <c>find_jobs</c> still offers
/// its actions and top-level parameters, and no new required parameter appeared. Per-action parameter names
/// are recorded so each call can flag a parameter the schema no longer declares.
/// </summary>
internal static class UpworkToolCatalogValidator
{
    public const string ListAccountsTool = "list_accounts";
    public const string FindJobsTool = "find_jobs";
    public const string ToolsListName = "tools/list";

    public static readonly string[] FindJobsActions = ["search", "get", "smart_search"];

    private static readonly string[] FindJobsArguments = ["action", "org_uid", "params"];

    public static UpworkToolCatalog Validate(IReadOnlyList<Tool> tools, DateTimeOffset startedAt, DateTimeOffset completedAt)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var used = new List<Tool>();
        Tool? findJobs = null;

        foreach (var logical in new[] { ListAccountsTool, FindJobsTool })
        {
            // The server may prefix tool names (e.g. "upwork__find_jobs").
            var tool = tools.FirstOrDefault(t => t.Name == logical)
                       ?? tools.FirstOrDefault(t => t.Name.EndsWith("__" + logical, StringComparison.Ordinal));
            if (tool is null)
            {
                errors.Add($"{ToolsListName}: tool '{logical}' is missing");
                continue;
            }

            names[logical] = tool.Name;
            used.Add(tool);
            if (logical == FindJobsTool)
                findJobs = tool;
        }

        var paramsByAction = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        if (findJobs is not null)
            CheckFindJobsSchema(findJobs.InputSchema, errors, warnings, paramsByAction);

        var rawToolsJson = SerializeTools(used);
        if (errors.Count > 0)
        {
            var raw = new UpworkRawExchange
            {
                Tool = ToolsListName,
                RequestJson = "{}",
                ResponseJson = rawToolsJson,
                PayloadSource = UpworkPayloadSource.StructuredContent,
                StartedAt = startedAt,
                CompletedAt = completedAt,
            };
            throw new UpworkContractMismatchException(
                $"Upwork MCP tools no longer match the client: {string.Join("; ", errors)}",
                raw)
            {
                Issues = errors,
            };
        }

        return new UpworkToolCatalog
        {
            ToolNames = names,
            FindJobsParamsByAction = paramsByAction,
            Warnings = warnings,
            RawToolsJson = rawToolsJson,
            VerifiedAt = completedAt,
        };
    }

    private static void CheckFindJobsSchema(
        JsonElement schema,
        List<string> errors,
        List<string> warnings,
        Dictionary<string, IReadOnlyList<string>> paramsByAction)
    {
        const string path = "find_jobs.inputSchema";

        if (Prop(schema, "properties") is not { ValueKind: JsonValueKind.Object } properties)
        {
            errors.Add($"{path}.properties is missing");
            return;
        }

        foreach (var name in FindJobsArguments)
        {
            if (Prop(properties, name) is null)
                errors.Add($"{path}.properties.{name} is missing");
        }

        if (Prop(Prop(properties, "action"), "enum") is { ValueKind: JsonValueKind.Array } actionEnum)
        {
            var offered = StringSet(actionEnum);
            foreach (var action in FindJobsActions)
            {
                if (!offered.Contains(action))
                    errors.Add($"{path}.properties.action.enum no longer offers '{action}'");
            }
        }
        else if (Prop(properties, "action") is not null)
        {
            warnings.Add($"{path}.properties.action has no enum; the actions cannot be verified");
        }

        if (Prop(schema, "required") is { ValueKind: JsonValueKind.Array } required)
        {
            foreach (var name in StringSet(required).Except(FindJobsArguments))
                errors.Add($"{path}.required now includes '{name}', which the client does not send");
        }

        if (Prop(schema, "allOf") is { ValueKind: JsonValueKind.Array } branches)
        {
            foreach (var branch in branches.EnumerateArray())
            {
                var action = Prop(Prop(Prop(Prop(branch, "if"), "properties"), "action"), "const");
                var declared = Prop(Prop(Prop(Prop(Prop(branch, "then"), "properties"), "params"), "properties"), null);
                if (action is { ValueKind: JsonValueKind.String } a && declared is { ValueKind: JsonValueKind.Object } d)
                    paramsByAction[a.GetString()!] = d.EnumerateObject().Select(p => p.Name).ToList();
            }
        }

        foreach (var action in FindJobsActions)
        {
            if (!paramsByAction.ContainsKey(action))
                warnings.Add($"{path}: no parameter list for action '{action}'; its parameters cannot be checked");
        }
    }

    /// <summary>Property of an object, or null when the element is not an object or lacks it. A null name returns the element itself.</summary>
    private static JsonElement? Prop(JsonElement? element, string? name)
    {
        if (element is not { ValueKind: JsonValueKind.Object } obj)
            return null;
        if (name is null)
            return obj;
        return obj.TryGetProperty(name, out var value) ? value : null;
    }

    private static HashSet<string> StringSet(JsonElement array) =>
        array.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!)
            .ToHashSet(StringComparer.Ordinal);

    private static string SerializeTools(IEnumerable<Tool> tools)
    {
        var array = new JsonArray();
        foreach (var tool in tools)
            array.Add(JsonSerializer.SerializeToNode(tool, McpJsonUtilities.DefaultOptions));
        return array.ToJsonString();
    }
}
