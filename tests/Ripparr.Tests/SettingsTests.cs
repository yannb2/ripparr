using System.Net;
using System.Text;
using Ripparr.Arr;
using Ripparr.Jobs;
using Ripparr.Options;

namespace Ripparr.Tests;

/// <summary>The Settings page: saved values over environment variables, and what notifications send.</summary>
[TestClass]
public class SettingsTests
{
    private string _folder = null!;

    [TestInitialize]
    public void Setup()
    {
        _folder = Path.Combine(Path.GetTempPath(), "ripparr-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_folder, true);

    private LiveSettings Live(RipparrOptions options) =>
        new(Microsoft.Extensions.Options.Options.Create(options), new JobStore(Path.Combine(_folder, "ripparr.db")));

    [TestMethod]
    public void SavedSettingsWinOverTheEnvironmentUntilReset()
    {
        var options = new RipparrOptions { Radarr = new ArrOptions { Url = "http://env:7878", ApiKey = "env" } };
        var live = Live(options);
        Assert.AreEqual("http://env:7878", live.Radarr.Url);
        Assert.IsFalse(live.Saved(LiveSettings.RadarrKey));
        Assert.IsFalse(live.Sonarr.Configured);

        live.Save(LiveSettings.RadarrKey, new ArrOptions { Url = "http://radarr:7878", ApiKey = "saved" });
        live.Save(LiveSettings.NotificationsKey, new NotificationOptions { NtfyUrl = "https://ntfy.sh/x", OnDone = false });

        Assert.AreEqual("saved", live.Radarr.ApiKey);
        Assert.IsTrue(live.Saved(LiveSettings.RadarrKey));
        Assert.IsFalse(live.Notifications.OnDone);
        Assert.IsTrue(live.Notifications.OnFailed);

        live.Reset(LiveSettings.RadarrKey);
        Assert.AreEqual("env", live.Radarr.ApiKey);
    }

    [TestMethod]
    public async Task TheClientsUseWhatWasJustSaved()
    {
        var live = Live(new RipparrOptions());
        var radarr = new RadarrClient(live, Microsoft.Extensions.Logging.Abstractions.NullLogger<RadarrClient>.Instance);
        Assert.IsFalse(radarr.Configured);
        var (_, error) = await radarr.SearchAsync("Hidden", CancellationToken.None);
        StringAssert.Contains(error, "Settings > Integrations");

        live.Save(LiveSettings.RadarrKey, new ArrOptions { Url = "http://radarr:7878", ApiKey = "k" });
        Assert.IsTrue(radarr.Configured, "no restart needed");
    }

    private static Job Ended(JobStatus status, bool dryRun = false) => new()
    {
        Title = "Hidden", Year = 2022, Status = status, DryRun = dryRun, Playlist = "00800", SourceName = "HIDDEN",
        OutputPath = "/done/Hidden (2022)/Hidden (2022) [Remux-2160p].mkv", Error = status == JobStatus.Failed ? "DGDemux stopped." : null
    };

    [TestMethod]
    public void MessagesFollowTheSwitches()
    {
        var options = new NotificationOptions { NtfyUrl = "https://ntfy.sh/x" };

        var done = Notifier.MessageFor(Ended(JobStatus.Done), options)!;
        StringAssert.Contains(done.Title, "Hidden (2022) is done");
        Assert.AreEqual("Hidden (2022) [Remux-2160p].mkv", done.Body);

        var failed = Notifier.MessageFor(Ended(JobStatus.Failed, dryRun: true), options)!;
        StringAssert.Contains(failed.Title, "failed (dry run)");
        Assert.AreEqual("DGDemux stopped.", failed.Body);
        Assert.AreEqual("high", failed.Priority);

        Assert.IsNull(Notifier.MessageFor(Ended(JobStatus.Done, dryRun: true), options), "a good dry run is quiet by default");
        Assert.IsNull(Notifier.MessageFor(Ended(JobStatus.Cancelled), options));
        Assert.IsNull(Notifier.MessageFor(Ended(JobStatus.Done), new NotificationOptions()), "no topic, no message");
        Assert.IsNull(Notifier.MessageFor(Ended(JobStatus.Failed), new NotificationOptions { NtfyUrl = "https://ntfy.sh/x", OnFailed = false }));
        Assert.IsNotNull(Notifier.MessageFor(Ended(JobStatus.Done, dryRun: true), new NotificationOptions { NtfyUrl = "https://ntfy.sh/x", DryRuns = true }));
    }

    [TestMethod]
    public void HeadersStayAscii()
    {
        Assert.AreEqual("Ripparr: Hidden", Notifier.HeaderValue("Ripparr: Hidden"));
        var encoded = Notifier.HeaderValue("Ripparr: Amélie");
        StringAssert.StartsWith(encoded, "=?UTF-8?B?");
        Assert.AreEqual("Ripparr: Amélie", Encoding.UTF8.GetString(Convert.FromBase64String(encoded[10..^2])));
        Assert.AreEqual("6.0.4.10291", ArrCheck.Version("""{"appName":"Radarr","version":"6.0.4.10291"}"""));
        Assert.AreEqual("", ArrCheck.Version("not json"));
    }

    [TestMethod]
    public async Task SendsToNtfyAndJellyfin()
    {
        var port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var options = new NotificationOptions
        {
            NtfyUrl = $"http://127.0.0.1:{port}/ripparr", NtfyToken = "tk_abc",
            JellyfinUrl = $"http://127.0.0.1:{port}/", JellyfinApiKey = "jf"
        };

        var received = Receive(listener);
        var error = await Notifier.SendAsync(options, new JobMessage("Ripparr: Amélie is done", "file.mkv", "white_check_mark", "default"), CancellationToken.None);
        var ntfy = await received;
        Assert.IsNull(error);
        Assert.AreEqual("/ripparr", ntfy.Path);
        Assert.AreEqual("file.mkv", ntfy.Body);
        Assert.AreEqual("Bearer tk_abc", ntfy.Headers["Authorization"]);
        StringAssert.StartsWith(ntfy.Headers["Title"], "=?UTF-8?B?");
        Assert.AreEqual("white_check_mark", ntfy.Headers["Tags"]);

        received = Receive(listener);
        Assert.IsNull(await Notifier.RefreshJellyfinAsync(options, CancellationToken.None));
        var jellyfin = await received;
        Assert.AreEqual("/Library/Refresh", jellyfin.Path);
        Assert.AreEqual("MediaBrowser Token=\"jf\"", jellyfin.Headers["Authorization"]);

        received = Receive(listener, HttpStatusCode.Unauthorized);
        Assert.AreEqual("Jellyfin refused the API key.", await Notifier.RefreshJellyfinAsync(options, CancellationToken.None));
        await received;
    }

    private static async Task<(string Path, string Body, System.Collections.Specialized.NameValueCollection Headers)> Receive(
        HttpListener listener, HttpStatusCode status = HttpStatusCode.OK)
    {
        var context = await listener.GetContextAsync();
        using var reader = new StreamReader(context.Request.InputStream);
        var body = await reader.ReadToEndAsync();
        var result = (context.Request.Url!.AbsolutePath, body, context.Request.Headers);
        context.Response.StatusCode = (int)status;
        context.Response.Close();
        return result;
    }

    private static int FreePort()
    {
        var socket = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        var port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        return port;
    }
}
