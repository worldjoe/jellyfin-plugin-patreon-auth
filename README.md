# Jellyfin Patreon Authentication

Patreon subscribers sign in to Jellyfin via Patreon OAuth. Targets Jellyfin 12.4 (net10.0). Plugins are built per server version; rebuild if you upgrade Jellyfin major versions.

## How it works
1. User opens `/Patreon/login` â†’ Patreon OAuth (`identity identity.memberships`).
2. `/Patreon/callback` verifies `state` (server-side + cookie), exchanges code, reads active memberships and entitled tiers.
3. Entitled â†’ Jellyfin user created/linked by Patreon user id (provider = this plugin). Callback mints a 2-minute single-use code; the page calls `/Users/AuthenticateByName` with it and stores the session in jellyfin-web localStorage. If auto sign-in fails, the code is shown to use as password.
4. Not entitled â†’ 403. If the patron already has a linked user and "disable lapsed users" is on, that user is disabled (re-enabled automatically if they return; users disabled by an admin stay disabled).

Patreon users have no password; the only way in is the one-time code after a successful Patreon check. Normal Jellyfin users are unaffected.

## Setup
1. Create a client at https://www.patreon.com/portal/registration/register-clients. Redirect URI: `https://YOUR-JELLYFIN/Patreon/callback`.
2. Build: `dotnet build -c Release`; copy `Jellyfin.Plugin.PatreonAuth.dll` into `<jellyfin data>/plugins/PatreonAuth/`. Restart.
3. Dashboard â†’ Plugins â†’ Patreon Authentication: client id, secret, tier ids (empty = any active patron), optional public base URL (set when behind a reverse proxy).
4. Dashboard â†’ General â†’ Branding â†’ Login disclaimer: `<a href="/Patreon/login">Login with Patreon</a>`.

## DMCA report button
Adds a "Report DMCA" button to item detail pages. Clicking it (signed-in users only) opens a form collecting the elements of a DMCA notice under 17 U.S.C. 512(c)(3)(A): the copyrighted work, the identified item (id/path added automatically), complainant name, email, phone and postal address, good-faith and penalty-of-perjury statements (required checkboxes), and an electronic signature. All fields are required and validated server-side; the notice is posted to a Discord channel via a bot. This is not legal advice; confirm requirements with a lawyer. Nothing is removed automatically; you review and delete the item yourself. Limit: 5 reports per user per hour.

### Discord setup
1. https://discord.com/developers/applications â†’ New Application â†’ Bot â†’ copy the token.
2. Invite the bot to your server (OAuth2 URL generator, scope `bot`, permissions View Channel + Send Messages).
3. Enable Discord developer mode, right-click the server â†’ Copy Server ID (guild id); right-click the channel â†’ Copy Channel ID.
4. Dashboard â†’ Plugins â†’ Patreon Authentication â†’ DMCA reports: tick enable, fill token, guild id, channel id, optional mention (`<@userid>` or `<@&roleid>`).

### Adding the button to jellyfin-web (manual, web client only)
Jellyfin has no plugin hook for item-page UI, so the plugin serves a script at `/Dmca/button.js` that you must load into jellyfin-web. Use either:

**Option A â€“ edit `index.html`** (in the jellyfin-web folder, e.g. `/usr/share/jellyfin/web/index.html` or `C:\Program Files\Jellyfin\Server\jellyfin-web\index.html`). Before `</body>` add:
```html
<script src="/Dmca/button.js" defer></script>
```
If Jellyfin runs under a base URL, prefix the path with it. Back up the file first.

**Option B â€“ JavaScript Injector plugin** (no file edits). Install "JavaScript Injector" from its repo, then add the script:
```js
const s = document.createElement('script'); s.src = '/Dmca/button.js'; s.defer = true; document.head.appendChild(s);
```

> **Redo after upgrades:** Option A is overwritten whenever Jellyfin (jellyfin-web) is updated; re-apply the edit after every Jellyfin upgrade. Also rebuild the plugin for new Jellyfin major versions. The button relies on jellyfin-web's DOM (`.mainDetailButtons`) and may need adjusting in `Web/dmca.js` if the UI changes. Native apps (Android, iOS, TV) do not show the button.

## Limitations
- Entitlement is checked at login only. A lapsed patron keeps an existing session/token until it is revoked or they try to log in again.
- Auto sign-in writes jellyfin-web's `jellyfin_credentials` localStorage format; other clients (apps) should use username + code fallback or the web client first.
