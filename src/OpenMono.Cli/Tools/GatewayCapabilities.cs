using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using OpenMono.Config;

namespace OpenMono.Tools;

/// <summary>
/// Discovers which inference-side web services are installed by asking the Caddy
/// gateway's <c>GET /services</c> capability endpoint. The inference box is the
/// single source of truth (<c>WEB_*_ENABLED</c> in <c>docker/.env</c>), so the
/// agent box doesn't need the user to mirror those flags into local config —
/// it just asks the gateway. The probe result is cached per gateway URL for the
/// process lifetime (capabilities don't change within a session).
/// </summary>
public static class GatewayCapabilities
{
    public enum WebService { Search, Scrape }

    private readonly record struct Capabilities(bool Search, bool Scrape);

    // Short timeout: /services is a tiny local-ish JSON response. If the gateway
    // isn't there (e.g. llm.endpoint points straight at a bare llama-server),
    // we want to fail fast and fall back to the built-in tools.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    private static readonly ConcurrentDictionary<string, Task<Capabilities>> Cache = new();

    /// <summary>
    /// The gateway base URL: an explicit <c>web.gateway</c> wins; otherwise the
    /// LLM endpoint, which is the same relay URL the gateway fronts in dual-box
    /// mode (Caddy path-routes <c>/v1</c>, <c>/search</c>, <c>/scrape</c> apart).
    /// </summary>
    public static string? ResolveGateway(AppConfig config) =>
        !string.IsNullOrEmpty(config.Web.Gateway) ? config.Web.Gateway : config.Llm.Endpoint;

    /// <summary>
    /// Whether <paramref name="service"/> should route through the gateway. An
    /// explicit <c>web.search</c> / <c>web.scrape</c> flag always wins; otherwise
    /// the gateway's <c>/services</c> registry decides. A missing gateway or a
    /// probe failure resolves to <c>false</c> so the caller falls back to its
    /// built-in DuckDuckGo / direct-fetch behaviour.
    /// </summary>
    public static async Task<bool> IsEnabledAsync(
        AppConfig config, WebService service, CancellationToken ct)
    {
        var configOverride = service == WebService.Search
            ? config.Web.SearchEnabled
            : config.Web.ScrapeEnabled;
        if (configOverride.HasValue)
            return configOverride.Value;

        var gateway = ResolveGateway(config);
        if (string.IsNullOrEmpty(gateway))
            return false;

        var caps = await ProbeAsync(gateway, config.Llm.ApiKey).WaitAsync(ct);
        return service == WebService.Search ? caps.Search : caps.Scrape;
    }

    private static Task<Capabilities> ProbeAsync(string gateway, string? apiKey) =>
        // Probe with no caller token so one cancelled request can't poison the
        // cached result for everyone; HttpClient.Timeout still bounds it.
        Cache.GetOrAdd(gateway.TrimEnd('/'), g => FetchAsync(g, apiKey));

    private static async Task<Capabilities> FetchAsync(string gateway, string? apiKey)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{gateway}/services");
            if (!string.IsNullOrEmpty(apiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            using var response = await Http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
                return default;

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return new Capabilities(
                Search: IsTrue(root, "search"),
                Scrape: IsTrue(root, "scrape"));
        }
        catch
        {
            // No gateway / no /services route / non-JSON body → fall back.
            return default;
        }
    }

    // Accept JSON booleans and "true"/"1"/"yes" strings — Caddy substitutes the
    // env values verbatim, so the field can land as either kind.
    private static bool IsTrue(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) && el.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.String => el.GetString()?.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "on",
            _ => false,
        };
}
