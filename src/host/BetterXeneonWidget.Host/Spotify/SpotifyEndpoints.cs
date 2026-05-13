// The project's `BetterXeneonWidget.Host.System` namespace shadows the BCL
// `System.*` namespaces inside this file, so the standard `System.Text.Json.*`
// references would fail to resolve. Importing via alias dodges that without
// having to global:: every type. Same pattern SpotifyService.cs uses.
using SysJson = global::System.Text.Json;
using BetterXeneonWidget.Host.Media;

namespace BetterXeneonWidget.Host.Spotify;

public static class SpotifyEndpoints
{
    public static IEndpointRouteBuilder MapSpotifyEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/spotify");

        group.MapPost("/connect", (SpotifyService svc) =>
        {
            if (!svc.IsConfigured)
                return Results.Problem(
                    detail: "Spotify ClientId not set. Add it to appsettings.json or BETTERXENEON_SPOTIFY_CLIENT_ID env var.",
                    statusCode: 503);
            return svc.BeginAuth() ? Results.NoContent() : Results.Problem("Failed to start auth", statusCode: 500);
        });

        // Spotify redirects the user's browser back here after they authorize.
        // Returns a tiny HTML page that thanks them and tries to close itself —
        // browsers only honor window.close() for windows opened by JS, so the
        // self-close is best-effort. Either way the widget polls /status to
        // detect completion within a poll cycle.
        group.MapGet("/callback", async (string? code, string? state, string? error, SpotifyService svc) =>
        {
            if (!string.IsNullOrEmpty(error))
                return Results.Content(BuildHtml(false, $"Spotify returned: {error}"), "text/html");

            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
                return Results.Content(BuildHtml(false, "Missing code or state from Spotify"), "text/html");

            var ok = await svc.HandleCallbackAsync(code, state);
            return Results.Content(BuildHtml(ok, ok ? null : "Token exchange failed"), "text/html");
        });

        group.MapGet("/status", (SpotifyService svc) => svc.GetStatus());

        group.MapPost("/disconnect", (SpotifyService svc) =>
        {
            svc.Disconnect();
            return Results.NoContent();
        });

        group.MapGet("/queue", async (HttpRequest req, SpotifyService svc) =>
            await svc.GetQueueAsync(
                string.Equals(req.Query["force"].ToString(), "true", global::System.StringComparison.OrdinalIgnoreCase)));
        group.MapGet("/recently-played", async (SpotifyService svc) => await svc.GetRecentlyPlayedAsync());
        group.MapGet("/playlists", async (SpotifyService svc) => await svc.GetPlaylistsAsync());

        // Spotify Connect device list. Mostly diagnostic — when the widget
        // sees Spotify playing in SMTC but /me/player returns nothing, this
        // says whether the desktop is "known" to Spotify Connect or not.
        group.MapGet("/devices", async (SpotifyService svc) => await svc.GetDevicesAsync());

        // Force the local computer to be the active Spotify Connect device.
        // The widget calls this when it detects the SMTC + /me/player split
        // (audio playing, no API session). Body forwards a chosen deviceId,
        // or we pick the first Computer-type device if none provided.
        group.MapPost("/devices/activate", async (HttpRequest req, SpotifyService svc) =>
        {
            string? requestedId = null;
            try
            {
                using var doc = await SysJson.JsonDocument.ParseAsync(req.Body);
                if (doc.RootElement.ValueKind == SysJson.JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("deviceId", out var idEl)
                    && idEl.ValueKind == SysJson.JsonValueKind.String)
                {
                    requestedId = idEl.GetString();
                }
            }
            catch { /* empty body is fine — fall through to autopick */ }

            string? targetId = requestedId;
            if (string.IsNullOrWhiteSpace(targetId))
            {
                var devices = await svc.GetDevicesAsync();
                // Prefer an already-active device (no-op activation); else
                // a non-restricted Computer-type; else first non-restricted.
                targetId = devices.FirstOrDefault(d => d.IsActive)?.Id
                        ?? devices.FirstOrDefault(d => d is { IsRestricted: false, Type: "Computer" })?.Id
                        ?? devices.FirstOrDefault(d => !d.IsRestricted)?.Id;
            }
            if (string.IsNullOrWhiteSpace(targetId))
                return Results.UnprocessableEntity(new { error = "No Spotify Connect device available to activate." });
            var ok = await svc.TransferPlaybackAsync(targetId);
            return ok ? Results.NoContent() : Results.UnprocessableEntity(new { error = "Spotify rejected the transfer." });
        });

        group.MapGet("/playback", async (SpotifyService svc) => await svc.GetPlaybackAsync());

        group.MapPost("/playback/play",     async (SpotifyService svc) =>
            await svc.PlaybackResumeAsync()   ? Results.NoContent() : Results.UnprocessableEntity());
        group.MapPost("/playback/pause",    async (SpotifyService svc) =>
            await svc.PlaybackPauseAsync()    ? Results.NoContent() : Results.UnprocessableEntity());
        group.MapPost("/playback/next", async (SpotifyService svc, MediaService media) =>
        {
            var ok = await svc.PlaybackNextAsync();
            if (ok) media.InvalidateArtCache();
            return ok ? Results.NoContent() : Results.UnprocessableEntity();
        });
        group.MapPost("/playback/previous", async (SpotifyService svc, MediaService media) =>
        {
            var ok = await svc.PlaybackPreviousAsync();
            if (ok) media.InvalidateArtCache();
            return ok ? Results.NoContent() : Results.UnprocessableEntity();
        });

        group.MapPost("/play/track/{id}", async (string id, SpotifyService svc, MediaService media) =>
        {
            var ok = await svc.PlayTrackAsync(id);
            if (ok) media.InvalidateArtCache();
            return ok ? Results.NoContent() : Results.UnprocessableEntity(
                new { error = "Spotify rejected playback. Check that Spotify is open on a device, your account is Premium, and you've reauthorized after the recent scope addition." });
        });

        group.MapPost("/play/playlist/{id}", async (string id, SpotifyService svc, MediaService media) =>
        {
            var ok = await svc.PlayPlaylistAsync(id);
            if (ok) media.InvalidateArtCache();
            return ok ? Results.NoContent() : Results.UnprocessableEntity(
                new { error = "Spotify rejected playback. Check that Spotify is open on a device, your account is Premium, and you've reauthorized after the recent scope addition." });
        });

        return app;
    }

    private static string BuildHtml(bool success, string? errorMessage)
    {
        var headline = success ? "Connected to Spotify" : "Connection failed";
        var color = success ? "#1db954" : "#ff5560";
        var detail = success
            ? "You can close this window and return to your Xeneon Edge."
            : errorMessage ?? "Please try connecting again from the widget.";
        return $$"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8">
            <title>{{headline}}</title>
            <style>
              :root { color-scheme: dark; }
              body { font-family: 'Segoe UI', system-ui, sans-serif; background: #0a0a0c;
                     color: #fff; display: grid; place-items: center; min-height: 100vh;
                     margin: 0; text-align: center; padding: 1rem; }
              h1 { color: {{color}}; margin: 0 0 0.5rem; font-size: 2rem; }
              p { opacity: 0.75; max-width: 32ch; }
            </style>
            </head><body>
              <main>
                <h1>{{headline}}</h1>
                <p>{{detail}}</p>
              </main>
              <script>setTimeout(()=>{ try { window.close(); } catch(e) {} }, 1500);</script>
            </body></html>
            """;
    }
}
