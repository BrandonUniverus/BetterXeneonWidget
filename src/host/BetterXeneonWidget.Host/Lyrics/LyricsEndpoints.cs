namespace BetterXeneonWidget.Host.Lyrics;

public static class LyricsEndpoints
{
    public static IEndpointRouteBuilder MapLyricsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/lyrics", async (string artist, string title, string? album, int? duration, LyricsService svc) =>
        {
            var dto = await svc.GetAsync(artist, title, album, duration);
            return Results.Ok(dto);
        });
        return app;
    }
}
