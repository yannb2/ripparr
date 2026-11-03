using System.Net.Sockets;
using System.Text;
using Ripparr.Disc;
using Ripparr.Jobs;

namespace Ripparr.Tests;

[TestClass]
public class IsoMountTests
{
    /// <summary>A pretend mount service: answers each JSON line with what the test gives it.</summary>
    private static async Task<(string Socket, Task<string> Request)> FakeService(string answer)
    {
        var socket = Path.Combine(Path.GetTempPath(), $"ripparr-test-{Guid.NewGuid():N}.sock");
        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(socket));
        listener.Listen(1);
        var request = Task.Run(async () =>
        {
            using var _ = listener;
            using var client = await listener.AcceptAsync();
            await using var stream = new NetworkStream(client);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var line = await reader.ReadLineAsync() ?? "";
            await stream.WriteAsync(Encoding.UTF8.GetBytes(answer + "\n"));
            return line;
        });
        await Task.Yield();
        return (socket, request);
    }

    [TestMethod]
    public async Task MountsThroughTheSocket()
    {
        var (socket, request) = await FakeService("""{"ok": true, "path": "/mnt/pc/iso-mounts/Film-1a2b3c4d"}""");
        try
        {
            var reply = await IsoMount.MountAsync(socket, "/downloads/Film.iso", CancellationToken.None);
            Assert.IsTrue(reply.Ok);
            Assert.AreEqual("/mnt/pc/iso-mounts/Film-1a2b3c4d", reply.Path);
            Assert.AreEqual("""{"op":"mount","iso":"/downloads/Film.iso"}""", await request);
        }
        finally
        {
            File.Delete(socket);
        }
    }

    [TestMethod]
    public async Task PassesTheServiceErrorOn()
    {
        var (socket, _) = await FakeService("""{"ok": false, "error": "mount failed: wrong fs type"}""");
        try
        {
            var reply = await IsoMount.MountAsync(socket, "/downloads/Film.iso", CancellationToken.None);
            Assert.IsFalse(reply.Ok);
            Assert.AreEqual("mount failed: wrong fs type", reply.Error);
        }
        finally
        {
            File.Delete(socket);
        }
    }

    [TestMethod]
    public async Task NoServiceSaysSo()
    {
        var reply = await IsoMount.PingAsync("/nowhere/mount.sock", CancellationToken.None);
        Assert.IsFalse(reply.Ok);
        StringAssert.Contains(reply.Error, "not running");
    }

    [TestMethod]
    public void ParsesAnswers()
    {
        Assert.AreEqual(new MountReply(true, Version: "1"), IsoMount.Parse("""{"ok": true, "version": "1", "root": "/x"}"""));
        Assert.IsFalse(IsoMount.Parse("not json").Ok);
        Assert.IsFalse(IsoMount.Parse("[1]").Ok);
    }

    [TestMethod]
    public void PlainIsosAreMountedWhenTheServiceIsOn()
    {
        var iso = new DiscSource("/downloads/Film.iso", SourceKind.Iso, "/downloads", 1, DateTime.UtcNow);
        var folder = new DiscSource("/downloads/Film", SourceKind.Folder, "/downloads", 1, DateTime.UtcNow);
        var plain = new DiscAnalysis { Source = iso, Encryption = Encryption.None };
        var encrypted = new DiscAnalysis { Source = iso, Encryption = Encryption.Encrypted };

        Assert.AreEqual(JobPipeline.Prepare.Mount, JobPipeline.PrepareMode(iso, plain, mount: true));
        Assert.AreEqual(JobPipeline.Prepare.Unpack, JobPipeline.PrepareMode(iso, plain, mount: false));
        Assert.AreEqual(JobPipeline.Prepare.Decrypt, JobPipeline.PrepareMode(iso, encrypted, mount: true));
        Assert.AreEqual(JobPipeline.Prepare.InPlace, JobPipeline.PrepareMode(folder, plain, mount: true));
    }

    [TestMethod]
    public void WholeJobAddsUpTheSteps()
    {
        var weights = JobProgress.Weights(JobPipeline.Prepare.Mount, mergeDolbyVision: false, srtTracks: 0);
        var total = weights.Values.Sum();

        Assert.IsNull(JobProgress.Overall(new Dictionary<string, double>(), JobSteps.Demux, 0.5));
        Assert.IsNull(JobProgress.Overall(weights, "Dry run", 0.5));
        Assert.AreEqual(0, JobProgress.Overall(weights, JobSteps.Check, null));
        // Check and Prepare done, half the demux.
        Assert.AreEqual((1 + 0.5 + 15) / total, JobProgress.Overall(weights, JobSteps.Demux, 0.5)!.Value, 1e-9);
        Assert.AreEqual((total - 1) / total, JobProgress.Overall(weights, JobSteps.Finish, 0)!.Value, 1e-9);

        // Unpacking and the Dolby Vision merge make the other steps a smaller share.
        var unpack = JobProgress.Weights(JobPipeline.Prepare.Unpack, mergeDolbyVision: true, srtTracks: 2);
        Assert.IsTrue(JobProgress.Overall(unpack, JobSteps.Demux, 0) > JobProgress.Overall(weights, JobSteps.Demux, 0));
    }

    [TestMethod]
    public void TimeLeftFollowsThePace()
    {
        var now = new DateTime(2026, 11, 3, 20, 0, 0, DateTimeKind.Utc);
        Assert.AreEqual(TimeSpan.FromMinutes(3), JobProgress.TimeLeft(now.AddMinutes(-1), 0.25, now));
        Assert.IsNull(JobProgress.TimeLeft(now.AddSeconds(-5), 0.5, now), "too early to tell");
        Assert.IsNull(JobProgress.TimeLeft(now.AddMinutes(-1), null, now));
        Assert.IsNull(JobProgress.TimeLeft(now.AddMinutes(-1), 0.001, now));
    }

    [TestMethod]
    public void MapsPathsBetweenContainerAndHost()
    {
        var settings = new Ripparr.Options.IsoMountOptions
        {
            PathMap = [new() { Container = "/usb", Host = "/mnt/usb" }, new() { Container = "/iso", Host = "/mnt/pc/iso-mounts/" }]
        };
        Assert.AreEqual("/mnt/usb/disk1/Film.iso", settings.ToHost("/usb/disk1/Film.iso"));
        Assert.AreEqual("/downloads/Film.iso", settings.ToHost("/downloads/Film.iso"));
        Assert.AreEqual("/usbstick/Film.iso", settings.ToHost("/usbstick/Film.iso"), "only whole folder names");
        Assert.AreEqual("/iso/Film-1a2b3c4d", settings.ToContainer("/mnt/pc/iso-mounts/Film-1a2b3c4d"));
    }
}
