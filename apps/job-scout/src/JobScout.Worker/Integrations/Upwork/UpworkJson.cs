using System.Text.Json;
using System.Text.Json.Serialization;

namespace JobScout.Worker.Integrations.Upwork;

/// <summary>
/// Serializer settings for the client's own models (requests, responses, raw exchanges):
/// camelCase, enums as strings, nulls omitted. Round-trips every model.
/// </summary>
public static class UpworkJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
