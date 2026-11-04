using System.Text.Json;
using Microsoft.Extensions.Options;
using Ripparr.Jobs;

namespace Ripparr.Options;

/// <summary>
/// The settings changed on the Settings page: Radarr, Sonarr and notifications. They are kept in
/// the database and win over the environment variables (Ripparr__Radarr__Url...), which stay as
/// the starting values until the page saves something.
/// </summary>
public sealed class LiveSettings(IOptions<RipparrOptions> options, JobStore store)
{
    public const string RadarrKey = "integrations.radarr";
    public const string SonarrKey = "integrations.sonarr";
    public const string NotificationsKey = "notifications";

    public ArrOptions Radarr => Read(RadarrKey, options.Value.Radarr);

    public ArrOptions Sonarr => Read(SonarrKey, options.Value.Sonarr);

    public NotificationOptions Notifications => Read(NotificationsKey, options.Value.Notifications);

    /// <summary>True when the page saved this setting; false when it still comes from the environment.</summary>
    public bool Saved(string key) => store.Setting(key) != null;

    public void Save<T>(string key, T value) => store.SetSetting(key, JsonSerializer.Serialize(value, JobStore.Json));

    /// <summary>Forgets what the page saved: the environment variables apply again.</summary>
    public void Reset(string key) => store.SetSetting(key, null);

    private T Read<T>(string key, T fallback) where T : class
    {
        var json = store.Setting(key);
        if (json == null)
        {
            return fallback;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, JobStore.Json) ?? fallback;
        }
        catch (JsonException)
        {
            return fallback;
        }
    }
}
