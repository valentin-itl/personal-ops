namespace JobScout.Worker.Persistence.Entities;

public enum EvaluationDispatchStatus
{
    Pending = 0,
    Dispatched = 1,
    Failed = 2
}

public sealed class EvaluationOutbox
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid JobRevisionId { get; set; }
    public JobRevision JobRevision { get; set; } = null!;

    public required string EvaluationKey { get; set; }

    public EvaluationDispatchStatus Status { get; set; } = EvaluationDispatchStatus.Pending;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? DispatchedAtUtc { get; set; }

    public int AttemptCount { get; set; }
    public string? LastError { get; set; }
}
