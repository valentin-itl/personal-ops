namespace JobScout.Contracts
{
    public record SourceScanRequest(
        string SourceId,
        string SearchProfileId);

    public record SourceScanResult(
        string SourceId,
        int NewJobs,
        int UpdatedJobs);
}
