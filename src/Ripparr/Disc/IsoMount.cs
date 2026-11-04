using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ripparr.Disc;

/// <summary>An answer from the mount service: Path when a mount worked, Error when something did not.</summary>
public sealed record MountReply(bool Ok, string? Path = null, string? Error = null, string? Version = null);

/// <summary>
/// Talks to the ISO mount service on the host (host/ripparr_mount.py) over its unix socket:
/// one JSON line out, one JSON line back. The container can't mount anything itself.
/// </summary>
public static class IsoMount
{
    public static Task<MountReply> PingAsync(string socket, CancellationToken cancellationToken) =>
        AskAsync(socket, new JsonObject { ["op"] = "ping" }, TimeSpan.FromSeconds(3), cancellationToken);

    public static Task<MountReply> MountAsync(string socket, string iso, CancellationToken cancellationToken) =>
        AskAsync(socket, new JsonObject { ["op"] = "mount", ["iso"] = iso }, TimeSpan.FromMinutes(2), cancellationToken);

    public static Task<MountReply> UnmountAsync(string socket, string path, CancellationToken cancellationToken) =>
        AskAsync(socket, new JsonObject { ["op"] = "unmount", ["path"] = path }, TimeSpan.FromMinutes(1), cancellationToken);

    /// <summary>
    /// Reads an ISO through the mount service for a short look (pictures, a quick subtitle check):
    /// uses a running job's mount of the same ISO, else mounts it and unmounts it after, unless a
    /// job started on it meanwhile. Null when the service is off or could not mount it.
    /// </summary>
    /// <param name="jobMount">The host path a running job mounted this ISO at, or null.</param>
    /// <param name="jobUsesIt">Whether a job is running on this ISO now.</param>
    /// <param name="work">Gets the disc folder (the one holding BDMV) as the container sees it.</param>
    public static async Task<T?> BorrowAsync<T>(Options.IsoMountOptions settings, string iso, string? jobMount, Func<bool> jobUsesIt,
        Func<string, Task<T>> work) where T : class
    {
        if (!settings.Enabled)
        {
            return null;
        }

        var hostPath = jobMount;
        var ours = false;
        if (hostPath == null)
        {
            var reply = await MountAsync(settings.Socket, settings.ToHost(iso), CancellationToken.None);
            if (reply is not { Ok: true, Path: { } path })
            {
                return null;
            }

            hostPath = path;
            ours = true;
        }

        try
        {
            var disc = SourceFinder.BdmvParent(settings.ToContainer(hostPath));
            return disc == null ? null : await work(disc);
        }
        finally
        {
            if (ours && !jobUsesIt())
            {
                await UnmountAsync(settings.Socket, hostPath, CancellationToken.None);
            }
        }
    }

    public static async Task<MountReply> AskAsync(string socket, JsonObject request, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!File.Exists(socket))
        {
            return new MountReply(false, Error: $"The mount service is not running (no {socket}).");
        }

        using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timer.CancelAfter(timeout);
        try
        {
            using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await client.ConnectAsync(new UnixDomainSocketEndPoint(socket), timer.Token);
            await using var stream = new NetworkStream(client, ownsSocket: false);
            await stream.WriteAsync(Encoding.UTF8.GetBytes(request.ToJsonString() + "\n"), timer.Token);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var line = await reader.ReadLineAsync(timer.Token);
            return line == null ? new MountReply(false, Error: "The mount service closed the connection.") : Parse(line);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new MountReply(false, Error: $"The mount service did not answer within {timeout.TotalSeconds:0} s.");
        }
        catch (SocketException e)
        {
            return new MountReply(false, Error: e.SocketErrorCode == SocketError.AccessDenied
                ? $"Not allowed to use {socket}: the socket must belong to the container's group (the service's --group)."
                : $"Could not reach the mount service: {e.Message}");
        }
        catch (IOException e)
        {
            return new MountReply(false, Error: $"Could not talk to the mount service: {e.Message}");
        }
    }

    public static MountReply Parse(string line)
    {
        try
        {
            var node = JsonNode.Parse(line) as JsonObject;
            if (node == null)
            {
                return new MountReply(false, Error: "The mount service sent something that is not JSON.");
            }

            return new MountReply(
                node["ok"]?.GetValue<bool>() == true,
                node["path"]?.GetValue<string>(),
                node["error"]?.GetValue<string>(),
                node["version"]?.GetValue<string>());
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            return new MountReply(false, Error: "The mount service sent something that is not JSON.");
        }
    }
}
