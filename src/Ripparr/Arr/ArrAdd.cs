using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ripparr.Options;

namespace Ripparr.Arr;

public sealed record RootFolder(string Path, long? FreeSpace);

public sealed record QualityProfile(int Id, string Name);

/// <summary>
/// Adds a film to Radarr or a series to Sonarr from Ripparr: the root folders and quality profiles
/// to pick from, then the lookup answer posted back with the picks, unmonitored and with no search,
/// so the arr only learns about it (titles, the manual import later) and grabs nothing.
/// </summary>
public static class ArrAdd
{
    public static async Task<(List<RootFolder> Roots, List<QualityProfile> Profiles, string? Error)> OptionsAsync(
        HttpClient http, ArrOptions arr, string name, CancellationToken cancellationToken)
    {
        var (roots, error) = await SendAsync(http, arr, name, HttpMethod.Get, "rootfolder", null, cancellationToken);
        if (roots == null)
        {
            return ([], [], error);
        }

        var (profiles, error2) = await SendAsync(http, arr, name, HttpMethod.Get, "qualityprofile", null, cancellationToken);
        if (profiles == null)
        {
            return ([], [], error2);
        }

        return (ParseRoots(roots), ParseProfiles(profiles), null);
    }

    public static List<RootFolder> ParseRoots(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateArray()
            .Select(r => new RootFolder(
                r.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "",
                r.TryGetProperty("freeSpace", out var f) && f.TryGetInt64(out var free) ? free : null))
            .Where(r => r.Path.Length > 0)
            .OrderBy(r => r.Path, StringComparer.Ordinal)
            .ToList();
    }

    public static List<QualityProfile> ParseProfiles(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateArray()
            .Select(q => new QualityProfile(
                q.TryGetProperty("id", out var i) && i.TryGetInt32(out var id) ? id : 0,
                q.TryGetProperty("name", out var n) ? n.GetString() ?? "" : ""))
            .Where(q => q.Id > 0)
            .ToList();
    }

    /// <summary>
    /// The root folder that fits, from the library's category folders (live-action, anime,
    /// animated): anime for Japanese animation or Sonarr's Anime type, animated for other
    /// animation, else live-action; else the first.
    /// </summary>
    public static string? GuessRoot(IReadOnlyList<RootFolder> roots, bool animation, bool anime)
    {
        var wanted = anime ? "anime" : animation ? "animated" : "live-action";
        return roots.FirstOrDefault(r => r.Path.TrimEnd('/').EndsWith("/" + wanted, StringComparison.OrdinalIgnoreCase))?.Path
               ?? roots.FirstOrDefault()?.Path;
    }

    /// <summary>
    /// Looks the item up again (<paramref name="lookup"/>, e.g. "series/lookup?term=tvdb:123"),
    /// lets <paramref name="fill"/> set the picks on it and posts it to <paramref name="endpoint"/>.
    /// Returns the arr's answer (with its new id).
    /// </summary>
    public static async Task<(JsonObject? Added, string? Error)> AddAsync(HttpClient http, ArrOptions arr, string name,
        string lookup, string endpoint, Action<JsonObject> fill, CancellationToken cancellationToken)
    {
        var (found, error) = await SendAsync(http, arr, name, HttpMethod.Get, lookup, null, cancellationToken);
        if (found == null)
        {
            return (null, error);
        }

        if (JsonNode.Parse(found) is not JsonArray { Count: > 0 } list || list[0] is not JsonObject item)
        {
            return (null, $"{name} could not find it any more.");
        }

        item.Remove("id");
        fill(item);
        var (added, error2) = await SendAsync(http, arr, name, HttpMethod.Post, endpoint, item.ToJsonString(), cancellationToken);
        return added == null ? (null, error2) : (JsonNode.Parse(added) as JsonObject, null);
    }

    private static async Task<(string? Json, string? Error)> SendAsync(HttpClient http, ArrOptions arr, string name, HttpMethod method,
        string path, string? body, CancellationToken cancellationToken)
    {
        if (!arr.Configured)
        {
            return (null, $"{name} is not set up (Settings > Integrations).");
        }

        try
        {
            using var request = new HttpRequestMessage(method, $"{arr.Url.TrimEnd('/')}/api/v3/{path}");
            request.Headers.Add("X-Api-Key", arr.ApiKey);
            if (body != null)
            {
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            }

            using var response = await http.SendAsync(request, cancellationToken);
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return (null, $"{name} answered {(int)response.StatusCode} {response.ReasonPhrase}{Reason(text)}");
            }

            return (text, null);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return (null, $"Could not reach {name}: {e.Message}");
        }
    }

    // The arr's own words for a refused add ("This series has already been added", a missing field).
    private static string Reason(string text)
    {
        try
        {
            var node = JsonNode.Parse(text);
            var messages = node switch
            {
                JsonArray array => array.Select(e => e?["errorMessage"]?.GetValue<string>()).OfType<string>().ToList(),
                JsonObject o when o["message"]?.GetValue<string>() is { } m => [m],
                _ => []
            };
            return messages.Count > 0 ? ": " + string.Join(" ", messages) : ".";
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            return ".";
        }
    }
}
