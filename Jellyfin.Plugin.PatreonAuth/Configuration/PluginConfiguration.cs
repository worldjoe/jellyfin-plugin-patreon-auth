using System;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.PatreonAuth.Configuration;

/// <summary>Plugin settings.</summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Gets or sets the Patreon OAuth client id.</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>Gets or sets the Patreon OAuth client secret.</summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>Gets or sets comma-separated tier ids. Empty means any active patron.</summary>
    public string AllowedTierIds { get; set; } = string.Empty;

    /// <summary>Gets or sets the public Jellyfin base URL (e.g. https://jf.example.com). Empty derives it from the request.</summary>
    public string PublicBaseUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether new Jellyfin users are created for new patrons.</summary>
    public bool CreateUsers { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether a lapsed patron's Jellyfin user is disabled when they try to log in.</summary>
    public bool DisableLapsedUsers { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether DMCA reporting is enabled.</summary>
    public bool DmcaEnabled { get; set; }

    /// <summary>Gets or sets the Discord bot token.</summary>
    public string DiscordBotToken { get; set; } = string.Empty;

    /// <summary>Gets or sets the Discord guild (server) id the channel must belong to.</summary>
    public string DiscordGuildId { get; set; } = string.Empty;

    /// <summary>Gets or sets the Discord channel id reports are posted to.</summary>
    public string DiscordChannelId { get; set; } = string.Empty;

    /// <summary>Gets or sets an optional Discord user/role mention (e.g. &lt;@123&gt;) prepended to reports.</summary>
    public string DiscordMention { get; set; } = string.Empty;

    /// <summary>Gets or sets the Patreon-to-Jellyfin user links.</summary>
    public PatronLink[] Links { get; set; } = Array.Empty<PatronLink>();
}

/// <summary>Links a Patreon user to a Jellyfin user.</summary>
public class PatronLink
{
    /// <summary>Gets or sets the Patreon user id.</summary>
    public string PatreonId { get; set; } = string.Empty;

    /// <summary>Gets or sets the Jellyfin user id.</summary>
    public Guid JellyfinUserId { get; set; }

    /// <summary>Gets or sets a value indicating whether this plugin disabled the user for a lapsed subscription.</summary>
    public bool DisabledByPlugin { get; set; }
}
