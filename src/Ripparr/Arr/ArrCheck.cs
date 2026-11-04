using System.Text.Json;
using Ripparr.Options;

namespace Ripparr.Arr;

/// <summary>The Test button of the Settings page: does Radarr or Sonarr answer with this URL and key?</summary>
public static class ArrCheck
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>Asks /api/v3/system/status and returns whether it answered, with what to show.</summary>
    public static async Task<(bool Ok, string Message)> TestAsync(string app, ArrOptions arr, CancellationToken cancellationToken)
    {
        if (!arr.Configured)
        {
            return (false, "Fill in the URL and the API key.");
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{arr.Url.TrimEnd('/')}/api/v3/system/status");
            request.Headers.Add("X-Api-Key", arr.ApiKey);
            using var response = await Http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return (false, response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                    ? $"{app} refused the API key."
                    : $"{app} answered {(int)response.StatusCode} {response.ReasonPhrase}.");
            }

            return (true, $"Connected to {app} {Version(await response.Content.ReadAsStringAsync(cancellationToken))}".TrimEnd() + ".");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or UriFormatException or InvalidOperationException)
        {
            return (false, $"Could not reach {app}: {e.Message}");
        }
    }

    /// <summary>The "version" of a system/status answer, or empty.</summary>
    public static string Version(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("version", out var version) ? version.GetString() ?? "" : "";
        }
        catch (JsonException)
        {
            return "";
        }
    }
}
