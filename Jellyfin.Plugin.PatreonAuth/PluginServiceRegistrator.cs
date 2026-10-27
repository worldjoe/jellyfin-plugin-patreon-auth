using MediaBrowser.Controller;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.PatreonAuth;

/// <summary>Registers plugin services.</summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<TicketStore>();
        serviceCollection.AddSingleton<PatreonClient>();
        serviceCollection.AddSingleton<DiscordClient>();
        serviceCollection.AddSingleton<IAuthenticationProvider, PatreonAuthenticationProvider>();
    }
}
