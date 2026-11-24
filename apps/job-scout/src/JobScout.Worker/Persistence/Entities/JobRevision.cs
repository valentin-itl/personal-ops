namespace JobScout.Worker.Persistence.Entities
{
    public sealed class JobRevision
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        public Guid JobId { get; set; }
        public Job Job { get; set; } = null!;

        public required string Fingerprint { get; set; }

        public required string PayloadJson { get; set; }

        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

        public ICollection<EvaluationOutbox> EvaluationRequests { get; set; } = [];
    }
}