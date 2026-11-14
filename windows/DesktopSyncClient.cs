using System.Net.WebSockets;
using System.Text.Json;

namespace Nearlink.Native;

/// <summary>Owns the desktop WebSocket session, event decoding, and bounded reconnect policy.</summary>
public sealed class DesktopSyncClient : IAsyncDisposable
{
    private const int MaxEventBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private Uri _endpoint;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private Task? _runTask;
    private ClientWebSocket? _activeSocket;

    public event Action<TransferHost.ServerEvent>? EventReceived;
    public event Action<string>? StatusChanged;

    public DesktopSyncClient(string baseUrl, string code, string sessionId, string deviceName)
    {
        var query = $"code={Uri.EscapeDataString(code)}&session={Uri.EscapeDataString(sessionId)}&name={Uri.EscapeDataString(deviceName)}&kind=desktop";
        var builder = new UriBuilder(baseUrl)
        {
            Scheme = new Uri(baseUrl).Scheme == "https" ? "wss" : "ws",
            Path = "/ws",
            Query = query
        };
        _endpoint = builder.Uri;
    }

    public void Start()
    {
        if (_runTask is not null) throw new InvalidOperationException("Desktop sync has already started");
        _runTask = RunAsync(_stop.Token);
    }

    public async Task UpdateDeviceNameAsync(string name)
    {
        var endpoint = new UriBuilder(_endpoint);
        var query = endpoint.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(part => !part.StartsWith("name=", StringComparison.Ordinal))
            .Append($"name={Uri.EscapeDataString(name)}");
        endpoint.Query = string.Join("&", query);
        _endpoint = endpoint.Uri;
        var socket = _activeSocket;
        if (socket?.State != WebSocketState.Open) return;
        await _sendLock.WaitAsync(_stop.Token);
        try
        {
            if (socket.State != WebSocketState.Open) return;
            var data = JsonSerializer.SerializeToUtf8Bytes(new { type = "rename", name });
            await socket.SendAsync(data, WebSocketMessageType.Text, true, _stop.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException) { }
        finally { _sendLock.Release(); }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var retry = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            using var socket = new ClientWebSocket();
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            try
            {
                await socket.ConnectAsync(_endpoint, cancellationToken).ConfigureAwait(false);
                _activeSocket = socket;
                retry = 0;
                StatusChanged?.Invoke("已连接 · 设备间实时同步");
                await ReceiveLoopAsync(socket, cancellationToken).ConfigureAwait(false);
                if (!cancellationToken.IsCancellationRequested) StatusChanged?.Invoke("连接已关闭，正在重连…");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is WebSocketException or IOException or InvalidDataException or JsonException or ObjectDisposedException)
            {
                StatusChanged?.Invoke("连接中断，正在重连…");
            }
            catch
            {
                StatusChanged?.Invoke("连接异常，正在重连…");
            }
            finally
            {
                Interlocked.CompareExchange(ref _activeSocket, null, socket);
            }

            if (cancellationToken.IsCancellationRequested) break;
            retry = Math.Min(retry + 1, 6);
            var delay = Math.Min(30_000, 750 * (1 << retry)) + Random.Shared.Next(0, 750);
            try { await Task.Delay(delay, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            using var data = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) return;
                if (data.Length + result.Count > MaxEventBytes) throw new InvalidDataException("Server event exceeds the allowed size");
                data.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            if (result.MessageType != WebSocketMessageType.Text) continue;
            var message = JsonSerializer.Deserialize<TransferHost.ServerEvent>(data.ToArray(), JsonOptions);
            if (message is not null) EventReceived?.Invoke(message);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_runTask is not null)
        {
            try { await _runTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        _activeSocket = null;
        _stop.Dispose();
        _sendLock.Dispose();
    }
}
