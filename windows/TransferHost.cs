using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using QRCoder;

namespace Nearlink.Native;

public sealed class TransferHost : IAsyncDisposable
{
    private const long MaxFileSize = 1024L * 1024 * 1024;
    private readonly SemaphoreSlim _uploadSlots = new(3, 3);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<WebSocket, Peer> _peers = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _trustedPeerTokens = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<TransferStore>> _peerStores = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<TransferStore>> _phoneStores = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _phoneRoomSessions = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _pairRequestTimes = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TransferGrant> _transferGrants = new(StringComparer.Ordinal);
    private readonly object _trustedPeerLock = new();
    private readonly string _trustedPeerPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nearlink", "trusted-peers.json");
    private readonly string _deviceNamePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nearlink", "device-name.txt");
    private readonly string _code = LoadOrCreatePairCode();
    private readonly string _sessionId = LoadOrCreateDeviceId();
    private readonly TransferStore _store = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nearlink", "transfers"));
    private WebApplication? _app;
    private HttpClient? _http;

    public string Code => _code;
    public string DeviceId => _sessionId;
    public string DeviceName { get; private set; } = LoadDeviceName();
    public string BaseUrl { get; private set; } = "";
    public string LocalBaseUrl { get; private set; } = "";
    public string PairUrl => $"{BaseUrl}/?code={_code}&deviceId={Uri.EscapeDataString(_sessionId)}";
    public string DesktopSessionId => _sessionId;
    public Func<PairRequest, CancellationToken, Task<bool>>? PairApprovalRequested { get; set; }
    public Func<IncomingTransferRequest, CancellationToken, Task<bool>>? IncomingTransferApprovalRequested { get; set; }
    public event Action<string, ServerEvent>? PeerSessionEvent;
    public event Action<string, ServerEvent>? PhoneSessionEvent;
    public event Action<IReadOnlyList<DeviceInfo>>? ConnectedDevicesChanged;
    public string GetStoredFilePath(string id) => _store.GetFilePath(id);
    public IReadOnlyList<TransferStore.Entry> GetHistory() => _store.GetHistory();
    public IReadOnlyList<TransferStore.Entry> GetPeerHistory(string peerDeviceId) => GetPeerStore(peerDeviceId).GetHistory();
    public IReadOnlyList<TransferStore.Entry> GetAllPhoneHistory() => EnumeratePhoneSessions().SelectMany(sessionId => GetPhoneStore(sessionId).GetHistory()).ToArray();
    public string GetPeerStoredFilePath(string peerDeviceId, string id) => GetPeerStore(peerDeviceId).GetFilePath(id);
    public string GetPhoneStoredFilePath(string sessionId, string id) => GetPhoneStore(sessionId).GetFilePath(id);

    public async Task DownloadPeerFileAsync(string peerDeviceId, string id, string destination, CancellationToken cancellationToken = default)
        => await CopyStoredFileAsync(GetPeerStore(peerDeviceId), id, destination, cancellationToken);

    public async Task DownloadPhoneFileAsync(string sessionId, string id, string destination, CancellationToken cancellationToken = default)
        => await CopyStoredFileAsync(GetPhoneStore(sessionId), id, destination, cancellationToken);

    private static async Task CopyStoredFileAsync(TransferStore store, string id, string destination, CancellationToken cancellationToken)
    {
        if (!store.TryGetFile(id, out _)) throw new FileNotFoundException("此电脑会话中找不到该文件");
        var temp = destination + $".nearlink-{Guid.NewGuid():N}.tmp";
        try
        {
            await using var source = new FileStream(store.GetFilePath(id), FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using (var target = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await source.CopyToAsync(target, cancellationToken);
                await target.FlushAsync(cancellationToken);
                target.Flush(flushToDisk: true);
            }
            File.Move(temp, destination, overwrite: true);
        }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _store.Initialize();
        LoadTrustedPeers();

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
            EnvironmentName = "Production"
        });
        builder.Logging.ClearProviders();
        builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = MaxFileSize + 1024 * 1024);
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxRequestBodySize = MaxFileSize + 1024 * 1024;
            options.ListenAnyIP(51461);
        });

        var app = builder.Build();
        app.UseWebSockets();
        MapEmbeddedAsset(app, "/", "index.html", "text/html; charset=utf-8");
        MapEmbeddedAsset(app, "/index.html", "index.html", "text/html; charset=utf-8");
        MapEmbeddedAsset(app, "/app.js", "app.js", "text/javascript; charset=utf-8");
        MapEmbeddedAsset(app, "/style.css", "style.css", "text/css; charset=utf-8");

        app.MapGet("/api/pair", (HttpRequest request) =>
        {
            if (!AuthorizedMaster(request)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(PairUrl, QRCodeGenerator.ECCLevel.Q);
            var qr = "data:image/png;base64," + Convert.ToBase64String(new PngByteQRCode(data).GetGraphic(7));
            return Results.Json(new { code = _code, url = PairUrl, qr });
        });
        app.MapGet("/api/target", (HttpRequest request) => AuthorizedMaster(request)
            ? Results.Json(new { name = DeviceName, deviceId = _sessionId }, JsonOptions)
            : Results.StatusCode(StatusCodes.Status403Forbidden));
        app.MapPost("/api/pair/request", async (HttpRequest request, PairRequestBody body) =>
        {
            var deviceId = CleanName(body.DeviceId);
            var name = CleanName(body.Name);
            var address = GetRemoteAddress(request);
            if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(name) || deviceId == DeviceId)
                return Results.BadRequest(new { error = "设备信息无效" });
            if (body.Token is not null && !IsValidPeerToken(body.Token)) return Results.BadRequest(new { error = "配对凭据无效" });
            var remoteBaseUrl = ValidatePeerBaseUrl(body.BaseUrl, request.HttpContext.Connection.RemoteIpAddress);
            if (body.BaseUrl is not null && remoteBaseUrl is null) return Results.BadRequest(new { error = "设备地址无效" });
            var now = DateTimeOffset.UtcNow;
            if (_pairRequestTimes.TryGetValue(address, out var previous) && now - previous < TimeSpan.FromSeconds(8))
                return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            _pairRequestTimes[address] = now;
            var approve = PairApprovalRequested;
            if (approve is null || !await approve(new PairRequest(deviceId, name, address, remoteBaseUrl, body.Token), request.HttpContext.RequestAborted))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (IsValidPeerToken(body.Token)) RegisterPeerToken(deviceId, body.Token!);
            var token = GetOrCreatePeerToken(deviceId);
            return Results.Json(new PairResponse(DeviceId, DeviceName, token), JsonOptions);
        });
        app.MapGet("/api/history", (HttpRequest request) =>
        {
            var access = ResolveAccess(request);
            if (access is null) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var history = access.PhoneSessionId is not null
                ? GetPhoneVisibleHistory(access.PhoneSessionId)
                : access.Store.GetHistory();
            return Results.Json(history, JsonOptions);
        });
        app.MapGet("/api/file/{id}", (string id, HttpRequest request) =>
        {
            var access = ResolveAccess(request);
            if (access is null) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var store = GetFileStore(access, id, out var item);
            if (store is null) return Results.NotFound();
            var path = store.GetFilePath(id);
            var name = request.Query["name"].ToString();
            if (!string.IsNullOrWhiteSpace(item!.Sha256)) request.HttpContext.Response.Headers["X-Nearlink-SHA256"] = item.Sha256;
            return Results.File(path, item.ContentType ?? "application/octet-stream", string.IsNullOrWhiteSpace(name) ? item.Name ?? id : Path.GetFileName(name));
        });
        app.MapGet("/api/preview/{id}", (string id, HttpRequest request) =>
        {
            var access = ResolveAccess(request);
            if (access is null) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var store = GetFileStore(access, id, out var item);
            if (store is null) return Results.NotFound();
            var path = store.GetFilePath(id);
            var contentType = item!.ContentType ?? "application/octet-stream";
            if (string.IsNullOrWhiteSpace(contentType)) contentType = "application/octet-stream";
            if (!contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
            return Results.File(path, contentType, enableRangeProcessing: true);
        });
        app.MapPost("/api/message", async (HttpRequest request) =>
        {
            var access = ResolveAccess(request);
            if (access is null) return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (request.ContentLength > 64 * 1024) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            if (request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
                limit.MaxRequestBodySize = 64 * 1024;
            MessageRequest? body;
            try { body = await JsonSerializer.DeserializeAsync<MessageRequest>(request.Body, JsonOptions, request.HttpContext.RequestAborted); }
            catch (JsonException) { return Results.BadRequest(new { error = "消息格式无效" }); }
            var text = body?.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text)) return Results.BadRequest(new { error = "消息不能为空" });
            if (text.Length > 4000) return Results.BadRequest(new { error = "消息不能超过 4000 个字符" });
            var senderId = access.PeerDeviceId ?? CleanName(request.Query["fromId"].ToString()) ?? access.PhoneSessionId ?? _sessionId;
            var item = await access.Store.AddMessageAsync(text, CleanName(request.Query["from"].ToString()) ?? "电脑", senderId, request.HttpContext.RequestAborted);
            await BroadcastAsync(new ServerEvent("message", item), access.RoomId);
            return Results.Ok();
        });
        app.MapPost("/api/transfer/request", async (HttpRequest request, PhoneFileRequest body) =>
        {
            if (!AuthorizedMaster(request)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var access = ResolveAccess(request);
            if (access is null || !IsExternalPhoneRequest(request, access)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var sessionId = CleanName(body.SessionId);
            var sender = CleanName(body.From) ?? "手机设备";
            var fileName = CleanFileName(body.Name);
            var transferId = string.IsNullOrWhiteSpace(body.TransferId) ? Guid.NewGuid().ToString("N") : body.TransferId;
            if (string.IsNullOrWhiteSpace(sessionId) || sessionId != access.PhoneSessionId || string.IsNullOrWhiteSpace(fileName) ||
                body.Size <= 0 || body.Size > MaxFileSize || !Guid.TryParseExact(transferId, "N", out _))
                return Results.BadRequest(new { error = "文件信息无效" });
            var address = GetRemoteAddress(request);
            var approve = IncomingTransferApprovalRequested;
            if (approve is null || !await approve(new IncomingTransferRequest(sender, address, "file", fileName, body.Size, null), request.HttpContext.RequestAborted))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            foreach (var (key, grant) in _transferGrants)
                if (grant.ExpiresAt < DateTimeOffset.UtcNow) _transferGrants.TryRemove(key, out _);
            var ticket = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
            _transferGrants[ticket] = new TransferGrant(sessionId, sender, fileName, body.Size, address, transferId, DateTimeOffset.UtcNow.AddHours(12));
            return Results.Json(new { ticket, transferId, expiresIn = 43200 }, JsonOptions);
        });
        app.MapPost("/api/upload", UploadAsync);
        app.MapGet("/api/upload/{transferId}/status", UploadStatusAsync);
        app.MapPut("/api/upload/{transferId}/chunk", UploadChunkAsync);
        app.MapPost("/api/upload/{transferId}/complete", CompleteUploadAsync);
        app.Map("/ws", WebSocketAsync);

        try
        {
            await app.StartAsync(cancellationToken);
            var server = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>();
            var addresses = server.Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()?.Addresses;
            var port = addresses?.Select(ParsePort).FirstOrDefault(value => value > 0) ?? throw new InvalidOperationException("无法获取局域网服务端口");
            var address = SelectLanAddress();
            BaseUrl = $"http://{address}:{port}";
            LocalBaseUrl = $"http://127.0.0.1:{port}";
            _http = new HttpClient { BaseAddress = new Uri(LocalBaseUrl), Timeout = Timeout.InfiniteTimeSpan };
            _app = app;
        }
        catch
        {
            try { await app.StopAsync(CancellationToken.None); } catch { }
            await app.DisposeAsync();
            throw;
        }
    }

    public async Task SendMessageAsync(string text, CancellationToken cancellationToken = default)
    {
        if (_http is null) throw new InvalidOperationException("服务尚未启动");
        var url = $"/api/message?code={Uri.EscapeDataString(_code)}&from={Uri.EscapeDataString(DeviceName)}&session={Uri.EscapeDataString(_sessionId)}";
        using var response = await _http.PostAsJsonAsync(url, new MessageRequest(text), JsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task UploadFileAsync(string filePath, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (_http is null) throw new InvalidOperationException("服务尚未启动");
        var url = $"/api/upload?code={Uri.EscapeDataString(_code)}&from={Uri.EscapeDataString(DeviceName)}&session={Uri.EscapeDataString(_sessionId)}";
        await ReliableTransfer.UploadFileAsync(_http, filePath, url, progress, cancellationToken);
    }

    public async Task DownloadFileAsync(string id, string name, string destination, CancellationToken cancellationToken = default)
    {
        if (_http is null) throw new InvalidOperationException("服务尚未启动");
        var url = $"/api/file/{Uri.EscapeDataString(id)}?code={Uri.EscapeDataString(_code)}&name={Uri.EscapeDataString(name)}";
        await ReliableTransfer.DownloadFileAsync(_http, url, destination, cancellationToken: cancellationToken);
    }

    private async Task<IResult> UploadAsync(HttpRequest request)
    {
        if (!await _uploadSlots.WaitAsync(0)) return Results.StatusCode(StatusCodes.Status429TooManyRequests);
        try { return await UploadCoreAsync(request); }
        finally { _uploadSlots.Release(); }
    }

    private async Task<IResult> UploadStatusAsync(string transferId, HttpRequest request)
    {
        var access = ResolveAccess(request);
        if (access is null) return Results.StatusCode(StatusCodes.Status403Forbidden);
        try
        {
            var upload = GetResumableUpload(request, access, transferId);
            if (!HasValidTransferGrant(request, access, upload)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var state = await access.Store.GetResumableUploadStateAsync(upload, request.HttpContext.RequestAborted);
            return Results.Json(new { offset = state.Offset, completed = state.Completed is not null, item = state.Completed }, JsonOptions);
        }
        catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
    }

    private async Task<IResult> UploadChunkAsync(string transferId, HttpRequest request)
    {
        if (!await _uploadSlots.WaitAsync(0)) return Results.StatusCode(StatusCodes.Status429TooManyRequests);
        try { return await UploadChunkCoreAsync(transferId, request); }
        finally { _uploadSlots.Release(); }
    }

    private async Task<IResult> UploadChunkCoreAsync(string transferId, HttpRequest request)
    {
        var access = ResolveAccess(request);
        if (access is null) return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (request.ContentLength is not > 0 or > 4 * 1024 * 1024)
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        if (!long.TryParse(request.Query["offset"], out var offset))
            return Results.BadRequest(new { error = "数据块位置无效" });

        try
        {
            var upload = GetResumableUpload(request, access, transferId);
            if (!HasValidTransferGrant(request, access, upload)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var bytes = new byte[(int)request.ContentLength.Value];
            await request.Body.ReadExactlyAsync(bytes, request.HttpContext.RequestAborted);
            var nextOffset = await access.Store.AppendResumableChunkAsync(upload, offset, bytes, request.HttpContext.RequestAborted);
            return Results.Json(new { offset = nextOffset }, JsonOptions);
        }
        catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
        catch (EndOfStreamException ex) { return Results.BadRequest(new { error = ex.Message }); }
    }

    private async Task<IResult> CompleteUploadAsync(string transferId, HttpRequest request)
    {
        var access = ResolveAccess(request);
        if (access is null) return Results.StatusCode(StatusCodes.Status403Forbidden);
        try
        {
            var upload = GetResumableUpload(request, access, transferId);
            if (!HasValidTransferGrant(request, access, upload)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var result = await access.Store.CompleteResumableUploadAsync(upload, request.HttpContext.RequestAborted);
            if (result.Created) await BroadcastAsync(new ServerEvent("file", result.Entry), access.RoomId);
            return Results.Json(result.Entry, JsonOptions);
        }
        catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
        catch (FileNotFoundException ex) { return Results.Conflict(new { error = ex.Message }); }
    }

    private static TransferStore.ResumableUpload GetResumableUpload(HttpRequest request, SessionAccess access, string transferId)
    {
        var name = CleanFileName(request.Query["name"].ToString());
        var from = CleanName(request.Query["from"].ToString()) ?? "设备";
        var fromId = access.PeerDeviceId ?? access.PhoneSessionId ?? CleanName(request.Query["session"].ToString()) ?? "";
        var contentTypeProvider = new FileExtensionContentTypeProvider();
        if (!contentTypeProvider.TryGetContentType(name, out var contentType)) contentType = "application/octet-stream";
        if (!long.TryParse(request.Query["size"], out var size)) size = 0;
        return new TransferStore.ResumableUpload(transferId, name, from, fromId, size,
            request.Query["sha256"].ToString(), contentType);
    }

    private bool HasValidTransferGrant(HttpRequest request, SessionAccess access, TransferStore.ResumableUpload upload)
    {
        if (!IsExternalPhoneRequest(request, access)) return true;
        var ticket = request.Query["ticket"].ToString();
        return !string.IsNullOrWhiteSpace(ticket) && _transferGrants.TryGetValue(ticket, out var grant) &&
            grant.ExpiresAt >= DateTimeOffset.UtcNow && grant.SessionId == upload.FromId && grant.Sender == upload.From &&
            grant.TransferId == upload.TransferId && grant.FileName == upload.Name && grant.Size == upload.Size &&
            grant.Address == GetRemoteAddress(request);
    }

    private async Task<IResult> UploadCoreAsync(HttpRequest request)
    {
        var access = ResolveAccess(request);
        if (access is null) return Results.StatusCode(StatusCodes.Status403Forbidden);
        IFormCollection form;
        try { form = await request.ReadFormAsync(request.HttpContext.RequestAborted); }
        catch { return Results.BadRequest(new { error = "上传内容无效" }); }
        var file = form.Files.GetFile("file");
        if (file is null || file.Length == 0) return Results.BadRequest(new { error = "请选择一个文件" });
        if (IsExternalPhoneRequest(request, access))
        {
            var ticket = request.Query["ticket"].ToString();
            if (string.IsNullOrWhiteSpace(ticket) || !_transferGrants.TryGetValue(ticket, out var grant) ||
                grant.ExpiresAt < DateTimeOffset.UtcNow || grant.SessionId != CleanName(request.Query["session"].ToString()) ||
                grant.Sender != (CleanName(request.Query["from"].ToString()) ?? "设备") ||
                !string.Equals(grant.FileName, CleanFileName(file.FileName), StringComparison.Ordinal) || grant.Size != file.Length ||
                !string.IsNullOrWhiteSpace(request.Query["transferId"]) && grant.TransferId != request.Query["transferId"].ToString() ||
                !string.Equals(grant.Address, GetRemoteAddress(request), StringComparison.Ordinal) ||
                !_transferGrants.TryRemove(ticket, out _))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
        }
        var provider = new FileExtensionContentTypeProvider();
        if (!provider.TryGetContentType(file.FileName, out var contentType))
        {
            contentType = System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(file.ContentType, out var parsedType)
                ? parsedType.MediaType ?? "application/octet-stream"
                : "application/octet-stream";
        }
        TransferStore.StoreResult stored;
        try
        {
            await using var source = file.OpenReadStream();
            stored = await access.Store.StoreFileAsync(source, file.FileName, file.Length,
                CleanName(request.Query["from"].ToString()) ?? "设备",
                access.PeerDeviceId ?? access.PhoneSessionId ?? CleanName(request.Query["session"].ToString()) ?? "",
                contentType, request.Query["transferId"].ToString(), request.Query["sha256"].ToString(),
                request.HttpContext.RequestAborted);
        }
        catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
        catch (EndOfStreamException ex) { return Results.BadRequest(new { error = ex.Message }); }
        if (stored.Created) await BroadcastAsync(new ServerEvent("file", stored.Entry), access.RoomId);
        return Results.Json(stored.Entry, JsonOptions);
    }

    private async Task WebSocketAsync(HttpContext context)
    {
        var access = ResolveAccess(context.Request);
        if (access is null || !context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var sessionId = access.PeerDeviceId ?? access.PhoneSessionId ?? CleanName(context.Request.Query["session"].ToString()) ?? Guid.NewGuid().ToString("N");
        var name = CleanName(context.Request.Query["name"].ToString()) ?? "未命名设备";
        var requestedKind = context.Request.Query["kind"].ToString();
        var kind = requestedKind is "desktop" or "phone" ? requestedKind : "device";
        var peer = new Peer(socket, sessionId, name, kind, access.RoomId, access.Store);
        _peers[socket] = peer;
        await BroadcastPresenceAsync();
        var history = access.PhoneSessionId is not null
            ? GetPhoneVisibleHistory(access.PhoneSessionId)
            : access.Store.GetHistory();
        foreach (var batch in history.Chunk(32))
            await SendAsync(peer, new ServerEvent("history", Items: batch));

        var buffer = new byte[8192];
        try
        {
            while (socket.State == WebSocketState.Open && !context.RequestAborted.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult received;
                do
                {
                    received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), context.RequestAborted);
                    if (received.MessageType == WebSocketMessageType.Close) break;
                    if (message.Length + received.Count > 64 * 1024) throw new InvalidDataException("WebSocket message too large");
                    message.Write(buffer, 0, received.Count);
                } while (!received.EndOfMessage);

                if (received.MessageType == WebSocketMessageType.Close) break;
                using var document = JsonDocument.Parse(message.ToArray());
                var root = document.RootElement;
                if (root.TryGetProperty("type", out var controlType) && controlType.GetString() == "rename" && root.TryGetProperty("name", out var nameNode))
                {
                    peer.Name = CleanName(nameNode.GetString()) ?? peer.Name;
                    await BroadcastPresenceAsync();
                    continue;
                }
                if (root.TryGetProperty("type", out var type) && type.GetString() == "message" && root.TryGetProperty("text", out var textNode))
                {
                    var text = textNode.GetString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        var item = await peer.Store.AddMessageAsync(text[..Math.Min(text.Length, 4000)], peer.Name, peer.SessionId, context.RequestAborted);
                        await BroadcastAsync(new ServerEvent("message", item), peer.RoomId);
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (JsonException) { }
        catch (InvalidDataException) { }
        finally
        {
            _peers.TryRemove(socket, out _);
            if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)
            {
                try { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "closed", CancellationToken.None); } catch { }
            }
            await BroadcastPresenceAsync();
        }
    }

    private SessionAccess? ResolveAccess(HttpRequest request)
    {
        var supplied = request.Query["code"].ToString();
        if (IsValidSecret(supplied, _code))
        {
            var remoteAddress = request.HttpContext.Connection.RemoteIpAddress;
            if (remoteAddress is not null && !IPAddress.IsLoopback(remoteAddress.IsIPv4MappedToIPv6 ? remoteAddress.MapToIPv4() : remoteAddress))
            {
                var phoneSessionId = CleanName(request.Query["session"].ToString());
                if (phoneSessionId is null || phoneSessionId == DeviceId) return null;
                var roomId = GetPhoneRoomId(phoneSessionId);
                return new SessionAccess(roomId, GetPhoneStore(phoneSessionId), null, phoneSessionId);
            }
            return new SessionAccess("phone", _store, null, null);
        }
        foreach (var (deviceId, token) in _trustedPeerTokens)
        {
            if (!IsValidSecret(supplied, token)) continue;
            var roomId = GetPeerRoomId(DeviceId, deviceId);
            var store = GetPeerStore(deviceId);
            return new SessionAccess(roomId, store, deviceId, null);
        }
        return null;
    }

    public string GetOrCreatePeerToken(string deviceId)
    {
        lock (_trustedPeerLock)
        {
            if (_trustedPeerTokens.TryGetValue(deviceId, out var existing)) return existing;
            var token = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
            _trustedPeerTokens[deviceId] = token;
            SaveTrustedPeers();
            return token;
        }
    }

    public IReadOnlyList<string> GetTrustedPeerIds() => _trustedPeerTokens.Keys.ToArray();

    public async Task ForgetConnectedDeviceAsync(string deviceId)
    {
        foreach (var peer in _peers.Values.Where(peer => peer.SessionId == deviceId).ToArray())
        {
            _peers.TryRemove(peer.Socket, out _);
            try { if (peer.Socket.State == WebSocketState.Open) await peer.Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "device removed", CancellationToken.None); } catch { }
        }
        lock (_trustedPeerLock)
            if (_trustedPeerTokens.TryRemove(deviceId, out _)) SaveTrustedPeers();
        await BroadcastPresenceAsync();
    }

    private void RegisterPeerToken(string deviceId, string token)
    {
        lock (_trustedPeerLock)
        {
            if (_trustedPeerTokens.ContainsKey(deviceId)) return;
            _trustedPeerTokens[deviceId] = token;
            SaveTrustedPeers();
        }
    }

    private static bool IsValidPeerToken(string? token) => !string.IsNullOrWhiteSpace(token) && token.Length == 43 && token.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_');

    private static string? ValidatePeerBaseUrl(string? baseUrl, IPAddress? remoteAddress)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) return null;
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme != "http" ||
            !IPAddress.TryParse(uri.Host, out var advertisedAddress) || uri.Port is < 1 or > 65535 ||
            !string.IsNullOrEmpty(uri.UserInfo) || uri.AbsolutePath != "/") return null;
        var source = remoteAddress?.IsIPv4MappedToIPv6 == true ? remoteAddress.MapToIPv4() : remoteAddress;
        var target = advertisedAddress.IsIPv4MappedToIPv6 ? advertisedAddress.MapToIPv4() : advertisedAddress;
        return source is not null && source.Equals(target) && !IPAddress.IsLoopback(target) ? uri.GetLeftPart(UriPartial.Authority) : null;
    }

    private string GetPeerRoomId(string firstDeviceId, string secondDeviceId)
    {
        var ordered = string.CompareOrdinal(firstDeviceId, secondDeviceId) < 0
            ? $"{firstDeviceId}:{secondDeviceId}" : $"{secondDeviceId}:{firstDeviceId}";
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(ordered))).ToLowerInvariant();
    }

    private TransferStore GetPeerStore(string peerDeviceId)
    {
        var roomId = GetPeerRoomId(DeviceId, peerDeviceId);
        return _peerStores.GetOrAdd(roomId, id => new Lazy<TransferStore>(() =>
        {
            var peerStore = new TransferStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nearlink", "peer-sessions", id));
            peerStore.Initialize();
            return peerStore;
        }, LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    private TransferStore GetPhoneStore(string phoneSessionId)
    {
        var sessionId = CleanName(phoneSessionId) ?? throw new ArgumentException("Invalid phone session", nameof(phoneSessionId));
        var roomId = GetPhoneRoomId(sessionId);
        _phoneRoomSessions[roomId] = sessionId;
        return _phoneStores.GetOrAdd(sessionId, id => new Lazy<TransferStore>(() =>
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nearlink", "phone-sessions", GetPhoneRoomId(id));
            Directory.CreateDirectory(folder);
            var identityPath = Path.Combine(folder, "session-id.txt");
            if (!File.Exists(identityPath)) File.WriteAllText(identityPath, id);
            var phoneStore = new TransferStore(folder);
            phoneStore.Initialize();
            return phoneStore;
        }, LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    private IEnumerable<string> EnumeratePhoneSessions()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nearlink", "phone-sessions");
        if (!Directory.Exists(folder)) return [];
        return Directory.EnumerateDirectories(folder).Select(path =>
        {
            try { return CleanName(File.ReadAllText(Path.Combine(path, "session-id.txt"))); } catch { return null; }
        }).Where(id => id is not null).Cast<string>().Distinct(StringComparer.Ordinal).ToArray();
    }

    private TransferStore.Entry[] GetPhoneVisibleHistory(string sessionId)
    {
        var otherPhoneMessages = EnumeratePhoneSessions()
            .Where(otherId => !string.Equals(otherId, sessionId, StringComparison.Ordinal))
            .SelectMany(otherId => GetPhoneStore(otherId).GetHistory())
            .Where(item => item.Kind == "message");
        return GetPhoneStore(sessionId).GetHistory()
            .Concat(_store.GetHistory().Where(item => item.FromId == DeviceId))
            .Concat(otherPhoneMessages)
            .DistinctBy(item => item.Id, StringComparer.Ordinal)
            .OrderBy(item => item.Time)
            .ToArray();
    }

    private static string GetPhoneRoomId(string sessionId) => "phone-" + Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(sessionId))).ToLowerInvariant();

    private TransferStore? GetFileStore(SessionAccess access, string id, out TransferStore.Entry? item)
    {
        if (access.Store.TryGetFile(id, out item)) return access.Store;
        if (access.PhoneSessionId is not null && _store.TryGetFile(id, out item)) return _store;
        item = null;
        return null;
    }

    private bool AuthorizedMaster(HttpRequest request) => IsValidSecret(request.Query["code"].ToString(), _code);

    private static bool IsExternalPhoneRequest(HttpRequest request, SessionAccess access)
    {
        if (access.PhoneSessionId is null) return false;
        var address = request.HttpContext.Connection.RemoteIpAddress;
        if (address is null) return false;
        return !IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);
    }

    private static string CleanFileName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var name = Path.GetFileName(value.Replace('\\', '/'));
        var clean = new string(name.Where(ch => !char.IsControl(ch) && ch is not '<' and not '>').ToArray()).Trim();
        return clean.Length == 0 ? "" : clean[..Math.Min(clean.Length, 240)];
    }

    private static bool IsValidSecret(string supplied, string expected)
    {
        if (supplied.Length != expected.Length) return false;
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(supplied), Encoding.ASCII.GetBytes(expected));
    }

    private static string GetRemoteAddress(HttpRequest request)
    {
        var address = request.HttpContext.Connection.RemoteIpAddress;
        if (address is null) return "局域网设备";
        return (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
    }

    private void LoadTrustedPeers()
    {
        lock (_trustedPeerLock)
        {
            try
            {
                if (!File.Exists(_trustedPeerPath)) return;
                var records = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_trustedPeerPath), JsonOptions);
                if (records is null) return;
                foreach (var (id, token) in records)
                    if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(token)) _trustedPeerTokens[id] = token;
            }
            catch { _trustedPeerTokens.Clear(); }
        }
    }

    private void SaveTrustedPeers()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_trustedPeerPath)!);
        var tempPath = _trustedPeerPath + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(_trustedPeerTokens, JsonOptions));
        File.Move(tempPath, _trustedPeerPath, overwrite: true);
    }

    private static string LoadOrCreateDeviceId()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nearlink");
        var path = Path.Combine(folder, "device-id.txt");
        try
        {
            Directory.CreateDirectory(folder);
            if (File.Exists(path) && Guid.TryParse(File.ReadAllText(path).Trim(), out var existing)) return existing.ToString("N");
            var id = Guid.NewGuid().ToString("N");
            File.WriteAllText(path, id);
            return id;
        }
        catch { return Guid.NewGuid().ToString("N"); }
    }

    public bool TrySetDeviceName(string? value, out string error)
    {
        var name = CleanName(value);
        if (string.IsNullOrWhiteSpace(name))
        {
            error = "设备标识不能为空";
            return false;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_deviceNamePath)!);
            File.WriteAllText(_deviceNamePath, name);
            DeviceName = name;
            error = "";
            return true;
        }
        catch (IOException ex)
        {
            error = $"无法保存设备标识：{ex.Message}";
            return false;
        }
    }

    private static string LoadDeviceName()
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nearlink", "device-name.txt");
            return CleanName(File.Exists(path) ? File.ReadAllText(path) : null) ?? Environment.MachineName;
        }
        catch { return Environment.MachineName; }
    }

    private static string LoadOrCreatePairCode()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nearlink");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "phone-pair-code.txt");
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).Trim();
            if (existing.Length == 16 && existing.All(Uri.IsHexDigit)) return existing.ToUpperInvariant();
        }
        var code = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8));
        File.WriteAllText(path, code);
        return code;
    }

    public sealed record PairRequest(string DeviceId, string Name, string Address, string? BaseUrl, string? Token);
    public sealed record PairRequestBody(string DeviceId, string Name, string? Token = null, string? BaseUrl = null);
    public sealed record IncomingTransferRequest(string DeviceName, string Address, string Kind, string? FileName, long Size, string? Text);
    public sealed record PhoneFileRequest(string SessionId, string From, string Name, long Size, string? TransferId = null);
    public sealed record PairResponse(string DeviceId, string Name, string Token);
    private sealed record TransferGrant(string SessionId, string Sender, string FileName, long Size, string Address, string TransferId, DateTimeOffset ExpiresAt);
    private static void MapEmbeddedAsset(WebApplication app, string path, string fileName, string contentType)
    {
        app.MapGet(path, async (HttpContext context) =>
        {
            await using var stream = typeof(TransferHost).Assembly.GetManifestResourceStream($"Nearlink.Native.wwwroot.{fileName}");
            if (stream is null) { context.Response.StatusCode = StatusCodes.Status404NotFound; return; }
            context.Response.Headers["Cache-Control"] = "no-store, max-age=0";
            context.Response.ContentType = contentType;
            await stream.CopyToAsync(context.Response.Body, context.RequestAborted);
        });
    }
    private static string? CleanName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var clean = new string(value.Where(ch => !char.IsControl(ch) && ch is not '<' and not '>').ToArray()).Trim();
        return clean.Length == 0 ? null : clean[..Math.Min(clean.Length, 32)];
    }
    private List<DeviceInfo> GetDevices() => _peers.Values
        .Select(peer => new DeviceInfo(peer.SessionId, peer.Name, peer.Kind))
        .DistinctBy(device => device.Id, StringComparer.Ordinal)
        .ToList();

    private async Task BroadcastPresenceAsync()
    {
        var devices = GetDevices();
        try { ConnectedDevicesChanged?.Invoke(devices); } catch { }
        foreach (var roomId in _peers.Values.Select(peer => peer.RoomId).Distinct(StringComparer.Ordinal).ToArray())
            await BroadcastToRoomAsync(new ServerEvent("presence", Devices: devices), roomId);
    }

    private async Task BroadcastAsync(ServerEvent data, string roomId)
    {
        if (roomId.StartsWith("phone-", StringComparison.Ordinal))
        {
            if (_phoneRoomSessions.TryGetValue(roomId, out var phoneSessionId))
            {
                try { PhoneSessionEvent?.Invoke(phoneSessionId, data); } catch { }
            }
        }
        else if (roomId != "phone")
        {
            var peerDeviceId = _trustedPeerTokens.Keys.FirstOrDefault(id => GetPeerRoomId(DeviceId, id) == roomId);
            if (peerDeviceId is not null)
            {
                try { PeerSessionEvent?.Invoke(peerDeviceId, data); } catch { }
            }
        }
        await BroadcastToRoomAsync(data, roomId);
        if (roomId.StartsWith("phone-", StringComparison.Ordinal) && data.Type == "message")
        {
            var otherPhoneRooms = _peers.Values
                .Select(peer => peer.RoomId)
                .Where(candidate => candidate.StartsWith("phone-", StringComparison.Ordinal) && candidate != roomId)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            foreach (var phoneRoom in otherPhoneRooms) await BroadcastToRoomAsync(data, phoneRoom);
        }
        if (roomId == "phone" && data.Type is "message" or "file")
        {
            var phoneRooms = _peers.Values.Where(peer => peer.RoomId.StartsWith("phone-", StringComparison.Ordinal))
                .Select(peer => peer.RoomId).Distinct(StringComparer.Ordinal).ToArray();
            foreach (var phoneRoom in phoneRooms) await BroadcastToRoomAsync(data, phoneRoom);
        }
    }

    private async Task BroadcastToRoomAsync(ServerEvent data, string roomId)
    {
        var json = JsonSerializer.Serialize(data, JsonOptions);
        var recipients = _peers.Values.Where(peer => peer.RoomId == roomId).ToArray();
        var results = await Task.WhenAll(recipients.Select(peer => SendTextAsync(peer, json)));
        for (var index = 0; index < recipients.Length; index++)
        {
            if (results[index]) continue;
            _peers.TryRemove(recipients[index].Socket, out _);
            try { recipients[index].Socket.Abort(); } catch { }
        }
    }

    private static async Task SendAsync(Peer peer, ServerEvent data)
    {
        _ = await SendTextAsync(peer, JsonSerializer.Serialize(data, JsonOptions));
    }
    private static async Task<bool> SendTextAsync(Peer peer, string json)
    {
        await peer.SendLock.WaitAsync();
        try
        {
            if (peer.Socket.State != WebSocketState.Open) return false;
            await peer.Socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);
            return true;
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or InvalidOperationException) { return false; }
        finally { peer.SendLock.Release(); }
    }

    private static string SelectLanAddress()
    {
        var adapters = NetworkInterface.GetAllNetworkInterfaces()
            .Where(network => network.OperationalStatus == OperationalStatus.Up && network.NetworkInterfaceType is NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Ethernet)
            .Where(network => !System.Text.RegularExpressions.Regex.IsMatch($"{network.Name} {network.Description}", "virtual|vmware|vmnet|vbox|hyper-v|wsl|docker|meta|vpn|tailscale|zerotier|wireguard", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            .SelectMany(network => network.GetIPProperties().UnicastAddresses
                .Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork && IsPrivate(address.Address))
                .Select(address => new { Network = network, Address = address.Address.ToString(), HasGateway = network.GetIPProperties().GatewayAddresses.Any(gateway => gateway.Address.AddressFamily == AddressFamily.InterNetwork && !gateway.Address.Equals(IPAddress.Any)) }))
            .OrderByDescending(item => item.HasGateway)
            .ThenBy(item => item.Network.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 0 : 1)
            .ToList();
        return adapters.FirstOrDefault()?.Address ?? "127.0.0.1";
    }

    private static bool IsPrivate(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 || bytes[0] == 192 && bytes[1] == 168 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31;
    }

    private static int ParsePort(string address)
    {
        if (Uri.TryCreate(address, UriKind.Absolute, out var uri)) return uri.Port;
        return 0;
    }

    public async ValueTask DisposeAsync()
    {
        _http?.Dispose();
        if (_app is not null) await _app.StopAsync();
        if (_app is not null) await _app.DisposeAsync();
    }

    private sealed record SessionAccess(string RoomId, TransferStore Store, string? PeerDeviceId, string? PhoneSessionId);
    private sealed class Peer(WebSocket socket, string sessionId, string name, string kind, string roomId, TransferStore store)
    {
        public WebSocket Socket { get; } = socket;
        public string SessionId { get; } = sessionId;
        public string Name { get; set; } = name;
        public string Kind { get; } = kind;
        public string RoomId { get; } = roomId;
        public TransferStore Store { get; } = store;
        public SemaphoreSlim SendLock { get; } = new(1, 1);
    }

    private sealed record MessageRequest(string? Text);
    public sealed record DeviceInfo(string Id, string Name, string Kind);
    public sealed record ServerEvent(string Type, TransferStore.Entry? Item = null, IReadOnlyList<DeviceInfo>? Devices = null, IReadOnlyList<TransferStore.Entry>? Items = null);

}
