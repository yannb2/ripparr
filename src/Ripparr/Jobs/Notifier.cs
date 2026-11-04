using System.Net.Http.Headers;
using System.Text;
using Ripparr.Options;

namespace Ripparr.Jobs;

/// <summary>A message about a job, as ntfy shows it.</summary>
public sealed record JobMessage(string Title, string Body, string Tags, string Priority);

/// <summary>
/// Says when a job ends: an ntfy message for a good or failed job, and a Jellyfin library scan after
/// a good one. Failures here are logged, never fail the job.
/// </summary>
public sealed class Notifier(LiveSettings settings, ILogger<Notifier> logger)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>What to send for this job, or null when the settings say nothing.</summary>
    public static JobMessage? MessageFor(Job job, NotificationOptions options)
    {
        if (!options.NtfyConfigured)
        {
            return null;
        }

        var dry = job.DryRun ? " (dry run)" : "";
        return job.Status switch
        {
            JobStatus.Done when options.OnDone && (!job.DryRun || options.DryRuns) =>
                new JobMessage($"Ripparr: {job.DisplayTitle} is done{dry}",
                    job.OutputPath is { Length: > 0 } output ? Path.GetFileName(output) : $"Playlist {job.Playlist} of {job.SourceName}",
                    "white_check_mark", "default"),
            JobStatus.Failed when options.OnFailed =>
                new JobMessage($"Ripparr: {job.DisplayTitle} failed{dry}", job.Error ?? "No reason given.", "x", "high"),
            _ => null
        };
    }

    /// <summary>Called by the runner once a job has its final status.</summary>
    public async Task JobEndedAsync(Job job, CancellationToken cancellationToken)
    {
        var options = settings.Notifications;
        if (MessageFor(job, options) is { } message && await SendAsync(options, message, cancellationToken) is { } error)
        {
            logger.LogWarning("ntfy message for job {Id} not sent: {Error}", job.Id, error);
        }

        if (job is { Status: JobStatus.Done, DryRun: false } && options is { RefreshJellyfin: true, JellyfinConfigured: true }
                                                              && await RefreshJellyfinAsync(options, cancellationToken) is { } refresh)
        {
            logger.LogWarning("Jellyfin scan after job {Id} not started: {Error}", job.Id, refresh);
        }
    }

    /// <summary>Posts the message to the ntfy topic. Returns the error, or null when it was sent.</summary>
    public static async Task<string?> SendAsync(NotificationOptions options, JobMessage message, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, options.NtfyUrl.Trim());
            request.Content = new StringContent(message.Body, Encoding.UTF8, "text/plain");
            // Headers are ASCII only; ntfy reads RFC 2047 encoded words for the rest (accented titles).
            request.Headers.TryAddWithoutValidation("Title", HeaderValue(message.Title));
            request.Headers.TryAddWithoutValidation("Tags", message.Tags);
            request.Headers.TryAddWithoutValidation("Priority", message.Priority);
            if (!string.IsNullOrWhiteSpace(options.NtfyToken))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.NtfyToken.Trim());
            }

            using var response = await Http.SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode ? null : $"ntfy answered {(int)response.StatusCode} {response.ReasonPhrase}.";
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or UriFormatException or InvalidOperationException)
        {
            return $"Could not reach ntfy: {e.Message}";
        }
    }

    /// <summary>Asks Jellyfin to scan all its libraries. Returns the error, or null when it started.</summary>
    public static async Task<string?> RefreshJellyfinAsync(NotificationOptions options, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{options.JellyfinUrl.Trim().TrimEnd('/')}/Library/Refresh");
            request.Headers.TryAddWithoutValidation("Authorization", $"MediaBrowser Token=\"{options.JellyfinApiKey.Trim()}\"");
            using var response = await Http.SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode
                ? null
                : response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                    ? "Jellyfin refused the API key."
                    : $"Jellyfin answered {(int)response.StatusCode} {response.ReasonPhrase}.";
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or UriFormatException or InvalidOperationException)
        {
            return $"Could not reach Jellyfin: {e.Message}";
        }
    }

    /// <summary>The text as is when it is ASCII, otherwise as an RFC 2047 UTF-8 encoded word.</summary>
    public static string HeaderValue(string text) =>
        text.All(c => c is >= ' ' and <= '~') ? text : $"=?UTF-8?B?{Convert.ToBase64String(Encoding.UTF8.GetBytes(text))}?=";
}
