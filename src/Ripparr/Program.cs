using System.Globalization;
using Ripparr;
using Ripparr.Arr;
using Ripparr.Components;
using Ripparr.Disc;
using Ripparr.Jobs;
using Ripparr.Options;
using Ripparr.Profiles;
using Ripparr.Subtitles;
using Ripparr.Tools;

// Numbers and dates the same way everywhere (23.976 fps, not 23,976).
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<RipparrOptions>()
    .Bind(builder.Configuration.GetSection(RipparrOptions.Section))
    .PostConfigure(o => o.WithDefaults());

builder.Services.AddSingleton<JobStore>();
builder.Services.AddScoped<Toasts>();
builder.Services.AddSingleton<JobEvents>();
builder.Services.AddSingleton<DiscAnalyzer>();
builder.Services.AddSingleton<DiscDrafts>();
builder.Services.AddSingleton<StreamProbe>();
builder.Services.AddSingleton<SubtitleScanner>();
builder.Services.AddSingleton<Thumbnails>();
builder.Services.AddSingleton<JobPipeline>();
builder.Services.AddSingleton<RadarrClient>();
builder.Services.AddSingleton<SonarrClient>();
builder.Services.AddSingleton<JobRunner>();
builder.Services.AddHostedService(services => services.GetRequiredService<JobRunner>());
builder.Services.AddSingleton<ToolStatusService>();
builder.Services.AddHostedService(services => services.GetRequiredService<ToolStatusService>());

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error", createScopeForErrors: true);
}

app.UseAntiforgery();
app.MapStaticAssets();
app.MapGet("/health", () => Results.Ok("ok"));

// Counts for a dashboard (Homepage's custom API widget). Read-only, no key.
app.MapGet("/api/stats", (JobStore store, ToolStatusService tools) => Results.Ok(Stats.From(store, tools)));
// Subtitle preview: one caption as a PNG, and the SRT a job made. Keys come from the job, not from the path.
app.MapGet("/api/jobs/{id:long}/subtitles/{key}/{index:int}.png",
    (long id, string key, int index, JobStore store, Microsoft.Extensions.Options.IOptions<RipparrOptions> options) =>
    {
        var job = store.Get(id);
        var path = job == null ? null : SubtitleStep.PreviewFile(options.Value, job, key, ".sup");
        var caption = path == null ? null : CaptionCache.Get(path).FirstOrDefault(c => c.Index == index);
        return caption == null
            ? Results.NotFound()
            : Results.File(Png.Rgba(caption.Render()), "image/png");
    });
app.MapGet("/api/jobs/{id:long}/subtitles/{key}.srt",
    (long id, string key, JobStore store, Microsoft.Extensions.Options.IOptions<RipparrOptions> options) =>
    {
        var job = store.Get(id);
        var path = job == null ? null : SubtitleStep.PreviewFile(options.Value, job, key, ".srt");
        return path == null
            ? Results.NotFound()
            : Results.File(path, "application/x-subrip", $"{Naming.SafeFileName(job!.DisplayTitle)}.{key}.srt");
    });
// The same for a disc's subtitles read before the job. The key and PID have to be a scan's own.
app.MapGet("/api/discs/{key}/subtitles/{pid:int}/{index:int}.png",
    (string key, int pid, int index, SubtitleScanner scanner) =>
    {
        var path = scanner.SupFile(key, pid);
        var caption = path == null ? null : CaptionCache.Get(path).FirstOrDefault(c => c.Index == index);
        return caption == null
            ? Results.NotFound()
            : Results.File(Png.Rgba(caption.Render()), "image/png");
    });
// Pictures of a playlist for the extras list: only discs inside the configured sources.
app.MapGet("/api/discs/pictures/{index:int}.jpg",
    async (int index, string path, string playlist, DiscAnalyzer analyzer, Thumbnails thumbnails) =>
    {
        var source = index is >= 0 and < Thumbnails.Count && analyzer.InSources(path) ? analyzer.Describe(path) : null;
        if (source == null)
        {
            return Results.NotFound();
        }

        var analysis = await analyzer.AnalyseAsync(source, CancellationToken.None);
        var found = analysis.Find(playlist);
        if (found == null)
        {
            return Results.NotFound();
        }

        var (file, error) = await thumbnails.GetAsync(source, analysis, found, index);
        return file == null ? Results.Problem(error, statusCode: 404) : Results.File(file, "image/jpeg");
    });
// 20 seconds of an audio track, to tell a commentary from an audio description: only discs inside the sources.
app.MapGet("/api/discs/listen/{pid:int}.m4a",
    async (int pid, string path, string playlist, DiscAnalyzer analyzer, Thumbnails thumbnails) =>
    {
        var source = analyzer.InSources(path) ? analyzer.Describe(path) : null;
        if (source == null)
        {
            return Results.NotFound();
        }

        var analysis = await analyzer.AnalyseAsync(source, CancellationToken.None);
        var found = analysis.Find(playlist);
        if (found == null || !found.Mpls.Streams.Any(s => s.Kind == StreamKind.Audio && s.Pid == pid))
        {
            return Results.NotFound();
        }

        var (file, error) = await thumbnails.SampleAsync(source, analysis, found, pid);
        return file == null ? Results.Problem(error, statusCode: 404) : Results.File(file, "audio/mp4", enableRangeProcessing: true);
    });
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
