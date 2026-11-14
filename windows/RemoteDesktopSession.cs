using System.Net.Http.Json;
using System.Text.Json;

namespace Nearlink.Native;

/// <summary>Connects to an approved Nearlink computer and transfers directly over the LAN.</summary>
public sealed class RemoteDesktopSession : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;
    private readonly string _token;
    private readonly string _deviceId;
    private string _name;
    private readonly DesktopSyncClient _sync;

    public string DeviceId { get; }
    public string Name { get; }
    public string BaseUrl { get; }
    public event Action<TransferHost.ServerEvent>? EventReceived;
    public event Action<string>? StatusChanged;

    public RemoteDesktopSession(string deviceId, string name, string baseUrl, string token, string localDeviceId, string localName)
    {
        DeviceId = deviceId;
        Name = name;
        BaseUrl = baseUrl.TrimEnd('/');
        _token = token;
        _deviceId = localDeviceId;
        _name = localName;
        _http = new HttpClient { BaseAddress = new Uri(BaseUrl), Timeout = Timeout.InfiniteTimeSpan };
        _sync = new DesktopSyncClient(BaseUrl, token, localDeviceId, localName);
        _sync.EventReceived += message => EventReceived?.Invoke(message);
        _sync.StatusChanged += status => StatusChanged?.Invoke(status);
    }

    public void Start() => _sync.Start();

    public async Task UpdateLocalDeviceNameAsync(string name)
    {
        _name = name;
        await _sync.UpdateDeviceNameAsync(name);
    }

    public async Task<IReadOnlyList<TransferStore.Entry>> GetHistoryAsync(CancellationToken cancellationToken = default)
    {
        var url = $"/api/history?code={Uri.EscapeDataString(_token)}";
        return await _http.GetFromJsonAsync<List<TransferStore.Entry>>(url, JsonOptions, cancellationToken) ?? [];
    }

    public async Task SendMessageAsync(string text, CancellationToken cancellationToken = default)
    {
        var url = $"/api/message?code={Uri.EscapeDataString(_token)}&from={Uri.EscapeDataString(_name)}&session={Uri.EscapeDataString(_deviceId)}";
        using var response = await _http.PostAsJsonAsync(url, new MessageRequest(text), JsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task UploadFileAsync(string path, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var url = $"/api/upload?code={Uri.EscapeDataString(_token)}&from={Uri.EscapeDataString(_name)}&session={Uri.EscapeDataString(_deviceId)}";
        await ReliableTransfer.UploadFileAsync(_http, path, url, progress, cancellationToken);
    }

    public Uri GetPreviewUri(string id) => new($"{BaseUrl}/api/preview/{Uri.EscapeDataString(id)}?code={Uri.EscapeDataString(_token)}");

    public async Task DownloadFileAsync(string id, string name, string destination, CancellationToken cancellationToken = default)
    {
        var url = $"/api/file/{Uri.EscapeDataString(id)}?code={Uri.EscapeDataString(_token)}&name={Uri.EscapeDataString(name)}";
        await ReliableTransfer.DownloadFileAsync(_http, url, destination, cancellationToken: cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _sync.DisposeAsync();
        _http.Dispose();
    }

    private sealed record MessageRequest(string Text);

}
