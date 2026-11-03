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
builder.Services.AddSingleton<StreamProbe>();
builder.Services.AddSingleton<JobPipeline>();
builder.Services.AddSingleton<RadarrClient>();
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
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
