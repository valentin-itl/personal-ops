namespace JobScout.Worker.Persistence.Entities
{
    public enum JobAvailability
    {
        Unknown = 0,
        Open = 1,
        Closed = 2,
        Unavailable = 3
    }

    public sealed class Job
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        public Guid SourceId { get; set; }
        public Source Source { get; set; } = null!;

        public required string ExternalId { get; set; }

        public JobAvailability Availability { get; set; } = JobAvailability.Unknown;

        public DateTime FirstSeenAtUtc { get; set; } = DateTime.UtcNow;
        public DateTime LastSeenAtUtc { get; set; } = DateTime.UtcNow;

        public string? CurrentFingerprint { get; set; }

        public ICollection<JobRevision> Revisions { get; set; } = [];
    }
}