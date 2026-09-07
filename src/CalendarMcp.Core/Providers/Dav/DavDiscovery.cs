using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace CalendarMcp.Core.Providers.Dav;

/// <summary>
/// RFC 6764-style discovery: well-known → current-user-principal → home-set.
/// Results are cached in-memory per account+service.
/// </summary>
public sealed class DavDiscovery
{
    private readonly DavHttpClient _http;
    private readonly ILogger<DavDiscovery> _logger;
    private readonly ConcurrentDictionary<string, Uri> _homeCache = new();

    public DavDiscovery(DavHttpClient http, ILogger<DavDiscovery> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<Uri> GetCalendarHomeAsync(
        DavAccountConfig config,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(config.CalendarHomeSet))
            return new Uri(config.CalendarHomeSet);

        return await DiscoverHomeAsync(
            config,
            DavServiceKind.CalDav,
            config.CalDavUrl ?? throw new InvalidOperationException("caldavUrl is required."),
            wellKnown: ".well-known/caldav",
            propfindHome: DavXml.PropfindCalendarHomeSet,
            pickHome: r => r.CalendarHomeSet,
            cacheKey: $"{config.AccountId}|cal",
            cancellationToken);
    }

    public async Task<Uri> GetAddressbookHomeAsync(
        DavAccountConfig config,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(config.AddressbookHomeSet))
            return new Uri(config.AddressbookHomeSet);

        return await DiscoverHomeAsync(
            config,
            DavServiceKind.CardDav,
            config.CardDavUrl ?? throw new InvalidOperationException("carddavUrl is required."),
            wellKnown: ".well-known/carddav",
            propfindHome: DavXml.PropfindAddressbookHomeSet,
            pickHome: r => r.AddressbookHomeSet,
            cacheKey: $"{config.AccountId}|card",
            cancellationToken);
    }

    private async Task<Uri> DiscoverHomeAsync(
        DavAccountConfig config,
        DavServiceKind kind,
        string entryUrl,
        string wellKnown,
        Func<string> propfindHome,
        Func<DavPropResponse, Uri?> pickHome,
        string cacheKey,
        CancellationToken cancellationToken)
    {
        if (_homeCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var entry = new Uri(entryUrl.EndsWith('/') ? entryUrl : entryUrl + "/");
        var start = new Uri(entry, wellKnown);

        _logger.LogDebug("DAV discovery starting at {Start} for {AccountId}/{Kind}",
            start, config.AccountId, kind);

        // Step 1: current-user-principal (from well-known or entry root)
        var principalResult = await _http.PropfindAsync(
            start, config.Username, config.Password, DavXml.PropfindCurrentUserPrincipal(),
            config.Preset, kind, entry, depth: 0, cancellationToken);

        if (!principalResult.IsSuccess)
        {
            // Fall back to entry root
            principalResult = await _http.PropfindAsync(
                entry, config.Username, config.Password, DavXml.PropfindCurrentUserPrincipal(),
                config.Preset, kind, entry, depth: 0, cancellationToken);
        }

        EnsureSuccess(principalResult, "current-user-principal");

        var principalResponses = DavXml.ParseMultiStatus(principalResult.Body, principalResult.FinalUri);
        var principal = principalResponses
            .Select(r => r.CurrentUserPrincipal)
            .FirstOrDefault(u => u is not null)
            ?? throw new InvalidOperationException(
                $"DAV server at '{entry}' did not return current-user-principal.");

        // Step 2: home-set on the principal
        var homeResult = await _http.PropfindAsync(
            principal, config.Username, config.Password, propfindHome(),
            config.Preset, kind, entry, depth: 0, cancellationToken);

        EnsureSuccess(homeResult, "home-set");

        var homeResponses = DavXml.ParseMultiStatus(homeResult.Body, homeResult.FinalUri);
        var home = homeResponses
            .Select(pickHome)
            .FirstOrDefault(u => u is not null)
            ?? throw new InvalidOperationException(
                $"DAV principal '{principal}' did not return a home-set for {kind}.");

        _logger.LogInformation("DAV discovery for {AccountId}/{Kind} resolved home {Home}",
            config.AccountId, kind, home);

        _homeCache[cacheKey] = home;
        return home;
    }

    private static void EnsureSuccess(DavHttpResult result, string step)
    {
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException(
                $"DAV {step} failed with HTTP {(int)result.StatusCode} at '{result.FinalUri}'.");
        }
    }

    public void Invalidate(string accountId)
    {
        foreach (var key in _homeCache.Keys.Where(k => k.StartsWith(accountId + "|", StringComparison.Ordinal)))
            _homeCache.TryRemove(key, out _);
    }
}

/// <summary>
/// Resolved per-account DAV configuration.
/// </summary>
public sealed record DavAccountConfig(
    string AccountId,
    string Username,
    string Password,
    string? Preset,
    string? CalDavUrl,
    string? CardDavUrl,
    string? CalendarHomeSet,
    string? AddressbookHomeSet,
    bool EnableCalendar,
    bool EnableContacts);
