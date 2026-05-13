namespace BetterXeneonWidget.Host.Media;

public static class MediaEndpoints
{
    public static IEndpointRouteBuilder MapMediaEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/media");

        group.MapGet("/now-playing", async (MediaService svc) => await svc.GetNowPlayingAsync());

        group.MapPost("/play", async (MediaService svc) =>
            await svc.PlayAsync() ? Results.NoContent() : Results.UnprocessableEntity());

        group.MapPost("/pause", async (MediaService svc) =>
            await svc.PauseAsync() ? Results.NoContent() : Results.UnprocessableEntity());

        group.MapPost("/toggle", async (MediaService svc) =>
            await svc.TogglePlayPauseAsync() ? Results.NoContent() : Results.UnprocessableEntity());

        group.MapPost("/next", async (MediaService svc) =>
        {
            var ok = await svc.NextAsync();
            if (ok) svc.InvalidateArtCache();
            return ok ? Results.NoContent() : Results.UnprocessableEntity();
        });

        group.MapPost("/previous", async (MediaService svc) =>
        {
            var ok = await svc.PreviousAsync();
            if (ok) svc.InvalidateArtCache();
            return ok ? Results.NoContent() : Results.UnprocessableEntity();
        });

        // Album art — bytes change with the track. Clients use ?v=<artVersion>
        // to bust the browser cache without re-fetching now-playing twice.
        group.MapGet("/album-art", (MediaService svc) =>
        {
            var bytes = svc.GetArtBytes(out _);
            if (bytes is null || bytes.Length == 0) return Results.NotFound();
            // SMTC thumbnails are typically JPEG but we don't peek at the bytes;
            // image/jpeg is the safe default and browsers sniff regardless.
            return Results.File(bytes, "image/jpeg");
        });

        return app;
    }
}
