using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.PatreonAuth;

/// <summary>Patreon identity result.</summary>
/// <param name="Id">Patreon user id.</param>
/// <param name="FullName">Display name.</param>
/// <param name="ActiveMember">Whether the user is an active patron.</param>
/// <param name="TierIds">Tier ids the user is currently entitled to as an active patron.</param>
public record PatreonIdentity(string Id, string FullName, bool ActiveMember, IReadOnlyList<string> TierIds);

/// <summary>Minimal Patreon OAuth/API client.</summary>
public class PatreonClient
{
    private const string AuthUrl = "https://www.patreon.com/oauth2/authorize";
    private const string TokenUrl = "https://www.patreon.com/api/oauth2/token";
    private const string IdentityUrl = "https://www.patreon.com/api/oauth2/v2/identity";

    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>Initializes a new instance of the <see cref="PatreonClient"/> class.</summary>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    public PatreonClient(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>Builds the Patreon authorize URL.</summary>
    /// <param name="clientId">Client id.</param>
    /// <param name="redirectUri">Redirect URI.</param>
    /// <param name="state">State.</param>
    /// <returns>The URL.</returns>
    public static string BuildAuthUrl(string clientId, string redirectUri, string state)
        => $"{AuthUrl}?response_type=code&client_id={Uri.EscapeDataString(clientId)}"
           + $"&redirect_uri={Uri.EscapeDataString(redirectUri)}"
           + $"&scope={Uri.EscapeDataString("identity identity.memberships")}"
           + $"&state={Uri.EscapeDataString(state)}";

    /// <summary>Checks whether the identity satisfies the configured tier list.</summary>
    /// <param name="identity">Identity.</param>
    /// <param name="allowedTierIds">Comma-separated tier ids; empty allows any active patron.</param>
    /// <returns>True if entitled.</returns>
    public static bool IsEntitled(PatreonIdentity identity, string allowedTierIds)
    {
        var allowed = allowedTierIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return allowed.Length == 0
            ? identity.ActiveMember
            : identity.TierIds.Any(t => allowed.Contains(t));
    }

    /// <summary>Exchanges an authorization code and fetches the patron's identity.</summary>
    /// <param name="code">Authorization code.</param>
    /// <param name="redirectUri">Redirect URI used in the authorize request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The identity, or null on failure.</returns>
    public async Task<PatreonIdentity?> GetIdentityAsync(string code, string redirectUri, CancellationToken ct)
    {
        var cfg = Plugin.Instance.Configuration;
        var http = _httpClientFactory.CreateClient();

        using var tokenRes = await http.PostAsync(
            TokenUrl,
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["code"] = code,
                ["grant_type"] = "authorization_code",
                ["client_id"] = cfg.ClientId,
                ["client_secret"] = cfg.ClientSecret,
                ["redirect_uri"] = redirectUri
            }),
            ct).ConfigureAwait(false);
        if (!tokenRes.IsSuccessStatusCode)
        {
            return null;
        }

        using var tokenDoc = JsonDocument.Parse(await tokenRes.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        if (!tokenDoc.RootElement.TryGetProperty("access_token", out var tokenEl)
            || tokenEl.GetString() is not { Length: > 0 } token)
        {
            return null;
        }

        const string Url = IdentityUrl
            + "?include=memberships.currently_entitled_tiers"
            + "&fields%5Bmember%5D=patron_status"
            + "&fields%5Buser%5D=full_name";
        using var req = new HttpRequestMessage(HttpMethod.Get, Url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var idRes = await http.SendAsync(req, ct).ConfigureAwait(false);
        if (!idRes.IsSuccessStatusCode)
        {
            return null;
        }

        using var doc = JsonDocument.Parse(await idRes.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var root = doc.RootElement;
        if (!root.TryGetProperty("data", out var data)
            || !data.TryGetProperty("id", out var idEl)
            || idEl.GetString() is not { Length: > 0 } id)
        {
            return null;
        }

        var fullName = string.Empty;
        if (data.TryGetProperty("attributes", out var attrs) && attrs.TryGetProperty("full_name", out var fn))
        {
            fullName = fn.GetString() ?? string.Empty;
        }

        var active = false;
        var tiers = new List<string>();
        if (root.TryGetProperty("included", out var included))
        {
            foreach (var item in included.EnumerateArray())
            {
                if (item.TryGetProperty("type", out var type) && type.GetString() == "member"
                    && item.TryGetProperty("attributes", out var a)
                    && a.TryGetProperty("patron_status", out var ps)
                    && ps.GetString() == "active_patron")
                {
                    active = true;
                    if (item.TryGetProperty("relationships", out var rel)
                        && rel.TryGetProperty("currently_entitled_tiers", out var cet)
                        && cet.TryGetProperty("data", out var tierData))
                    {
                        foreach (var t in tierData.EnumerateArray())
                        {
                            if (t.TryGetProperty("id", out var tid) && tid.GetString() is { } s)
                            {
                                tiers.Add(s);
                            }
                        }
                    }
                }
            }
        }

        return new PatreonIdentity(id, fullName, active, tiers);
    }
}
