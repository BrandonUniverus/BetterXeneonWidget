using BetterXeneonWidget.Host.Config;
using System.Text.Json;

namespace BetterXeneonWidget.Host.Audio;

public static class AudioSpectrumEndpoints
{
    public static IEndpointRouteBuilder MapAudioSpectrumEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/audio/spectrum");

        // Snapshot — single response. Useful for debugging or low-cadence
        // consumers that don't want to open an SSE stream.
        group.MapGet("/", (AudioSpectrumService svc) => svc.GetSnapshot());

        // Device enumeration lives under the spectrum group (the
        // sibling /api/audio/devices is owned by the audio-switcher
        // widget and lists playback devices for OS-level default
        // switching — different audience). This one lists every active
        // render endpoint so the spectrum capture can point at a
        // specific software-mixer virtual device.
        group.MapGet("/devices", (AudioSpectrumService svc) => svc.ListRenderDevices());

        // Read current capture source. Returns the configured device id
        // (from disk) plus the device we're actually capturing from right
        // now — they can diverge if the saved device was unplugged and we
        // fell back to default.
        group.MapGet("/source", (AudioSpectrumService svc, ConfigService cfg) =>
        {
            var snap = svc.GetSnapshot();
            return Results.Ok(new
            {
                configuredId = cfg.ReadAudioCaptureDeviceId(),
                activeId = snap.DeviceId,
                activeName = snap.DeviceName,
                captureOk = snap.CaptureOk,
            });
        });

        // Set the capture device. Body: { "deviceId": "..." } or null to
        // reset to system default. Persists to %APPDATA%\...\config.json
        // AND restarts the WASAPI capture live — no host restart required.
        // Accepting null lets the user revert via the same endpoint.
        group.MapPost("/source", async (HttpRequest req, AudioSpectrumService svc) =>
        {
            string? deviceId = null;
            try
            {
                var body = await req.ReadFromJsonAsync<SetAudioCaptureSourceRequest>();
                deviceId = body?.DeviceId;
            }
            catch { /* empty / malformed body = treat as null (= reset to default) */ }
            svc.SetCaptureDevice(deviceId, persist: true);
            var snap = svc.GetSnapshot();
            return Results.Ok(new
            {
                activeId = snap.DeviceId,
                activeName = snap.DeviceName,
                captureOk = snap.CaptureOk,
            });
        });

        // Server-Sent Events stream — pushes the current spectrum at ~60Hz.
        // The widget renderer still respects iCUE's reported fpsLimit, but
        // a 60Hz stream keeps high-refresh devices from being starved by
        // stale 30Hz snapshots.
        group.MapGet("/stream", async (HttpContext ctx, AudioSpectrumService svc, CancellationToken ct) =>
        {
            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            // Disable response buffering so each push lands immediately.
            ctx.Response.Headers["X-Accel-Buffering"] = "no";

            var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

            try
            {
                while (!ct.IsCancellationRequested && !ctx.RequestAborted.IsCancellationRequested)
                {
                    var snap = svc.GetSnapshot();
                    var json = JsonSerializer.Serialize(snap, options);
                    await ctx.Response.WriteAsync("data: ", ct);
                    await ctx.Response.WriteAsync(json, ct);
                    await ctx.Response.WriteAsync("\n\n", ct);
                    await ctx.Response.Body.FlushAsync(ct);
                    await Task.Delay(16, ct);  // ~60Hz
                }
            }
            catch (OperationCanceledException) { /* client closed */ }
            catch (Exception)
            {
                // Connection went away mid-write. Nothing to do — the client
                // will reconnect via EventSource's built-in reconnect.
            }
        });

        return app;
    }
}
