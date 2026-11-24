namespace JobScout.Worker.Persistence.Entities;

public enum SourceHealth
{
    Healthy,
    Degraded,
    AuthRequired,
    ContractMismatch,
    Unavailable,
    Disabled
}

public sealed class Source
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Human-readable name, e.g. "Upwork".</summary>
    public required string Name { get; set; }

    /// <summary>
    /// The OAuth client registration this source signs in with, as JSON: the provider's registration response
    /// (RFC 7591) without secrets. Must contain <c>client_id</c>; <c>redirect_uris</c> is checked when present.
    /// Filled automatically on first sign-in when empty.
    /// </summary>
    public string? OAuthClientJson { get; set; }

    public SourceHealth Health { get; set; } = SourceHealth.Healthy;

    public string? ErrorMessage { get; set; }

    public ICollection<Job> Jobs { get; set; } = [];
    public ICollection<SourceScan> SourceScans { get; set; } = [];
}
