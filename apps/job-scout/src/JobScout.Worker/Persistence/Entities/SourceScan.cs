namespace JobScout.Worker.Persistence.Entities;

public enum SourceScanStatus
{
    Running = 0,
    Completed = 1,
    Failed = 2
}

public sealed class SourceScan
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid SourceId { get; set; }
    public Source Source { get; set; } = null!;
    
    public required string SearchProfileId { get; set; }

    public SourceScanStatus Status { get; set; } = SourceScanStatus.Running;

    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAtUtc { get; set; }

    public int ObservedCount { get; set; }
    public int CreatedCount { get; set; }
    public int UpdatedCount { get; set; }
    public int UnchangedCount { get; set; }
    public int EvaluationsQueuedCount { get; set; }

    public string? ErrorMessage { get; set; }
}
