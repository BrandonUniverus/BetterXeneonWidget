using System.Text.Json.Nodes;

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

        // Widget settings — opaque JSON object owned entirely by the
        // media-v2 widget. We persist it without inspecting the schema
        // (lets the widget add settings without touching the host) and
        // return it on read. POST replaces the entire object — clients
        // are expected to send all keys they want to keep.
        var widget = app.MapGroup("/api/widget");
        widget.MapGet("/settings", (ConfigService cfg) => cfg.ReadWidgetSettings());
        widget.MapPost("/settings", async (HttpRequest req, ConfigService cfg) =>
        {
            JsonObject? body = null;
            try
            {
                var node = await JsonNode.ParseAsync(req.Body);
                body = node as JsonObject;
            }
            catch
            {
                /* invalid JSON — fall through to BadRequest below */
            }
            if (body is null) return Results.BadRequest(new { error = "Body must be a JSON object." });
            cfg.WriteWidgetSettings(body);
            return Results.NoContent();
        });

        return app;
    }
}
