using System.Globalization;
using Ripparr;
using Ripparr.Arr;
using Ripparr.Components;
using Ripparr.Disc;
using Ripparr.Jobs;
using Ripparr.Options;
using Ripparr.Tools;

// Numbers and dates the same way everywhere (23.976 fps, not 23,976).
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<RipparrOptions>()
    .Bind(builder.Configuration.GetSection(RipparrOptions.Section))
    .PostConfigure(o => o.WithDefaults());

builder.Services.AddSingleton<JobStore>();
builder.Services.AddSingleton<JobEvents>();
builder.Services.AddSingleton<DiscAnalyzer>();
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
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
