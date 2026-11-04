using System.Text.Json;
using Ripparr.Media;
using Ripparr.Options;

namespace Ripparr.Arr;

/// <summary>A series from Sonarr's lookup. <see cref="SonarrId"/> is set when the series is in Sonarr (it then has episodes).</summary>
public sealed record SeriesMatch(string Title, int Year, int TvdbId, int? TmdbId, string? ImdbId, string? OriginalLanguage,
    bool Anime, int? SonarrId, string? LibraryPath, IReadOnlyList<int> Seasons);

/// <summary>One episode of a series in Sonarr.</summary>
public sealed record EpisodeInfo(int Season, int Episode, int? Absolute, string Title);

/// <summary>Looks up a series in Sonarr: its title, year, IDs, language, type (anime numbers) and episode titles.</summary>
public sealed class SonarrClient(LiveSettings settings, ILogger<SonarrClient> logger)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private ArrOptions Sonarr => settings.Sonarr;

    public bool Configured => Sonarr.Configured;

    public async Task<(List<SeriesMatch> Matches, string? Error)> SearchAsync(string term, CancellationToken cancellationToken)
    {
        var (json, error) = await GetAsync($"series/lookup?term={Uri.EscapeDataString(term)}", cancellationToken);
        return json == null ? ([], error) : (ParseSeries(json), null);
    }

    public async Task<(List<EpisodeInfo> Episodes, string? Error)> EpisodesAsync(int seriesId, CancellationToken cancellationToken)
    {
        var (json, error) = await GetAsync($"episode?seriesId={seriesId}", cancellationToken);
        return json == null ? ([], error) : (ParseEpisodes(json), null);
    }

    private async Task<(string? Json, string? Error)> GetAsync(string path, CancellationToken cancellationToken)
    {
        if (!Configured)
        {
            return (null, "Sonarr is not set up (Settings > Integrations).");
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{Sonarr.Url.TrimEnd('/')}/api/v3/{path}");
            request.Headers.Add("X-Api-Key", Sonarr.ApiKey);
            using var response = await Http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return (null, $"Sonarr answered {(int)response.StatusCode} {response.ReasonPhrase}.");
            }

            return (await response.Content.ReadAsStringAsync(cancellationToken), null);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(e, "Sonarr request failed");
            return (null, $"Could not reach Sonarr: {e.Message}");
        }
    }

    /// <summary>Reads Sonarr's series lookup answer. A series already in Sonarr has an id and a path.</summary>
    public static List<SeriesMatch> ParseSeries(string json)
    {
        var list = new List<SeriesMatch>();
        using var document = JsonDocument.Parse(json);
        foreach (var series in document.RootElement.EnumerateArray())
        {
            var tvdb = Int(series, "tvdbId") ?? 0;
            if (tvdb == 0)
            {
                continue;
            }

            string? language = null;
            if (series.TryGetProperty("originalLanguage", out var original) && original.ValueKind == JsonValueKind.Object
                                                                             && original.TryGetProperty("name", out var name))
            {
                language = Languages.FromName(name.GetString());
            }

            var seasons = new List<int>();
            if (series.TryGetProperty("seasons", out var list2) && list2.ValueKind == JsonValueKind.Array)
            {
                seasons.AddRange(list2.EnumerateArray().Select(s => Int(s, "seasonNumber")).OfType<int>().Order());
            }

            int? id = Int(series, "id") is > 0 and var i ? i : null;
            list.Add(new SeriesMatch(
                Text(series, "title") ?? "",
                Int(series, "year") ?? 0,
                tvdb,
                Int(series, "tmdbId") is > 0 and var tmdb ? tmdb : null,
                Text(series, "imdbId") is { Length: > 0 } imdb ? imdb : null,
                language,
                string.Equals(Text(series, "seriesType"), "anime", StringComparison.OrdinalIgnoreCase),
                id,
                id != null ? Text(series, "path") : null,
                seasons));
        }

        return list;
    }

    public static List<EpisodeInfo> ParseEpisodes(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateArray()
            .Select(e => new EpisodeInfo(Int(e, "seasonNumber") ?? 0, Int(e, "episodeNumber") ?? 0,
                Int(e, "absoluteEpisodeNumber") is > 0 and var a ? a : null, Text(e, "title") ?? ""))
            .Where(e => e.Episode > 0)
            .OrderBy(e => e.Season).ThenBy(e => e.Episode)
            .ToList();
    }

    private static int? Int(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
