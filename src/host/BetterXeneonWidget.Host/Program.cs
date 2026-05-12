using BetterXeneonWidget.Host.Audio;
using BetterXeneonWidget.Host.Config;
using BetterXeneonWidget.Host.Diagnostics;
using BetterXeneonWidget.Host.Lyrics;
using BetterXeneonWidget.Host.Media;
using BetterXeneonWidget.Host.Spotify;
using BetterXeneonWidget.Host.System;
using NAudio.CoreAudioApi;

// Logging path comes first so any startup failure below has somewhere to go.
// %LOCALAPPDATA%\BetterXeneonWidget\host.log — same dir as the per-user
// config files, easy to find for the user.
var logDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "BetterXeneonWidget");
var logPath = Path.Combine(logDir, "host.log");
var fileLogger = new FileLoggerProvider(logPath);

void LogFatal(string message, Exception? ex = null)
{
    var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} CRT Startup: {message}";
    if (ex != null) line += Environment.NewLine + ex;
    try { File.AppendAllText(logPath, line + Environment.NewLine); } catch { }
}

try
{
    var builder = WebApplication.CreateBuilder(args);

    // Layer in an optional appsettings.Local.json (gitignored) so a dev or
    // an installed copy can supply secrets like Spotify:ClientId without
    // touching the tracked appsettings.json.
    builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

    builder.Logging.AddProvider(fileLogger);

    var httpPort = builder.Configuration.GetValue<int?>("Listen:Port") ?? 8976;

    // Single HTTP listener on loopback. Spotify OAuth used to need a separate
    // HTTPS listener for the callback (Spotify dropped HTTP-on-loopback in
    // 2025), but we now use a custom URI scheme — `betterxeneonwidget://` —
    // which Windows routes to oauth-forward.vbs in the install dir. The VBS
    // forwards the code to /api/spotify/callback over HTTP, so no cert is
    // needed anywhere.
    builder.WebHost.UseUrls($"http://127.0.0.1:{httpPort}");

    builder.Services.AddSingleton<MMDeviceEnumerator>();
    builder.Services.AddSingleton<AudioService>();
    builder.Services.AddSingleton<SessionService>();
    builder.Services.AddSingleton<IconCache>();
    builder.Services.AddSingleton<ConfigService>();
    builder.Services.AddSingleton<MediaService>();
    // Audio spectrum (WASAPI loopback + FFT). Constructor starts capture
    // eagerly so by the time the first client hits the stream endpoint
    // there's already real data flowing.
    builder.Services.AddSingleton<AudioSpectrumService>();
    // SteelSeries Sonar bridge. Lazy — won't actually try to reach Sonar
    // until the widget asks for status / swaps an output, so missing GG
    // installs cost nothing at startup.
    builder.Services.AddSingleton<SteelSeriesService>();

    builder.Services.Configure<SpotifyOptions>(builder.Configuration.GetSection("Spotify"));
    builder.Services.AddSingleton<SpotifyTokenStore>();
    builder.Services.AddSingleton<SpotifyService>();
    builder.Services.AddHttpClient("spotify");

    builder.Services.AddSingleton<LyricsService>();
    builder.Services.AddHttpClient("lyrics");

    builder.Services.AddCors(options =>
    {
        options.AddDefaultPolicy(policy => policy
            .SetIsOriginAllowed(_ => true) // loopback only — file:// origin is "null"
            .AllowAnyHeader()
            .AllowAnyMethod());
    });

    var app = builder.Build();

    app.UseCors();

    app.MapGet("/api/health", () => Results.Ok(new { status = "ok", service = "betterxeneon-host" }));

    app.MapAudioEndpoints();
    app.MapAudioSpectrumEndpoints();
    app.MapSystemEndpoints();
    app.MapConfigEndpoints();
    app.MapMediaEndpoints();
    app.MapSpotifyEndpoints();
    app.MapLyricsEndpoints();

    // Touch the spectrum service so its constructor (which starts WASAPI
    // capture) runs at startup rather than on first request. Lazy
    // construction would mean the first SSE client sees ~50ms of empty
    // bars while the capture warms up.
    _ = app.Services.GetRequiredService<AudioSpectrumService>();

    app.Logger.LogInformation("Listening on http://127.0.0.1:{Port}", httpPort);

    app.Run();
}
catch (Exception ex)
{
    LogFatal("Host startup failed", ex);
    throw;
}
