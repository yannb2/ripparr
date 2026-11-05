using System.Text.Json;
using System.Text.Json.Nodes;
using Ripparr.Media;
using Ripparr.Options;

namespace Ripparr.Arr;

public sealed record MovieMatch(string Title, int Year, int TmdbId, string? ImdbId, string? OriginalLanguage, string? LibraryPath)
{
    /// <summary>TMDb's genres ("Animation"): pick the root folder when adding it to Radarr.</summary>
    public IReadOnlyList<string> Genres { get; init; } = [];

    public bool Animation => Genres.Contains("Animation");
}

/// <summary>Looks up a film in Radarr: the title, year, IDs and original language for names and tags.</summary>
public sealed class RadarrClient(LiveSettings settings, ILogger<RadarrClient> logger)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private ArrOptions Radarr => settings.Radarr;

    public bool Configured => Radarr.Configured;

    public async Task<(List<MovieMatch> Matches, string? Error)> SearchAsync(string term, CancellationToken cancellationToken)
    {
        if (!Configured)
        {
            return ([], "Radarr is not set up (Settings > Integrations).");
        }

        try
        {
            var url = $"{Radarr.Url.TrimEnd('/')}/api/v3/movie/lookup?term={Uri.EscapeDataString(term)}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("X-Api-Key", Radarr.ApiKey);
            using var response = await Http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return ([], $"Radarr answered {(int)response.StatusCode} {response.ReasonPhrase}.");
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            return (Parse(json), null);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning(e, "Radarr lookup failed");
            return ([], $"Could not reach Radarr: {e.Message}");
        }
    }

    public Task<(List<RootFolder> Roots, List<QualityProfile> Profiles, string? Error)> AddOptionsAsync(CancellationToken cancellationToken) =>
        ArrAdd.OptionsAsync(Http, Radarr, "Radarr", cancellationToken);

    /// <summary>Adds the film to Radarr, unmonitored and with no search: it takes the manual import later.</summary>
    public async Task<(MovieMatch? Added, string? Error)> AddAsync(MovieMatch match, string rootFolder, int qualityProfile,
        CancellationToken cancellationToken)
    {
        var (added, error) = await ArrAdd.AddAsync(Http, Radarr, "Radarr", $"movie/lookup?term=tmdb:{match.TmdbId}", "movie", item =>
        {
            item["qualityProfileId"] = qualityProfile;
            item["rootFolderPath"] = rootFolder;
            item["monitored"] = false;
            item["minimumAvailability"] = "released";
            item["addOptions"] = new JsonObject { ["searchForMovie"] = false, ["monitor"] = "none" };
        }, cancellationToken);
        if (added == null)
        {
            return (null, error);
        }

        logger.LogInformation("Added {Title} to Radarr in {Root}", match.Title, rootFolder);
        return (Parse($"[{added.ToJsonString()}]").FirstOrDefault(), null);
    }

    /// <summary>Reads Radarr's movie lookup answer. A film already in Radarr has an id and a path.</summary>
    public static List<MovieMatch> Parse(string json)
    {
        var list = new List<MovieMatch>();
        using var document = JsonDocument.Parse(json);
        foreach (var movie in document.RootElement.EnumerateArray())
        {
            var tmdb = movie.TryGetProperty("tmdbId", out var t) && t.TryGetInt32(out var id) ? id : 0;
            if (tmdb == 0)
            {
                continue;
            }

            string? language = null;
            if (movie.TryGetProperty("originalLanguage", out var original) && original.ValueKind == JsonValueKind.Object
                                                                            && original.TryGetProperty("name", out var name))
            {
                language = Languages.FromName(name.GetString());
            }

            list.Add(new MovieMatch(
                movie.TryGetProperty("title", out var title) ? title.GetString() ?? "" : "",
                movie.TryGetProperty("year", out var year) && year.TryGetInt32(out var y) ? y : 0,
                tmdb,
                movie.TryGetProperty("imdbId", out var imdb) ? imdb.GetString() : null,
                language,
                movie.TryGetProperty("path", out var path) && movie.TryGetProperty("id", out var movieId) && movieId.GetInt32() > 0
                    ? path.GetString()
                    : null)
            {
                Genres = movie.TryGetProperty("genres", out var genres) && genres.ValueKind == JsonValueKind.Array
                    ? genres.EnumerateArray().Where(g => g.ValueKind == JsonValueKind.String).Select(g => g.GetString()!).ToList()
                    : []
            });
        }

        return list;
    }
}
