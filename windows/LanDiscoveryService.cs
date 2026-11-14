using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Nearlink.Native;

/// <summary>Advertises Nearlink computers on the local multicast domain without exposing pairing secrets.</summary>
public sealed class LanDiscoveryService : IAsyncDisposable
{
    private const int Port = 51462;
    private static readonly IPAddress Group = IPAddress.Parse("239.255.42.99");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _deviceId;
    private string _name;
    private readonly Func<string> _baseUrl;
    private readonly ConcurrentDictionary<string, NearbyComputer> _nearby = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stop = new();
    private UdpClient? _udp;
    private Task? _receiveTask;
    private Task? _announceTask;
    private string _lastPublishedSnapshot = "";

    public event Action<IReadOnlyList<NearbyComputer>>? DevicesChanged;

    public LanDiscoveryService(string deviceId, string name, Func<string> baseUrl)
    {
        _deviceId = deviceId;
        _name = name;
        _baseUrl = baseUrl;
    }

    public void Start()
    {
        if (_udp is not null) return;
        var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Client.ExclusiveAddressUse = false;
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, Port));
        udp.JoinMulticastGroup(Group);
        udp.MulticastLoopback = true;
        udp.Ttl = 1;
        _udp = udp;
        DevicesChanged?.Invoke(Array.Empty<NearbyComputer>());
        _receiveTask = ReceiveLoopAsync(_stop.Token);
        _announceTask = AnnounceLoopAsync(_stop.Token);
    }

    public void SetDeviceName(string name)
    {
        _name = name;
        _lastPublishedSnapshot = "";
    }

    private async Task AnnounceLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var url = _baseUrl();
                if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Host != "127.0.0.1")
                {
                    var payload = JsonSerializer.SerializeToUtf8Bytes(new Announcement("nearlink", _deviceId, _name, url), JsonOptions);
                    await _udp!.SendAsync(payload, new IPEndPoint(Group, Port), cancellationToken);
                }
                PruneExpired();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { }
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ContinueWith(_ => { }, TaskScheduler.Default);
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var packet = await _udp!.ReceiveAsync(cancellationToken);
                var announcement = JsonSerializer.Deserialize<Announcement>(packet.Buffer, JsonOptions);
                if (announcement?.Service != "nearlink" || string.IsNullOrWhiteSpace(announcement.DeviceId) || announcement.DeviceId == _deviceId) continue;
                if (string.IsNullOrWhiteSpace(announcement.Name) || !Uri.TryCreate(announcement.BaseUrl, UriKind.Absolute, out var uri) || uri.Scheme != "http" || uri.Port is < 1 or > 65535) continue;
                var address = packet.RemoteEndPoint.Address;
                var host = uri.Host;
                if (IPAddress.TryParse(host, out var advertisedIp) && IPAddress.IsLoopback(advertisedIp)) continue;
                if (IPAddress.TryParse(host, out _) && IPAddress.IsLoopback(address)) continue;
                _nearby[announcement.DeviceId] = new NearbyComputer(announcement.DeviceId, announcement.Name, announcement.BaseUrl, DateTimeOffset.UtcNow);
                PublishIfChanged();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (SocketException) { await Task.Delay(250, cancellationToken).ContinueWith(_ => { }, TaskScheduler.Default); }
            catch (JsonException) { }
        }
    }

    private void PruneExpired()
    {
        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(8);
        foreach (var (id, device) in _nearby)
            if (device.LastSeen < cutoff) _nearby.TryRemove(id, out _);
        PublishIfChanged();
    }

    private void PublishIfChanged()
    {
        var snapshot = _nearby.Values.OrderBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        var signature = string.Join("|", snapshot.Select(device => $"{device.DeviceId}:{device.Name}:{device.BaseUrl}"));
        if (signature == _lastPublishedSnapshot) return;
        _lastPublishedSnapshot = signature;
        DevicesChanged?.Invoke(snapshot);
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _udp?.Dispose();
        var tasks = new[] { _receiveTask, _announceTask }.Where(task => task is not null).Cast<Task>().ToArray();
        try { await Task.WhenAll(tasks); } catch (OperationCanceledException) { }
        _stop.Dispose();
    }

    private sealed record Announcement(string Service, string DeviceId, string Name, string BaseUrl);
    public sealed record NearbyComputer(string DeviceId, string Name, string BaseUrl, DateTimeOffset LastSeen);
}
