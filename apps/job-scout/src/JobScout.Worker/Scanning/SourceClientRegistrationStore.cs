using JobScout.Worker.Integrations.Upwork;
using JobScout.Worker.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace JobScout.Worker.Scanning;

/// <summary>
/// Keeps each source's OAuth client registration in <c>sources.oauth_client_json</c>.
/// Uses its own DbContext per call: it runs while connecting, independently of the scan's unit of work.
/// </summary>
internal sealed class SourceClientRegistrationStore(IServiceScopeFactory scopes) : IUpworkClientRegistrationStore
{
    public async Task<UpworkClientRegistration?> LoadAsync(Guid sourceId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<JobScoutDbContext>();
        var source = await db.Sources.AsNoTracking()
            .Where(s => s.Id == sourceId)
            .Select(s => new { s.Name, s.OAuthClientJson })
            .SingleOrDefaultAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(source?.OAuthClientJson))
            return null;

        try
        {
            return UpworkClientRegistration.Parse(source.OAuthClientJson);
        }
        catch (FormatException ex)
        {
            // Not silently replaced: the value was set on purpose, so a typo must be visible.
            throw new UpworkInvalidRequestException(
                $"Source '{source.Name}' ({sourceId}) has an unusable oauth_client_json: {ex.Message}. " +
                "Fix it, or set it to NULL to register a new OAuth client on the next sign-in.")
            {
                Tool = "connect",
                ErrorCode = "OAUTH_CLIENT_INVALID",
            };
        }
    }

    public async Task SaveAsync(Guid sourceId, UpworkClientRegistration registration, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<JobScoutDbContext>();
        var source = await db.Sources.SingleAsync(s => s.Id == sourceId, cancellationToken);
        source.OAuthClientJson = registration.Json;
        await db.SaveChangesAsync(cancellationToken);
    }
}
