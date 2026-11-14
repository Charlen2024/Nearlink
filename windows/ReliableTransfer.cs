using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Nearlink.Native;

/// <summary>
/// Reliable file-transfer operations shared by the local host and paired desktop sessions.
/// Retries reuse one transfer id, so a lost response cannot create duplicate history entries.
/// </summary>
internal static class ReliableTransfer
{
    private const int MaxAttempts = 4;
    private const int ChunkSize = 4 * 1024 * 1024;
    private const long MaxFileSize = 1024L * 1024 * 1024;

    public static async Task UploadFileAsync(HttpClient client, string path, string url,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("找不到要发送的文件", path);
        if (info.Length <= 0 || info.Length > MaxFileSize)
            throw new InvalidDataException("文件必须大于 0 且不能超过 1 GB");

        await using var hashStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var expectedHash = Convert.ToHexString(await SHA256.HashDataAsync(hashStream, cancellationToken));
        var transferKey = BuildTransferKey(url, info, expectedHash);
        var transferId = OutgoingTransferJournal.GetOrCreate(transferKey);
        var metadata = new UploadMetadata(transferId, info.Name, info.Length, expectedHash);
        var status = await GetUploadStatusAsync(client, url, metadata, cancellationToken);
        if (status.Completed)
        {
            OutgoingTransferJournal.Remove(transferKey, transferId);
            progress?.Report(1d);
            return;
        }
        if (status.Offset < 0 || status.Offset > info.Length)
            throw new InvalidDataException("服务端返回了无效的断点位置");

        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.RandomAccess);
        var buffer = new byte[ChunkSize];
        var offset = status.Offset;
        while (offset < info.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = (int)Math.Min(buffer.Length, info.Length - offset);
            input.Position = offset;
            await input.ReadExactlyAsync(buffer.AsMemory(0, count), cancellationToken);
            Exception? lastError = null;
            var advanced = false;

            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                try
                {
                    using var content = new ByteArrayContent(buffer, 0, count);
                    using var attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    attemptCancellation.CancelAfter(TimeSpan.FromHours(5));
                    using var response = await client.PutAsync(BuildUploadUrl(url, metadata, "chunk", offset), content,
                        attemptCancellation.Token);
                    if (IsTransient(response.StatusCode))
                    {
                        lastError = new HttpRequestException($"临时传输错误：HTTP {(int)response.StatusCode}");
                        if (attempt == MaxAttempts) break;
                        await Task.Delay(GetRetryDelay(response, attempt), cancellationToken);
                    }
                    else
                    {
                        response.EnsureSuccessStatusCode();
                        offset = await ReadOffsetAsync(response, cancellationToken);
                        if (offset <= status.Offset || offset > status.Offset + count || offset > info.Length)
                            throw new InvalidDataException("服务端返回了无效的断点位置");
                        advanced = offset > status.Offset;
                        if (!advanced) throw new IOException("服务端尚未确认数据块");
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (OperationCanceledException ex)
                {
                    lastError = ex;
                    if (attempt == MaxAttempts) break;
                    await Task.Delay(Backoff(attempt), cancellationToken);
                }
                catch (Exception ex) when (IsRetryable(ex))
                {
                    lastError = ex;
                    if (attempt == MaxAttempts) break;
                    await Task.Delay(Backoff(attempt), cancellationToken);
                }

                if (!advanced && attempt < MaxAttempts)
                {
                    var current = await GetUploadStatusAsync(client, url, metadata, cancellationToken);
                    if (current.Completed) { progress?.Report(1d); return; }
                    if (current.Offset > status.Offset)
                    {
                        if (current.Offset > status.Offset + count)
                            throw new InvalidDataException("服务端返回了超出当前数据块的断点位置");
                        offset = current.Offset;
                        advanced = true;
                    }
                }
                if (advanced) break;
            }

            if (!advanced) throw new IOException($"数据块在 {MaxAttempts} 次尝试后仍未确认", lastError);
            status = new UploadStatus(offset, Completed: false);
            progress?.Report(Math.Min(0.99, (double)offset / info.Length));
        }

        Exception? completionError = null;
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                using var attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                attemptCancellation.CancelAfter(TimeSpan.FromHours(5));
                using var response = await client.PostAsync(BuildUploadUrl(url, metadata, "complete"), content: null,
                    attemptCancellation.Token);
                if (IsTransient(response.StatusCode) && attempt < MaxAttempts)
                {
                    completionError = new HttpRequestException($"临时传输错误：HTTP {(int)response.StatusCode}");
                    await Task.Delay(GetRetryDelay(response, attempt), cancellationToken);
                    continue;
                }
                response.EnsureSuccessStatusCode();
                OutgoingTransferJournal.Remove(transferKey, transferId);
                progress?.Report(1d);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (OperationCanceledException ex) when (attempt < MaxAttempts)
            {
                completionError = ex;
                await Task.Delay(Backoff(attempt), cancellationToken);
            }
            catch (Exception ex) when (IsRetryable(ex) && attempt < MaxAttempts)
            {
                completionError = ex;
                await Task.Delay(Backoff(attempt), cancellationToken);
            }
        }
        throw new IOException($"文件入库在 {MaxAttempts} 次尝试后仍未确认", completionError);
    }

    private static async Task<UploadStatus> GetUploadStatusAsync(HttpClient client, string baseUrl,
        UploadMetadata metadata, CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                using var attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                attemptCancellation.CancelAfter(TimeSpan.FromMinutes(2));
                using var response = await client.GetAsync(BuildUploadUrl(baseUrl, metadata, "status"), attemptCancellation.Token);
                if (IsTransient(response.StatusCode) && attempt < MaxAttempts)
                {
                    lastError = new HttpRequestException($"临时传输错误：HTTP {(int)response.StatusCode}");
                    await Task.Delay(GetRetryDelay(response, attempt), cancellationToken);
                    continue;
                }
                response.EnsureSuccessStatusCode();
                using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
                var root = document.RootElement;
                return new UploadStatus(root.GetProperty("offset").GetInt64(), root.GetProperty("completed").GetBoolean());
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (OperationCanceledException ex) when (attempt < MaxAttempts)
            {
                lastError = ex;
                await Task.Delay(Backoff(attempt), cancellationToken);
            }
            catch (Exception ex) when (IsRetryable(ex) && attempt < MaxAttempts)
            {
                lastError = ex;
                await Task.Delay(Backoff(attempt), cancellationToken);
            }
        }
        throw new IOException($"无法读取文件断点状态（{MaxAttempts} 次尝试）", lastError);
    }

    private static async Task<long> ReadOffsetAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        return document.RootElement.GetProperty("offset").GetInt64();
    }

    private static string BuildUploadUrl(string baseUrl, UploadMetadata metadata, string action, long? offset = null)
    {
        var queryAt = baseUrl.IndexOf('?');
        var path = queryAt >= 0 ? baseUrl[..queryAt] : baseUrl;
        var query = queryAt >= 0 ? baseUrl[(queryAt + 1)..] : "";
        var suffix = $"/{Uri.EscapeDataString(metadata.TransferId)}/{action}";
        var metadataQuery = $"name={Uri.EscapeDataString(metadata.Name)}&size={metadata.Size}&sha256={metadata.Sha256}";
        var fullPath = path.TrimEnd('/') + suffix;
        var queryParts = string.IsNullOrWhiteSpace(query) ? metadataQuery : $"{query}&{metadataQuery}";
        if (offset is not null) queryParts += $"&offset={offset.Value}";
        return $"{fullPath}?{queryParts}";
    }

    public static async Task DownloadFileAsync(HttpClient client, string url, string destination,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(destination))!;
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(destination)}.nearlink-{Guid.NewGuid():N}.tmp");
        Exception? lastError = null;

        try
        {
            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    using var attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    attemptCancellation.CancelAfter(TimeSpan.FromHours(5));
                    using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, attemptCancellation.Token);
                    if (IsTransient(response.StatusCode) && attempt < MaxAttempts)
                    {
                        await Task.Delay(GetRetryDelay(response, attempt), cancellationToken);
                        continue;
                    }
                    response.EnsureSuccessStatusCode();
                    if (response.Content.Headers.ContentLength is long contentLength && contentLength > MaxFileSize)
                        throw new InvalidDataException("接收文件超过 1 GB 限制");

                    await using var source = await response.Content.ReadAsStreamAsync(attemptCancellation.Token);
                    using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    await using (var target = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None,
                        128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                    {
                        var buffer = new byte[128 * 1024];
                        long received = 0;
                        int read;
                        while ((read = await source.ReadAsync(buffer, attemptCancellation.Token)) > 0)
                        {
                            received += read;
                            if (received > MaxFileSize) throw new InvalidDataException("接收文件超过 1 GB 限制");
                            hasher.AppendData(buffer, 0, read);
                            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                            if (response.Content.Headers.ContentLength is long expected && expected > 0)
                                progress?.Report(Math.Min(0.99, (double)received / expected));
                        }

                        if (response.Content.Headers.ContentLength is long expectedLength && received != expectedLength)
                            throw new IOException("文件长度不完整，可安全重试");
                        if (response.Headers.TryGetValues("X-Nearlink-SHA256", out var values))
                        {
                            var expectedHash = values.FirstOrDefault();
                            var actualHash = Convert.ToHexString(hasher.GetHashAndReset());
                            if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
                                throw new IOException("文件完整性校验失败，可安全重试");
                        }
                        await target.FlushAsync(cancellationToken);
                        target.Flush(flushToDisk: true);
                    }

                    File.Move(tempPath, destination, overwrite: true);
                    progress?.Report(1d);
                    return;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException ex) when (attempt < MaxAttempts)
                {
                    lastError = ex;
                    TryDelete(tempPath);
                    await Task.Delay(Backoff(attempt), cancellationToken);
                }
                catch (Exception ex) when (IsRetryable(ex) && attempt < MaxAttempts)
                {
                    lastError = ex;
                    TryDelete(tempPath);
                    await Task.Delay(Backoff(attempt), cancellationToken);
                }
            }

            throw new IOException($"文件接收在 {MaxAttempts} 次尝试后仍未成功", lastError);
        }
        finally { TryDelete(tempPath); }
    }

    private static bool IsTransient(HttpStatusCode status) => status is HttpStatusCode.RequestTimeout
        or HttpStatusCode.TooManyRequests or HttpStatusCode.InternalServerError
        or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    private static bool IsRetryable(Exception error)
    {
        if (error is IOException) return true;
        if (error is not HttpRequestException httpError) return false;
        return httpError.StatusCode is null || IsTransient(httpError.StatusCode.Value);
    }

    private static TimeSpan GetRetryDelay(HttpResponseMessage response, int attempt)
    {
        var retryAfter = response.Headers.RetryAfter;
        var requested = retryAfter?.Delta ?? (retryAfter?.Date - DateTimeOffset.UtcNow);
        return requested is { } delay && delay > TimeSpan.Zero
            ? TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds, 10))
            : Backoff(attempt);
    }

    private static TimeSpan Backoff(int attempt) => TimeSpan.FromMilliseconds(250 * Math.Pow(2, attempt - 1));

    private static string AddQuery(string url, string key, string value) =>
        $"{url}{(url.Contains('?') ? '&' : '?')}{Uri.EscapeDataString(key)}={Uri.EscapeDataString(value)}";

    private static string BuildTransferKey(string destination, FileInfo file, string sha256)
    {
        var identity = $"{destination}\n{file.FullName}\n{file.Length}\n{file.LastWriteTimeUtc.Ticks}\n{sha256}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private sealed record UploadMetadata(string TransferId, string Name, long Size, string Sha256);
    private sealed record UploadStatus(long Offset, bool Completed);

    private static class OutgoingTransferJournal
    {
        private static readonly object Sync = new();
        private static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nearlink");
        private static readonly string JournalPath = Path.Combine(Root, "outgoing-transfers.json");
        private static Dictionary<string, JournalItem>? _items;

        public static string GetOrCreate(string key)
        {
            lock (Sync)
            {
                var items = Load();
                if (items.TryGetValue(key, out var existing))
                {
                    items[key] = existing with { UpdatedUtc = DateTimeOffset.UtcNow };
                    Save(items);
                    return existing.TransferId;
                }
                var transferId = Guid.NewGuid().ToString("N");
                items[key] = new JournalItem(transferId, DateTimeOffset.UtcNow);
                Save(items);
                return transferId;
            }
        }

        public static void Remove(string key, string transferId)
        {
            lock (Sync)
            {
                var items = Load();
                if (items.TryGetValue(key, out var existing) && existing.TransferId == transferId)
                {
                    items.Remove(key);
                    try { Save(items); } catch { }
                }
            }
        }

        private static Dictionary<string, JournalItem> Load()
        {
            if (_items is not null) return _items;
            try
            {
                _items = File.Exists(JournalPath)
                    ? JsonSerializer.Deserialize<Dictionary<string, JournalItem>>(File.ReadAllText(JournalPath)) ?? []
                    : [];
            }
            catch { _items = []; }
            var expiry = DateTimeOffset.UtcNow.AddDays(-30);
            foreach (var key in _items.Where(pair => pair.Value.UpdatedUtc < expiry).Select(pair => pair.Key).ToArray())
                _items.Remove(key);
            return _items;
        }

        private static void Save(Dictionary<string, JournalItem> items)
        {
            Directory.CreateDirectory(Root);
            var tempPath = Path.Combine(Root, $".outgoing-transfers-{Guid.NewGuid():N}.tmp");
            try
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(items);
                using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    16 * 1024, FileOptions.WriteThrough))
                {
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(tempPath, JournalPath, overwrite: true);
                _items = items;
            }
            finally { try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { } }
        }

        private sealed record JournalItem(string TransferId, DateTimeOffset UpdatedUtc);
    }
}
