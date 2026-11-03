using System.Text.Json;
using Microsoft.Extensions.Options;
using Ripparr.Media;
using Ripparr.Options;

namespace Ripparr.Arr;

public sealed record MovieMatch(string Title, int Year, int TmdbId, string? ImdbId, string? OriginalLanguage, string? LibraryPath);

/// <summary>Looks up a film in Radarr: the title, year, IDs and original language for names and tags.</summary>
public sealed class RadarrClient(IOptions<RipparrOptions> options, ILogger<RadarrClient> logger)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly ArrOptions _radarr = options.Value.Radarr;

    public bool Configured => _radarr.Configured;

    public async Task<(List<MovieMatch> Matches, string? Error)> SearchAsync(string term, CancellationToken cancellationToken)
    {
        if (!Configured)
        {
            return ([], "Radarr is not set up (Ripparr__Radarr__Url and Ripparr__Radarr__ApiKey).");
        }

        try
        {
            var url = $"{_radarr.Url.TrimEnd('/')}/api/v3/movie/lookup?term={Uri.EscapeDataString(term)}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("X-Api-Key", _radarr.ApiKey);
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
                    : null));
        }

        return list;
    }
}
