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

        // Server-Sent Events stream — pushes the current spectrum at ~30Hz.
        // The visualizer canvas redraws on requestAnimationFrame (60Hz) and
        // just reads the latest stored snapshot, so 30Hz delivery is plenty
        // — the exponential smoothing inside the spectrum service already
        // gives the bars a fluid feel between updates.
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
                    await Task.Delay(33, ct);  // ~30Hz
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
