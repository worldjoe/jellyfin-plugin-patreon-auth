using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.PatreonAuth;

/// <summary>Minimal Discord bot REST client.</summary>
public class DiscordClient
{
    private const string ApiBase = "https://discord.com/api/v10";

    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>Initializes a new instance of the <see cref="DiscordClient"/> class.</summary>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    public DiscordClient(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>Posts a message to a channel, verifying the channel belongs to the guild.</summary>
    /// <param name="token">Bot token.</param>
    /// <param name="guildId">Expected guild id (empty skips the check).</param>
    /// <param name="channelId">Channel id.</param>
    /// <param name="title">Embed title.</param>
    /// <param name="fields">Embed fields (name max 256, value max 1024 chars; max 25 fields).</param>
    /// <param name="mention">Optional mention (e.g. &lt;@123&gt; or &lt;@&amp;123&gt;) allowed to ping.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Null on success, otherwise an error description.</returns>
    public async Task<string?> SendMessageAsync(
        string token,
        string guildId,
        string channelId,
        string title,
        IReadOnlyList<(string Name, string Value)> fields,
        string? mention,
        CancellationToken ct)
    {
        var http = _httpClientFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(15);

        HttpRequestMessage Req(HttpMethod method, string path)
        {
            var r = new HttpRequestMessage(method, ApiBase + path);
            r.Headers.Authorization = new AuthenticationHeaderValue("Bot", token);
            r.Headers.UserAgent.ParseAdd("JellyfinPatreonAuth (https://github.com/worldjoe, 1.0)");
            return r;
        }

        if (!string.IsNullOrWhiteSpace(guildId))
        {
            using var chReq = Req(HttpMethod.Get, "/channels/" + Uri.EscapeDataString(channelId));
            using var chResp = await http.SendAsync(chReq, ct).ConfigureAwait(false);
            if (!chResp.IsSuccessStatusCode)
            {
                return $"Discord channel lookup failed: HTTP {(int)chResp.StatusCode}";
            }

            using var doc = JsonDocument.Parse(await chResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var actual = doc.RootElement.TryGetProperty("guild_id", out var g) ? g.GetString() : null;
            if (!string.Equals(actual, guildId, StringComparison.Ordinal))
            {
                return "Discord channel does not belong to the configured guild.";
            }
        }

        using var req = Req(HttpMethod.Post, "/channels/" + Uri.EscapeDataString(channelId) + "/messages");
        // Only the configured mention may ping; user-supplied text never can.
        var m = Regex.Match(mention ?? string.Empty, @"^<@(&?)(\d+)>$");
        var allowed = new
        {
            parse = Array.Empty<string>(),
            users = m.Success && m.Groups[1].Value.Length == 0 ? new[] { m.Groups[2].Value } : Array.Empty<string>(),
            roles = m.Success && m.Groups[1].Value.Length > 0 ? new[] { m.Groups[2].Value } : Array.Empty<string>()
        };
        var embed = new
        {
            title,
            fields = fields.Select(f => new { name = f.Name, value = f.Value, inline = false }).ToArray()
        };
        req.Content = JsonContent.Create(new { content = mention ?? string.Empty, embeds = new[] { embed }, allowed_mentions = allowed });
        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        return resp.IsSuccessStatusCode ? null : $"Discord send failed: HTTP {(int)resp.StatusCode}";
    }
}
