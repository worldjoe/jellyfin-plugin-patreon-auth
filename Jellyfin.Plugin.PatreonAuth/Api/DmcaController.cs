using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PatreonAuth.Api;

/// <summary>DMCA takedown notice request (elements of 17 U.S.C. 512(c)(3)(A)).</summary>
public class DmcaReportRequest
{
    /// <summary>Gets or sets the reported item id.</summary>
    [Required]
    public Guid ItemId { get; set; }

    /// <summary>Gets or sets the identification of the copyrighted work claimed to be infringed.</summary>
    [Required]
    [StringLength(600, MinimumLength = 5)]
    public string CopyrightedWork { get; set; } = string.Empty;

    /// <summary>Gets or sets the complainant's full legal name.</summary>
    [Required]
    [StringLength(100, MinimumLength = 2)]
    public string ComplainantName { get; set; } = string.Empty;

    /// <summary>Gets or sets the complainant's postal address.</summary>
    [Required]
    [StringLength(300, MinimumLength = 5)]
    public string Address { get; set; } = string.Empty;

    /// <summary>Gets or sets the complainant's phone number.</summary>
    [Required]
    [StringLength(40, MinimumLength = 5)]
    public string Phone { get; set; } = string.Empty;

    /// <summary>Gets or sets the complainant's email address.</summary>
    [Required]
    [EmailAddress]
    [StringLength(150)]
    public string Email { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the complainant has a good faith belief the use is unauthorized.</summary>
    public bool GoodFaithStatement { get; set; }

    /// <summary>Gets or sets a value indicating whether the complainant swears, under penalty of perjury, the notice is accurate and they are authorized to act.</summary>
    public bool AccuracyStatement { get; set; }

    /// <summary>Gets or sets the electronic signature (typed full name).</summary>
    [Required]
    [StringLength(100, MinimumLength = 2)]
    public string Signature { get; set; } = string.Empty;
}

/// <summary>DMCA reporting endpoints.</summary>
[ApiController]
[Route("Dmca")]
public class DmcaController : ControllerBase
{
    private const int MaxReportsPerHour = 5;
    private static readonly ConcurrentDictionary<string, ConcurrentQueue<DateTime>> Recent = new();

    private readonly ILibraryManager _library;
    private readonly DiscordClient _discord;
    private readonly ILogger<DmcaController> _logger;

    /// <summary>Initializes a new instance of the <see cref="DmcaController"/> class.</summary>
    /// <param name="library">Library manager.</param>
    /// <param name="discord">Discord client.</param>
    /// <param name="logger">Logger.</param>
    public DmcaController(ILibraryManager library, DiscordClient discord, ILogger<DmcaController> logger)
    {
        _library = library;
        _discord = discord;
        _logger = logger;
    }

    /// <summary>Serves the script that adds the report button to jellyfin-web item pages.</summary>
    /// <returns>JavaScript.</returns>
    [HttpGet("button.js")]
    [AllowAnonymous]
    public ActionResult ButtonScript()
    {
        var name = typeof(Plugin).Namespace + ".Web.dmca.js";
        var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
        if (stream is null)
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "no-cache";
        return File(stream, "application/javascript");
    }

    /// <summary>Reports an item to the admin via Discord.</summary>
    /// <param name="request">Report.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Status.</returns>
    [HttpPost("report")]
    [Authorize]
    public async Task<ActionResult> Report([FromBody] DmcaReportRequest request, CancellationToken ct)
    {
        var cfg = Plugin.Instance.Configuration;
        if (!cfg.DmcaEnabled
            || string.IsNullOrWhiteSpace(cfg.DiscordBotToken)
            || string.IsNullOrWhiteSpace(cfg.DiscordChannelId))
        {
            return StatusCode(503, "DMCA reporting is not configured.");
        }

        var item = _library.GetItemById(request.ItemId);
        if (item is null)
        {
            return NotFound("Item not found.");
        }

        if (!request.GoodFaithStatement || !request.AccuracyStatement)
        {
            return BadRequest("Both statements must be accepted.");
        }

        var userId = User.FindFirst("Jellyfin-UserId")?.Value ?? "unknown";
        var userName = User.FindFirst("Jellyfin-User")?.Value ?? "unknown";
        if (!AllowRequest(userId))
        {
            return StatusCode(429, "Too many reports. Try again later.");
        }

        var fields = new List<(string, string)>
        {
            ("Item", Clean(item.Name, 200)),
            ("Item id / location", $"`{item.Id}`\n`{Clean(item.Path, 300)}`"),
            ("Copyrighted work claimed infringed", Clean(request.CopyrightedWork, 600)),
            ("Complainant", Clean(request.ComplainantName, 100)),
            ("Email", Clean(request.Email, 150)),
            ("Phone", Clean(request.Phone, 40)),
            ("Address", Clean(request.Address, 300)),
            ("Good faith statement", "Complainant states a good faith belief that the use is not authorized by the copyright owner, its agent, or the law."),
            ("Accuracy / authority statement", "Complainant states, under penalty of perjury, that the information is accurate and they are authorized to act on behalf of the copyright owner."),
            ("Electronic signature", $"/s/ {Clean(request.Signature, 100)}"),
            ("Submitted by (Jellyfin user)", $"{Clean(userName, 100)} (`{Clean(userId, 40)}`)"),
            ("Submitted at (UTC)", DateTime.UtcNow.ToString("u", System.Globalization.CultureInfo.InvariantCulture))
        };

        string? error;
        try
        {
            error = await _discord.SendMessageAsync(
                cfg.DiscordBotToken,
                cfg.DiscordGuildId,
                cfg.DiscordChannelId,
                "DMCA takedown notice",
                fields,
                cfg.DiscordMention,
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            error = ex.Message;
        }

        if (error is not null)
        {
            _logger.LogError("DMCA report for {Item} by {User} not delivered: {Error}", item.Id, userName, error);
            return StatusCode(502, "Could not deliver the report.");
        }

        _logger.LogInformation("DMCA report for {Item} filed by {User}", item.Id, userName);
        return Ok();
    }

    // Neutralise Discord markdown/mentions and newlines in user-controlled text.
    private static string Clean(string? s, int max)
    {
        var t = (s ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Replace('`', '\'');
        t = t.Replace("@", "@\u200b").Replace("<", "\\<");
        return t.Length > max ? t[..max] : t;
    }

    private static bool AllowRequest(string userId)
    {
        var q = Recent.GetOrAdd(userId, _ => new ConcurrentQueue<DateTime>());
        var now = DateTime.UtcNow;
        lock (q)
        {
            while (q.TryPeek(out var t) && now - t > TimeSpan.FromHours(1))
            {
                q.TryDequeue(out _);
            }

            if (q.Count >= MaxReportsPerHour)
            {
                return false;
            }

            q.Enqueue(now);
            return true;
        }
    }
}
