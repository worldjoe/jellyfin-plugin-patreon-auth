using System;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Authentication;

namespace Jellyfin.Plugin.PatreonAuth;

/// <summary>
/// Authentication provider. The "password" is a one-time code issued by the Patreon OAuth callback,
/// so Patreon-managed users can only sign in after proving their Patreon subscription.
/// </summary>
public class PatreonAuthenticationProvider : IAuthenticationProvider
{
    private readonly TicketStore _tickets;

    /// <summary>Initializes a new instance of the <see cref="PatreonAuthenticationProvider"/> class.</summary>
    /// <param name="tickets">Ticket store.</param>
    public PatreonAuthenticationProvider(TicketStore tickets)
    {
        _tickets = tickets;
    }

    /// <inheritdoc />
    public string Name => "Patreon";

    /// <inheritdoc />
    public bool IsEnabled => true;

    /// <inheritdoc />
    public Task<ProviderAuthenticationResult> Authenticate(string username, string password)
    {
        if (!_tickets.ConsumeCode(username, password))
        {
            throw new AuthenticationException("Invalid or expired Patreon login code.");
        }

        return Task.FromResult(new ProviderAuthenticationResult { Username = username });
    }

    /// <inheritdoc />
    public bool HasPassword(User user) => true;

    /// <inheritdoc />
    public Task ChangePassword(User user, string newPassword)
        => throw new NotSupportedException("Patreon users cannot set a password.");
}
