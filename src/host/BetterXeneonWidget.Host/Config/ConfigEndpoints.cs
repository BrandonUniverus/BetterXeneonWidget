namespace BetterXeneonWidget.Host.Config;

public static class ConfigEndpoints
{
    public static IEndpointRouteBuilder MapConfigEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/config");

        group.MapGet("/", (ConfigService cfg) => cfg.Read());

        group.MapPost("/pins", (ConfigService cfg, SetPinsRequest req) =>
        {
            cfg.WritePins(req.PinnedIds ?? Array.Empty<string>());
            return Results.NoContent();
        });

        return app;
    }
}
