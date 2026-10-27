using System;
using System.Linq;
using System.Net;
using System.Net.Mime;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.PatreonAuth.Configuration;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PatreonAuth.Api;

/// <summary>Patreon OAuth endpoints.</summary>
[ApiController]
[Route("Patreon")]
[AllowAnonymous]
public class PatreonController : ControllerBase
{
    private const string StateCookie = "patreon_oauth_state";
    private static readonly SemaphoreSlim LinkLock = new(1, 1);
    private static readonly Regex InvalidNameChars = new(@"[^\w \-'._@+]", RegexOptions.Compiled);

    private readonly IUserManager _userManager;
    private readonly TicketStore _tickets;
    private readonly PatreonClient _patreon;
    private readonly ILogger<PatreonController> _logger;

    /// <summary>Initializes a new instance of the <see cref="PatreonController"/> class.</summary>
    /// <param name="userManager">User manager.</param>
    /// <param name="tickets">Ticket store.</param>
    /// <param name="patreon">Patreon client.</param>
    /// <param name="logger">Logger.</param>
    public PatreonController(IUserManager userManager, TicketStore tickets, PatreonClient patreon, ILogger<PatreonController> logger)
    {
        _userManager = userManager;
        _tickets = tickets;
        _patreon = patreon;
        _logger = logger;
    }

    /// <summary>Starts the Patreon login.</summary>
    /// <returns>Redirect to Patreon.</returns>
    [HttpGet("login")]
    public ActionResult Login()
    {
        var cfg = Plugin.Instance.Configuration;
        if (string.IsNullOrWhiteSpace(cfg.ClientId) || string.IsNullOrWhiteSpace(cfg.ClientSecret))
        {
            return StatusCode(503, "Patreon authentication is not configured.");
        }

        var state = _tickets.NewState();
        Response.Cookies.Append(StateCookie, state, new CookieOptions
        {
            HttpOnly = true,
            Secure = Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            MaxAge = TimeSpan.FromMinutes(10),
            Path = "/"
        });
        return Redirect(PatreonClient.BuildAuthUrl(cfg.ClientId, RedirectUri(), state));
    }

    /// <summary>Handles the Patreon redirect.</summary>
    /// <param name="code">Authorization code.</param>
    /// <param name="state">State.</param>
    /// <param name="error">OAuth error.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A page that completes the Jellyfin sign-in.</returns>
    [HttpGet("callback")]
    public async Task<ActionResult> Callback(
        [FromQuery] string? code,
        [FromQuery] string? state,
        [FromQuery] string? error,
        CancellationToken ct)
    {
        var cookieState = Request.Cookies[StateCookie];
        Response.Cookies.Delete(StateCookie, new CookieOptions { Path = "/" });

        if (!string.IsNullOrEmpty(error))
        {
            return Page(403, "Patreon login cancelled", "Patreon did not authorize the login.");
        }

        if (string.IsNullOrEmpty(code)
            || !_tickets.ConsumeState(state)
            || !string.Equals(state, cookieState, StringComparison.Ordinal))
        {
            return Page(400, "Login failed", "Invalid or expired login attempt. Please try again.");
        }

        PatreonIdentity? identity;
        try
        {
            identity = await _patreon.GetIdentityAsync(code, RedirectUri(), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Patreon identity lookup failed");
            identity = null;
        }

        if (identity is null)
        {
            return Page(502, "Login failed", "Could not verify your Patreon account.");
        }

        var cfg = Plugin.Instance.Configuration;
        var entitled = PatreonClient.IsEntitled(identity, cfg.AllowedTierIds);

        await LinkLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var link = cfg.Links.FirstOrDefault(l => l.PatreonId == identity.Id);
            var user = link is null ? null : _userManager.GetUserById(link.JellyfinUserId);

            if (!entitled)
            {
                if (user is not null && cfg.DisableLapsedUsers && !user.HasPermission(PermissionKind.IsDisabled))
                {
                    user.SetPermission(PermissionKind.IsDisabled, true);
                    await _userManager.UpdateUserAsync(user).ConfigureAwait(false);
                    link!.DisabledByPlugin = true;
                    Plugin.Instance.SaveConfiguration();
                }

                _logger.LogInformation("Patreon user {Id} denied: no entitled tier", identity.Id);
                return Page(403, "Access denied", "You need an active Patreon subscription to a qualifying tier to sign in.");
            }

            if (user is null)
            {
                if (!cfg.CreateUsers)
                {
                    return Page(403, "Access denied", "No Jellyfin account is linked to your Patreon account.");
                }

                user = await _userManager.CreateUserAsync(PickUsername(identity)).ConfigureAwait(false);
                link = new PatronLink { PatreonId = identity.Id, JellyfinUserId = user.Id };
                cfg.Links = cfg.Links.Where(l => l.PatreonId != identity.Id).Append(link).ToArray();
                _logger.LogInformation("Created Jellyfin user {User} for Patreon user {Id}", user.Username, identity.Id);
            }

            var changed = false;
            var providerId = typeof(PatreonAuthenticationProvider).FullName!;
            if (!string.Equals(user.AuthenticationProviderId, providerId, StringComparison.Ordinal))
            {
                user.AuthenticationProviderId = providerId;
                changed = true;
            }

            changed |= ApplyRestrictions(user);

            if (link!.DisabledByPlugin)
            {
                user.SetPermission(PermissionKind.IsDisabled, false);
                link.DisabledByPlugin = false;
                changed = true;
            }
            else if (user.HasPermission(PermissionKind.IsDisabled))
            {
                return Page(403, "Access denied", "Your account has been disabled by the server administrator.");
            }

            if (changed)
            {
                await _userManager.UpdateUserAsync(user).ConfigureAwait(false);
            }

            Plugin.Instance.SaveConfiguration();
            return SignInPage(user.Username, _tickets.NewCode(user.Username));
        }
        finally
        {
            LinkLock.Release();
        }
    }

    // Enforced on every login so Patreon accounts stay stream-only even if changed in the dashboard.
    private static bool ApplyRestrictions(Jellyfin.Database.Implementations.Entities.User user)
    {
        PermissionKind[] denied =
        {
            PermissionKind.IsAdministrator,
            PermissionKind.EnableContentDownloading,
            PermissionKind.EnableContentDeletion,
            PermissionKind.EnableCollectionManagement,
            PermissionKind.EnableSubtitleManagement,
            PermissionKind.EnableLyricManagement,
            PermissionKind.EnableMediaConversion,
            PermissionKind.EnableLiveTvManagement,
            PermissionKind.EnableRemoteControlOfOtherUsers,
            PermissionKind.EnableSharedDeviceControl,
            PermissionKind.EnablePublicSharing
        };

        var changed = false;
        foreach (var kind in denied)
        {
            if (user.HasPermission(kind))
            {
                user.SetPermission(kind, false);
                changed = true;
            }
        }

        return changed;
    }

    private string PickUsername(PatreonIdentity identity)
    {
        var name = InvalidNameChars.Replace(identity.FullName, string.Empty).Trim();
        if (name.Length == 0)
        {
            return "patron-" + identity.Id;
        }

        return _userManager.GetUserByName(name) is null ? name : name + "-" + identity.Id;
    }

    private string RedirectUri()
    {
        var baseUrl = Plugin.Instance.Configuration.PublicBaseUrl.Trim().TrimEnd('/');
        if (baseUrl.Length == 0)
        {
            baseUrl = $"{Request.Scheme}://{Request.Host}{Request.PathBase}";
        }

        return baseUrl + "/Patreon/callback";
    }

    private ContentResult Page(int status, string title, string message)
    {
        var pathBase = WebUtility.HtmlEncode(Request.PathBase.Value ?? string.Empty);
        var body = $"<h1>{WebUtility.HtmlEncode(title)}</h1><p>{WebUtility.HtmlEncode(message)}</p>"
            + $"<p><a href=\"{pathBase}/Patreon/login\">Try again</a> &middot; <a href=\"{pathBase}/web/\">Back to Jellyfin</a></p>";
        return Html(status, title, body, string.Empty);
    }

    private ContentResult SignInPage(string username, string code)
    {
        var json = JsonSerializer.Serialize(new { u = username, p = code, b = Request.PathBase.Value ?? string.Empty });
        const string Body = "<h1>Signing you in&hellip;</h1><p id=\"msg\">One moment.</p>";
        const string Script = """
            (async()=>{
            const ls=window.localStorage;
            let dev=ls.getItem('_deviceId2');
            if(!dev){dev=crypto.randomUUID();ls.setItem('_deviceId2',dev);}
            const auth='MediaBrowser Client="Jellyfin Web", Device="Patreon Login", DeviceId="'+dev+'", Version="10.10.0"';
            try{
              const r=await fetch(d.b+'/Users/AuthenticateByName',{method:'POST',headers:{'Content-Type':'application/json','Authorization':auth},body:JSON.stringify({Username:d.u,Pw:d.p})});
              if(!r.ok)throw new Error(r.status);
              const a=await r.json();
              const creds=JSON.parse(ls.getItem('jellyfin_credentials')||'{"Servers":[]}');
              const s={Id:a.ServerId,ManualAddress:location.origin+d.b,manualAddressOnly:true,DateLastAccessed:Date.now(),LastConnectionMode:2,UserId:a.User.Id,AccessToken:a.AccessToken};
              creds.Servers=(creds.Servers||[]).filter(x=>x.Id!==s.Id);creds.Servers.push(s);
              ls.setItem('jellyfin_credentials',JSON.stringify(creds));
              location.replace(d.b+'/web/');
            }catch(e){
              document.getElementById('msg').textContent='Automatic sign-in failed. On the Jellyfin login page use username "'+d.u+'" and password "'+d.p+'" (valid 2 minutes, one use).';
            }
            })();
            """;
        return Html(200, "Signing in", Body, "<script>const d=" + json + ";" + Script + "</script>");
    }

    private ContentResult Html(int status, string title, string body, string script)
    {
        Response.Headers["Cache-Control"] = "no-store";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        var html = "<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\">"
            + "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">"
            + $"<title>{WebUtility.HtmlEncode(title)}</title></head>"
            + "<body style=\"font-family:system-ui,sans-serif;max-width:480px;margin:80px auto;text-align:center;background:#101010;color:#eee\">"
            + body + script + "</body></html>";
        return new ContentResult { StatusCode = status, ContentType = MediaTypeNames.Text.Html + "; charset=utf-8", Content = html };
    }
}
