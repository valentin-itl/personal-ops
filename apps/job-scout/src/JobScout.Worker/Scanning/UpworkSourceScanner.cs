using JobScout.Contracts;
using JobScout.Worker.Integrations.Upwork;
using JobScout.Worker.Integrations.Upwork.Models;
using JobScout.Worker.Persistence;
using JobScout.Worker.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Temporalio.Exceptions;

namespace JobScout.Worker.Scanning;

/// <summary><see cref="ApplicationFailureException.ErrorType"/> values for scan failures that are not Upwork's.</summary>
public static class SourceScanFailureTypes
{
    public const string InvalidSourceId = "InvalidSourceId";
    public const string SourceNotFound = "SourceNotFound";
    public const string SearchProfileNotFound = "SearchProfileNotFound";
    public const string SearchProfileInvalid = "SearchProfileInvalid";
}

/// <summary>
/// Fetches fresh postings for one source and search profile and upserts jobs: a new or changed posting gets
/// a revision (its raw JSON) and an evaluation request. Upwork failures are logged together with the JSON that
/// was sent and received, recorded on the scan row and source health, and rethrown as Temporal failures that
/// carry the retry decision.
/// </summary>
public sealed class UpworkSourceScanner(
    JobScoutDbContext db,
    IUpworkMcpClient upwork,
    IOptionsMonitor<JobScoutScanOptions> options,
    TimeProvider time,
    ILogger<UpworkSourceScanner> logger)
{
    public async Task<SourceScanResult> ScanAsync(
        SourceScanRequest request, Action? heartbeat, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(request.SourceId, out var sourceId))
            throw Fail(SourceScanFailureTypes.InvalidSourceId, $"SourceId '{request.SourceId}' is not a GUID");

        var source = await db.Sources.SingleOrDefaultAsync(s => s.Id == sourceId, cancellationToken)
            ?? throw Fail(SourceScanFailureTypes.SourceNotFound, $"Source {sourceId} does not exist in job_scout.sources");

        if (source.Health == SourceHealth.Disabled)
        {
            logger.LogInformation("Source {SourceId} is disabled; scan skipped", sourceId);
            return new SourceScanResult(request.SourceId, NewJobs: 0, UpdatedJobs: 0);
        }

        var (profileId, profile) = ResolveProfile(request.SearchProfileId);
        var now = time.GetUtcNow();
        var cutoff = await ComputeCutoffAsync(sourceId, profileId, profile, now, cancellationToken);

        var scan = new SourceScan { SourceId = sourceId, SearchProfileId = profileId, StartedAtUtc = now.UtcDateTime };
        db.SourceScans.Add(scan);
        await db.SaveChangesAsync(cancellationToken);

        try
        {
            var (pages, newestFirst) = OpenPages(profile, cutoff, cancellationToken);
            var seen = new HashSet<string>(StringComparer.Ordinal);

            await foreach (var page in pages)
            {
                heartbeat?.Invoke();

                var fresh = new List<UpworkJobSummary>();
                foreach (var job in page.Jobs)
                {
                    // The last page usually reaches past the cutoff; those postings were handled by an earlier scan.
                    if (newestFirst && job.PublishedAt < cutoff)
                        continue;
                    // New postings push older ones onto the next page, so a job can appear twice in one scan.
                    if (seen.Add(job.Id))
                        fresh.Add(job);
                }

                await UpsertJobsAsync(sourceId, scan, fresh, now.UtcDateTime, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
            }

            scan.Status = SourceScanStatus.Completed;
            scan.CompletedAtUtc = time.GetUtcNow().UtcDateTime;
            source.Health = SourceHealth.Healthy;
            source.ErrorMessage = null;
            await db.SaveChangesAsync(cancellationToken);

            logger.LogInformation(
                "Scan {ScanId} of {SourceId}/{Profile} since {Cutoff:u}: {Observed} observed, {Created} new, {Updated} updated, {Unchanged} unchanged, {Queued} evaluations queued",
                scan.Id, sourceId, profileId, cutoff, scan.ObservedCount, scan.CreatedCount, scan.UpdatedCount,
                scan.UnchangedCount, scan.EvaluationsQueuedCount);

            return new SourceScanResult(request.SourceId, scan.CreatedCount, scan.UpdatedCount);
        }
        catch (Exception ex)
        {
            await RecordFailureAsync(scan.Id, sourceId, ex);
            if (ex is UpworkMcpException upworkError)
                throw upworkError.ToApplicationFailure();
            throw;
        }
    }

    private (string Id, UpworkSearchProfile Profile) ResolveProfile(string profileId)
    {
        var profiles = options.CurrentValue.SearchProfiles;
        var match = profiles.FirstOrDefault(p => string.Equals(p.Key, profileId, StringComparison.OrdinalIgnoreCase));
        if (match.Value is null)
        {
            throw Fail(
                SourceScanFailureTypes.SearchProfileNotFound,
                $"Search profile '{profileId}' is not configured under {JobScoutScanOptions.SectionName}:SearchProfiles " +
                $"(configured: {(profiles.Count == 0 ? "none" : string.Join(", ", profiles.Keys))})");
        }

        var problems = match.Value.Validate().ToList();
        if (problems.Count > 0)
            throw Fail(SourceScanFailureTypes.SearchProfileInvalid, $"Search profile '{match.Key}' is invalid: {string.Join("; ", problems)}");

        return (match.Key, match.Value);
    }

    /// <summary>
    /// Start of the window to read: the last completed scan of this profile minus the overlap,
    /// but never earlier than <see cref="UpworkSearchProfile.MaxLookback"/> ago.
    /// </summary>
    private async Task<DateTimeOffset> ComputeCutoffAsync(
        Guid sourceId, string profileId, UpworkSearchProfile profile, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var lastCompleted = await db.SourceScans
            .Where(s => s.SourceId == sourceId && s.SearchProfileId == profileId && s.Status == SourceScanStatus.Completed)
            .Select(s => (DateTime?)s.StartedAtUtc)
            .MaxAsync(cancellationToken);

        var floor = now - profile.MaxLookback;
        if (lastCompleted is { } last)
        {
            var resume = new DateTimeOffset(DateTime.SpecifyKind(last, DateTimeKind.Utc)) - profile.Overlap;
            if (resume > floor)
                return resume;
        }

        return floor;
    }

    private (IAsyncEnumerable<UpworkJobSearchPage> Pages, bool NewestFirst) OpenPages(
        UpworkSearchProfile profile, DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        if (profile.Search is { } search)
        {
            var newestFirst = UpworkJobOrdering.IsNewestFirst(search);
            var paging = new UpworkPaging { MaxPages = profile.MaxPages, PublishedAfter = newestFirst ? cutoff : null };
            return (upwork.SearchJobPagesAsync(search with { Cursor = null }, paging, cancellationToken), newestFirst);
        }

        var smart = profile.SmartSearch! with { Cursor = null };
        var mostRecent = UpworkJobOrdering.IsNewestFirst(smart);
        // most_recent filters by date on the server; let it do the narrowing unless the profile pins its own window.
        if (mostRecent && smart.DaysPosted is null && smart.FromDate is null && smart.ToDate is null)
            smart = smart with { FromDate = cutoff };
        var smartPaging = new UpworkPaging { MaxPages = profile.MaxPages, PublishedAfter = mostRecent ? cutoff : null };
        return (upwork.SmartSearchJobPagesAsync(smart, smartPaging, cancellationToken), mostRecent);
    }

    private async Task UpsertJobsAsync(
        Guid sourceId, SourceScan scan, IReadOnlyList<UpworkJobSummary> jobs, DateTime nowUtc, CancellationToken cancellationToken)
    {
        if (jobs.Count == 0)
            return;

        var ids = jobs.Select(j => j.Id).ToList();
        var known = await db.Jobs
            .Where(j => j.SourceId == sourceId && ids.Contains(j.ExternalId))
            .ToDictionaryAsync(j => j.ExternalId, StringComparer.Ordinal, cancellationToken);

        foreach (var summary in jobs)
        {
            scan.ObservedCount++;
            var fingerprint = JobFingerprint.Compute(summary);

            if (!known.TryGetValue(summary.Id, out var job))
            {
                job = new Job
                {
                    SourceId = sourceId,
                    ExternalId = summary.Id,
                    Availability = JobAvailability.Open,
                    FirstSeenAtUtc = nowUtc,
                    LastSeenAtUtc = nowUtc,
                    CurrentFingerprint = fingerprint,
                };
                db.Jobs.Add(job);
                AddRevision(job, fingerprint, summary, nowUtc);
                scan.CreatedCount++;
                scan.EvaluationsQueuedCount++;
                continue;
            }

            // Search results only list open postings.
            job.LastSeenAtUtc = nowUtc;
            job.Availability = JobAvailability.Open;

            if (job.CurrentFingerprint == fingerprint)
            {
                scan.UnchangedCount++;
                continue;
            }

            job.CurrentFingerprint = fingerprint;
            scan.UpdatedCount++;

            // Content can change back to an earlier version; that revision and its evaluation already exist.
            var revisionExists = await db.JobRevisions.AnyAsync(
                r => r.JobId == job.Id && r.Fingerprint == fingerprint, cancellationToken);
            if (!revisionExists)
            {
                AddRevision(job, fingerprint, summary, nowUtc);
                scan.EvaluationsQueuedCount++;
            }
        }
    }

    private void AddRevision(Job job, string fingerprint, UpworkJobSummary summary, DateTime nowUtc)
    {
        var revision = new JobRevision
        {
            JobId = job.Id,
            Job = job,
            Fingerprint = fingerprint,
            PayloadJson = summary.RawJson,
            CreatedAtUtc = nowUtc,
        };
        db.JobRevisions.Add(revision);
        db.EvaluationOutbox.Add(new EvaluationOutbox
        {
            JobRevisionId = revision.Id,
            JobRevision = revision,
            // Deterministic, so a replayed scan cannot queue the same content twice (unique index).
            EvaluationKey = $"{job.SourceId:N}/{job.ExternalId}/{fingerprint}",
            CreatedAtUtc = nowUtc,
        });
    }

    /// <summary>
    /// Logs the failure (for Upwork failures, with the JSON sent and received), then, best effort and with a
    /// fresh change tracker, marks the scan failed and updates source health. Never masks the original exception.
    /// </summary>
    private async Task RecordFailureAsync(Guid scanId, Guid sourceId, Exception exception)
    {
        if (exception is UpworkMcpException upworkError)
            LogUpworkFailure(scanId, sourceId, upworkError);
        else
            logger.LogError(exception, "Scan {ScanId} of {SourceId} failed", scanId, sourceId);

        try
        {
            db.ChangeTracker.Clear();
            var scan = await db.SourceScans.SingleAsync(s => s.Id == scanId, CancellationToken.None);
            scan.Status = SourceScanStatus.Failed;
            scan.CompletedAtUtc = time.GetUtcNow().UtcDateTime;
            scan.ErrorMessage = exception.Message;

            if (exception is UpworkMcpException upworkFailure && HealthAfter(upworkFailure) is { } health)
            {
                var source = await db.Sources.SingleAsync(s => s.Id == sourceId, CancellationToken.None);
                source.Health = health;
                source.ErrorMessage = upworkFailure.Message;
            }

            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception recordError)
        {
            logger.LogError(recordError, "Could not record the failure of scan {ScanId}", scanId);
        }
    }

    /// <summary>
    /// One structured entry with everything needed to diagnose the failure, including the received payload
    /// verbatim (<c>ResponseJson</c>) and, for format changes, the exact problems (<c>Issues</c>).
    /// </summary>
    private void LogUpworkFailure(Guid scanId, Guid sourceId, UpworkMcpException error)
    {
        var retryable = error.Disposition is UpworkFailureDisposition.Retry or UpworkFailureDisposition.RetryAfter;
        var raw = error.Raw;
        logger.Log(
            retryable ? LogLevel.Warning : LogLevel.Error,
            error,
            "Scan {ScanId} of {SourceId} failed ({Disposition}): {Error} Issues: {Issues} Request: {RequestJson} Received ({PayloadSource}, trace {TraceId}): {ResponseJson}",
            scanId,
            sourceId,
            error.Disposition,
            error.Message,
            error is UpworkContractMismatchException mismatch ? mismatch.Issues : Array.Empty<string>(),
            raw?.RequestJson,
            raw?.PayloadSource,
            raw?.TraceId,
            raw?.ResponseJson);
    }

    /// <summary>Null when the failure says nothing about the source itself (our request was wrong, one job was gone).</summary>
    internal static SourceHealth? HealthAfter(UpworkMcpException exception) => exception switch
    {
        UpworkAuthRequiredException => SourceHealth.AuthRequired,
        UpworkContractMismatchException => SourceHealth.ContractMismatch,
        UpworkInvalidRequestException or UpworkJobUnavailableException => null,
        _ => SourceHealth.Degraded,
    };

    private static ApplicationFailureException Fail(string errorType, string message) =>
        new(message, errorType: errorType, nonRetryable: true);
}
