using System.Text.Json;
using System.Text.Json.Nodes;

namespace JobScout.Worker.Integrations.Upwork;

/// <summary>
/// An OAuth client registered with Upwork (RFC 7591). <see cref="Json"/> is the registration response without
/// secrets; only <c>client_id</c> is required, so a hand-written <c>{"client_id":"..."}</c> works too.
/// </summary>
public sealed record UpworkClientRegistration
{
    private static readonly string[] SecretFields = ["registration_access_token", "client_secret"];

    public required string ClientId { get; init; }

    /// <summary>Redirect URIs the client was registered with; empty when unknown (then any is assumed to work).</summary>
    public IReadOnlyList<string> RedirectUris { get; init; } = [];

    /// <summary>The registration document as stored: provider fields kept, secrets removed.</summary>
    public required string Json { get; init; }

    public bool AllowsRedirect(Uri redirectUri) =>
        RedirectUris.Count == 0 || RedirectUris.Contains(redirectUri.ToString(), StringComparer.Ordinal);

    /// <summary>Parses a registration response or a stored registration. Throws <see cref="FormatException"/> when unusable.</summary>
    public static UpworkClientRegistration Parse(string json)
    {
        JsonObject document;
        try
        {
            document = JsonNode.Parse(json) as JsonObject
                ?? throw new FormatException("expected a JSON object");
        }
        catch (JsonException ex)
        {
            throw new FormatException($"not valid JSON ({ex.Message})", ex);
        }

        if (document["client_id"] is not JsonValue idValue
            || idValue.GetValueKind() != JsonValueKind.String
            || string.IsNullOrWhiteSpace(idValue.GetValue<string>()))
        {
            throw new FormatException("client_id is missing or not a string");
        }

        var redirectUris = document["redirect_uris"] is JsonArray uris
            ? uris.Where(u => u?.GetValueKind() == JsonValueKind.String).Select(u => u!.GetValue<string>()).ToList()
            : [];

        foreach (var secret in SecretFields)
            document.Remove(secret);

        return new UpworkClientRegistration
        {
            ClientId = idValue.GetValue<string>().Trim(),
            RedirectUris = redirectUris,
            Json = document.ToJsonString(),
        };
    }
}

/// <summary>Where each source's OAuth client registration is kept (the job-scout database).</summary>
public interface IUpworkClientRegistrationStore
{
    /// <summary>The source's registration, or null when it has none yet.</summary>
    Task<UpworkClientRegistration?> LoadAsync(Guid sourceId, CancellationToken cancellationToken);

    Task SaveAsync(Guid sourceId, UpworkClientRegistration registration, CancellationToken cancellationToken);
}
