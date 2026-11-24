using JobScout.Contracts;
using JobScout.Worker.Scanning;
using Temporalio.Activities;

namespace JobScout.Worker.Activities
{
    public class SourceScanActivities(UpworkSourceScanner scanner)
    {
        /// <summary>
        /// Scans one source with one search profile and persists what it finds.
        /// Failures arrive as <see cref="Temporalio.Exceptions.ApplicationFailureException"/>: Upwork ones carry the
        /// exception class as ErrorType (e.g. UpworkAuthRequiredException, non-retryable) and a retry delay when throttled;
        /// configuration problems use <see cref="SourceScanFailureTypes"/> and are non-retryable.
        /// </summary>
        [Activity]
        public Task<SourceScanResult> ScanAndPersistAsync(SourceScanRequest request)
        {
            var context = ActivityExecutionContext.Current;
            return scanner.ScanAsync(request, () => context.Heartbeat(), context.CancellationToken);
        }
    }
}
