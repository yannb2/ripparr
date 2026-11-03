using Microsoft.Extensions.Options;
using Ripparr.Options;

namespace Ripparr.Tools;

public sealed record ToolStatus
{
    public required ToolDefinition Tool { get; init; }
    public string? Installed { get; init; }
    public bool Missing { get; init; }
    public string? Latest { get; init; }

    /// <summary>A newer major version exists but is held back (MakeMKV 2.x).</summary>
    public string? HeldBack { get; init; }

    public string? UpstreamError { get; init; }

    public string? Pinned => Tool.Pinned;

    public bool UpdateAvailable => Latest != null && (Installed ?? Pinned) is { } current
                                   && VersionComparer.Instance.Compare(Latest, current) > 0;
}

public sealed record MakeMkvState(string? MaskedKey, IReadOnlyList<string> Messages, string? Version, KeyCheck Check);

/// <summary>
/// Asks every tool for its version when the app starts and looks for newer versions
/// upstream once a day. Nothing is installed: an update is a new image.
/// </summary>
public sealed class ToolStatusService(IOptions<RipparrOptions> options, ILogger<ToolStatusService> logger) : BackgroundService
{
    private static readonly HttpClient Http = CreateClient();
    private readonly RipparrOptions _options = options.Value;
    private readonly SemaphoreSlim _checkNow = new(0);

    public IReadOnlyList<ToolStatus> Statuses { get; private set; } = [];
    public MakeMkvState? MakeMkv { get; private set; }
    public DateTime? InstalledCheckedAt { get; private set; }
    public DateTime? UpstreamCheckedAt { get; private set; }
    public bool Checking { get; private set; }

    public event Action? Changed;

    public void CheckNow() => _checkNow.Release();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await RefreshAsync(_options.Updates.Enabled, stoppingToken);
            try
            {
                var interval = TimeSpan.FromHours(Math.Max(1, _options.Updates.IntervalHours));
                await _checkNow.WaitAsync(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public async Task RefreshAsync(bool upstream, CancellationToken cancellationToken)
    {
        Checking = true;
        Changed?.Invoke();
        try
        {
            var statuses = new List<ToolStatus>();
            foreach (var tool in ToolCatalog.All)
            {
                statuses.Add(await CheckAsync(tool, upstream, cancellationToken));
            }

            Statuses = statuses;
            InstalledCheckedAt = DateTime.UtcNow;
            if (upstream)
            {
                UpstreamCheckedAt = DateTime.UtcNow;
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "Tool check failed");
        }
        finally
        {
            Checking = false;
            Changed?.Invoke();
        }
    }

    private async Task<ToolStatus> CheckAsync(ToolDefinition tool, bool upstream, CancellationToken cancellationToken)
    {
        var command = tool.Command(_options.Tools);
        var spec = new ProcessSpec(command, tool.VersionArguments);
        if (tool.Id == "makemkv")
        {
            Directory.CreateDirectory(_options.MakeMkvHome);
            spec = spec with { Environment = new Dictionary<string, string> { ["HOME"] = Path.GetFullPath(_options.MakeMkvHome) } };
        }

        var result = await ProcessRunner.RunAsync(spec, TimeSpan.FromSeconds(45), null, cancellationToken);
        var output = result.Output + "\n" + result.Error;
        var installed = result.NotStarted ? null : ToolCatalog.InstalledVersion(tool, output);
        if (tool.Id == "makemkv")
        {
            var key = Tools.MakeMkv.ReadKey(_options.MakeMkvHome);
            var messages = result.NotStarted ? [] : Tools.MakeMkv.KeyMessages(output);
            MakeMkv = new MakeMkvState(key == null ? null : Tools.MakeMkv.Mask(key), messages, installed,
                Tools.MakeMkv.Assess(key != null, !result.NotStarted && installed != null, messages));
        }

        string? latest = null, heldBack = null, error = null;
        var previous = Statuses.FirstOrDefault(s => s.Tool.Id == tool.Id);
        if (upstream && tool.Upstream != UpstreamKind.None)
        {
            (latest, heldBack, error) = await LatestAsync(tool, installed ?? tool.Pinned, cancellationToken);
        }
        else if (previous != null)
        {
            (latest, heldBack, error) = (previous.Latest, previous.HeldBack, previous.UpstreamError);
        }

        return new ToolStatus
        {
            Tool = tool,
            Installed = installed,
            Missing = result.NotStarted,
            Latest = latest,
            HeldBack = heldBack,
            UpstreamError = error
        };
    }

    private async Task<(string? Latest, string? HeldBack, string? Error)> LatestAsync(ToolDefinition tool, string? current,
        CancellationToken cancellationToken)
    {
        try
        {
            if (tool.Upstream == UpstreamKind.GitHubRelease)
            {
                var json = await Http.GetStringAsync($"https://api.github.com/repos/{tool.UpstreamSources[0]}/releases?per_page=30", cancellationToken);
                return (ToolCatalog.GitHubTag(json), null, null);
            }

            string? lastError = null;
            foreach (var url in tool.UpstreamSources)
            {
                try
                {
                    var page = await Http.GetStringAsync(url, cancellationToken);
                    var highest = ToolCatalog.HighestOnPage(tool.UpstreamPattern!, page);
                    if (highest == null)
                    {
                        lastError = $"no version found on {url}";
                        continue;
                    }

                    // MakeMKV: a new major version is only offered once allowed in the settings.
                    if (tool.Id == "makemkv" && !_options.Updates.AllowMakeMkvMajor && current != null
                        && ToolCatalog.Major(highest) > ToolCatalog.Major(current))
                    {
                        var sameMajor = ToolCatalog.HighestOnPage(tool.UpstreamPattern!, page,
                            v => ToolCatalog.Major(v) == ToolCatalog.Major(current));
                        return (sameMajor, highest, null);
                    }

                    return (highest, null, null);
                }
                catch (HttpRequestException e)
                {
                    lastError = $"{url}: {e.Message}";
                }
            }

            return (null, null, lastError);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return (null, null, e.Message);
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"Ripparr/{AppInfo.Version} (+https://github.com/yannb2/ripparr)");
        return client;
    }

    /// <summary>Fetches the current beta key from the official forum thread, for installs without a purchased key.</summary>
    public static async Task<(string? Key, string? Error)> FetchBetaKeyAsync(CancellationToken cancellationToken)
    {
        const string url = "https://forum.makemkv.com/forum/viewtopic.php?t=1053";
        try
        {
            var page = await Http.GetStringAsync(url, cancellationToken);
            var key = Tools.MakeMkv.FindBetaKey(page);
            return key == null ? (null, "No key found on the forum page.") : (key, null);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return (null, $"Could not read the forum page: {e.Message}");
        }
    }
}
