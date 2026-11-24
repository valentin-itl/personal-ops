using JobScout.Worker.Integrations.Upwork;
using JobScout.Worker.Persistence;
using JobScout.Worker.Persistence.Entities;
using JobScout.Worker.Scanning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace JobScout.Tests.Scanning;

public sealed class SourceClientRegistrationStoreTests : IDisposable
{
    private readonly ServiceProvider _services;
    private readonly SourceClientRegistrationStore _store;
    private readonly Guid _sourceId = Guid.NewGuid();

    public SourceClientRegistrationStoreTests()
    {
        var database = Guid.NewGuid().ToString();
        _services = new ServiceCollection()
            .AddDbContext<JobScoutDbContext>(o => o.UseInMemoryDatabase(database))
            .BuildServiceProvider();
        _store = new SourceClientRegistrationStore(_services.GetRequiredService<IServiceScopeFactory>());
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task SeedAsync(string? oauthClientJson)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<JobScoutDbContext>();
        db.Sources.Add(new Source { Id = _sourceId, Name = "Upwork", OAuthClientJson = oauthClientJson });
        await db.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task Source_without_registration_has_none()
    {
        await SeedAsync(null);

        Assert.Null(await _store.LoadAsync(_sourceId, Ct));
    }

    [Fact]
    public async Task Saved_registration_lands_on_the_source_row()
    {
        await SeedAsync(null);
        var registration = UpworkClientRegistration.Parse(
            """{"client_id":"7e4cb25799f6a5c3d1196850fabcce95","redirect_uris":["http://localhost:53682/callback"]}""");

        await _store.SaveAsync(_sourceId, registration, Ct);

        await using var scope = _services.CreateAsyncScope();
        var source = await scope.ServiceProvider.GetRequiredService<JobScoutDbContext>().Sources.SingleAsync(Ct);
        Assert.Equal(registration.Json, source.OAuthClientJson);
        Assert.Equal("7e4cb25799f6a5c3d1196850fabcce95", (await _store.LoadAsync(_sourceId, Ct))!.ClientId);
    }

    [Fact]
    public async Task Hand_written_client_id_is_loaded()
    {
        await SeedAsync("""{"client_id":"7e4cb25799f6a5c3d1196850fabcce95"}""");

        var loaded = await _store.LoadAsync(_sourceId, Ct);

        Assert.Equal("7e4cb25799f6a5c3d1196850fabcce95", loaded!.ClientId);
        Assert.Empty(loaded.RedirectUris);
    }

    [Fact]
    public async Task Unusable_value_fails_loudly_instead_of_being_replaced()
    {
        await SeedAsync("""{"clientId":"typo"}""");

        var ex = await Assert.ThrowsAsync<UpworkInvalidRequestException>(() => _store.LoadAsync(_sourceId, Ct));

        Assert.Equal("OAUTH_CLIENT_INVALID", ex.ErrorCode);
        Assert.Contains("'Upwork'", ex.Message);
        Assert.Contains("client_id is missing", ex.Message);
    }

    public void Dispose() => _services.Dispose();
}
