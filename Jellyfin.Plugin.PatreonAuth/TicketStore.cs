using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Security.Cryptography;

namespace Jellyfin.Plugin.PatreonAuth;

/// <summary>In-memory short-lived OAuth state values and one-time login codes.</summary>
public class TicketStore
{
    private static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(2);

    private readonly ConcurrentDictionary<string, DateTime> _states = new();
    private readonly ConcurrentDictionary<string, (string Username, DateTime Expiry)> _codes = new();

    /// <summary>Creates an OAuth state value.</summary>
    /// <returns>The state.</returns>
    public string NewState()
    {
        Purge();
        var state = Token();
        _states[state] = DateTime.UtcNow + StateLifetime;
        return state;
    }

    /// <summary>Consumes an OAuth state value.</summary>
    /// <param name="state">The state.</param>
    /// <returns>True if valid.</returns>
    public bool ConsumeState(string? state)
        => !string.IsNullOrEmpty(state) && _states.TryRemove(state, out var exp) && exp > DateTime.UtcNow;

    /// <summary>Creates a one-time login code for a user.</summary>
    /// <param name="username">Jellyfin username.</param>
    /// <returns>The code.</returns>
    public string NewCode(string username)
    {
        Purge();
        var code = Token();
        _codes[code] = (username, DateTime.UtcNow + CodeLifetime);
        return code;
    }

    /// <summary>Consumes a login code; succeeds only for the matching user.</summary>
    /// <param name="username">Jellyfin username.</param>
    /// <param name="code">The code.</param>
    /// <returns>True if valid.</returns>
    public bool ConsumeCode(string username, string code)
    {
        if (string.IsNullOrEmpty(code) || !_codes.TryRemove(code, out var entry))
        {
            return false;
        }

        return entry.Expiry > DateTime.UtcNow
            && string.Equals(entry.Username, username, StringComparison.OrdinalIgnoreCase);
    }

    private static string Token()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private void Purge()
    {
        var now = DateTime.UtcNow;
        foreach (var k in _states.Where(p => p.Value <= now).Select(p => p.Key).ToList())
        {
            _states.TryRemove(k, out _);
        }

        foreach (var k in _codes.Where(p => p.Value.Expiry <= now).Select(p => p.Key).ToList())
        {
            _codes.TryRemove(k, out _);
        }
    }
}
