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

    public SourceHealth Health { get; set; } = SourceHealth.Healthy;

    public string? ErrorMessage { get; set; }

    public ICollection<Job> Jobs { get; set; } = [];
    public ICollection<SourceScan> SourceScans { get; set; } = [];
}
