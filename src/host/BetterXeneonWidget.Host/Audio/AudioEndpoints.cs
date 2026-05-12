namespace BetterXeneonWidget.Host.Audio;

public static class AudioEndpoints
{
    public static IEndpointRouteBuilder MapAudioEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/audio");

        group.MapGet("/devices", (AudioService svc) => svc.ListPlaybackDevices());

        group.MapPost("/default", (AudioService svc, SetDefaultRequest req) =>
        {
            svc.SetDefault(req.Id);
            return Results.NoContent();
        });

        group.MapGet("/volume", (AudioService svc) => svc.GetDefaultVolume());

        group.MapPost("/volume", (AudioService svc, SetVolumeRequest req) =>
        {
            svc.SetDefaultVolume(req.Level);
            return Results.NoContent();
        });

        group.MapPost("/mute", (AudioService svc, SetMuteRequest req) =>
        {
            svc.SetDefaultMute(req.Muted);
            return Results.NoContent();
        });

        // Per-device targeted controls (used by pinned-output rows in the widget).
        group.MapPost("/devices/volume", (AudioService svc, SetDeviceVolumeRequest req) =>
        {
            svc.SetDeviceVolume(req.Id, req.Level);
            return Results.NoContent();
        });

        group.MapPost("/devices/mute", (AudioService svc, SetDeviceMuteRequest req) =>
        {
            svc.SetDeviceMute(req.Id, req.Muted);
            return Results.NoContent();
        });

        group.MapGet("/devices/icon", (string id, AudioService audio, IconCache cache) =>
        {
            var iconPath = audio.GetDeviceIconPath(id);
            if (string.IsNullOrEmpty(iconPath)) return Results.NotFound();
            var bytes = cache.Get(iconPath);
            if (bytes is null || bytes.Length == 0) return Results.NotFound();
            return Results.File(bytes, "image/png");
        });

        // Audio sessions (apps mixer view).
        group.MapGet("/sessions", (SessionService svc) => svc.ListSessions());

        group.MapPost("/sessions/volume", (SessionService svc, SetSessionVolumeRequest req) =>
        {
            return svc.SetSessionVolume(req.Id, req.Level)
                ? Results.NoContent()
                : Results.NotFound(new { error = "session not found" });
        });

        group.MapPost("/sessions/mute", (SessionService svc, SetSessionMuteRequest req) =>
        {
            return svc.SetSessionMute(req.Id, req.Muted)
                ? Results.NoContent()
                : Results.NotFound(new { error = "session not found" });
        });

        group.MapGet("/sessions/icon", (string id, SessionService sessions, IconCache cache) =>
        {
            var iconPath = sessions.GetSessionIconPath(id);
            if (string.IsNullOrEmpty(iconPath)) return Results.NotFound();
            var bytes = cache.Get(iconPath);
            if (bytes is null || bytes.Length == 0) return Results.NotFound();
            return Results.File(bytes, "image/png");
        });

        // SteelSeries Sonar output device cycling. /status returns whether
        // Sonar is reachable + the current "all-output" device + the full
        // device list. /swap cycles to the next physical device (mirrors
        // the Sonar hotkey). /probe is a debug dump for iterating on the
        // parse logic when the response shape doesn't match expectations.
        group.MapGet("/steelseries/status", async (SteelSeriesService svc, CancellationToken ct) =>
            await svc.GetStatusAsync(ct));

        group.MapPost("/steelseries/swap", async (SteelSeriesService svc, CancellationToken ct) =>
        {
            var result = await svc.CycleNextAsync(ct);
            return result.Ok ? Results.Ok(result) : Results.Json(result, statusCode: 502);
        });

        group.MapGet("/steelseries/probe", async (SteelSeriesService svc, CancellationToken ct) =>
            Results.Json(await svc.ProbeAsync(ct)));

        return app;
    }
}
