using System.Text.Json;
using System.Security.Cryptography;

namespace Nearlink.Native;

/// <summary>
/// Owns durable transfer history and file storage. Writes are serialized and committed by
/// same-volume rename so a crash cannot expose a partial file or partially-written index.
/// </summary>
public sealed class TransferStore
{
    private const int CurrentSchema = 3;
    private const long JournalCompactThreshold = 1024 * 1024;
    private const long MaxFileSize = 1024L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _root;
    private readonly string _indexPath;
    private readonly string _journalPath;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly object _stateLock = new();
    private List<Entry> _history = [];

    public TransferStore(string root)
    {
        _root = root;
        _indexPath = Path.Combine(root, "index.json");
        _journalPath = Path.Combine(root, "messages.jsonl");
    }

    public void Initialize()
    {
        Directory.CreateDirectory(_root);
        LoadIndex();
        foreach (var pattern in new[] { ".upload-*", ".index-*.tmp", ".journal-*.tmp", ".resume-*.tmp" })
        {
            foreach (var pending in Directory.EnumerateFiles(_root, pattern))
                try { File.Delete(pending); } catch { }
        }
        foreach (var manifest in Directory.EnumerateFiles(_root, ".resume-*.json"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(manifest) < DateTime.UtcNow.AddDays(-7))
                {
                    File.Delete(manifest);
                    var transferId = Path.GetFileName(manifest)[8..^5];
                    TryDelete(Path.Combine(_root, $".resume-{transferId}.part"));
                }
            }
            catch { }
        }
        RepairJournalTail();
        ReplayJournal();
    }

    public IReadOnlyList<Entry> GetHistory()
    {
        lock (_stateLock) return _history.ToArray();
    }

    public bool TryGetFile(string id, out Entry? entry)
    {
        entry = null;
        if (!IsSafeId(id)) return false;
        lock (_stateLock)
            entry = _history.LastOrDefault(item => item.Kind == "file" && item.Id == id);
        return entry is not null && File.Exists(GetFilePath(id));
    }

    public string GetFilePath(string id)
    {
        if (!IsSafeId(id)) throw new ArgumentException("Invalid file id", nameof(id));
        return Path.Combine(_root, id);
    }

    public async Task<ResumableUploadState> GetResumableUploadStateAsync(ResumableUpload upload,
        CancellationToken cancellationToken = default)
    {
        ValidateResumableUpload(upload);
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            if (TryGetCompletedUpload(upload, out var completed))
                return new ResumableUploadState(upload.Size, completed!);
            var manifestPath = GetResumeManifestPath(upload.TransferId);
            var partPath = GetResumePartPath(upload.TransferId);
            if (File.Exists(manifestPath))
            {
                var stored = await ReadResumeManifestAsync(manifestPath, cancellationToken);
                EnsureSameUpload(stored, upload);
                if (!File.Exists(partPath) && await TryRecoverMovedUploadAsync(upload, manifestPath, cancellationToken))
                    return new ResumableUploadState(upload.Size, FindCompletedUpload(upload)!);
            }
            else
            {
                TryDelete(partPath);
                await WriteResumeManifestAsync(upload, manifestPath, cancellationToken);
            }
            if (!File.Exists(manifestPath))
                await WriteResumeManifestAsync(upload, manifestPath, cancellationToken);
            var offset = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
            if (offset > upload.Size) throw new InvalidDataException("断点数据超出文件大小");
            return new ResumableUploadState(offset, null);
        }
        finally { _writeLock.Release(); }
    }

    public async Task<long> AppendResumableChunkAsync(ResumableUpload upload, long offset,
        ReadOnlyMemory<byte> chunk, CancellationToken cancellationToken = default)
    {
        ValidateResumableUpload(upload);
        if (chunk.Length is <= 0 or > 4 * 1024 * 1024) throw new InvalidDataException("数据块大小无效");
        if (offset < 0 || offset + chunk.Length > upload.Size) throw new InvalidDataException("数据块位置超出文件范围");

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            if (TryGetCompletedUpload(upload, out _)) return upload.Size;
            var manifestPath = GetResumeManifestPath(upload.TransferId);
            if (!File.Exists(manifestPath)) throw new InvalidDataException("未初始化此文件的断点传输");
            EnsureSameUpload(await ReadResumeManifestAsync(manifestPath, cancellationToken), upload);
            var partPath = GetResumePartPath(upload.TransferId);
            var currentOffset = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;

            if (offset < currentOffset)
            {
                if (offset + chunk.Length > currentOffset)
                    throw new InvalidDataException("数据块与已确认断点重叠");
                await using var existing = new FileStream(partPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    64 * 1024, FileOptions.Asynchronous | FileOptions.RandomAccess);
                existing.Position = offset;
                var comparison = new byte[chunk.Length];
                await existing.ReadExactlyAsync(comparison, cancellationToken);
                if (!comparison.AsSpan().SequenceEqual(chunk.Span))
                    throw new InvalidDataException("重试数据块与已保存内容不一致");
                return currentOffset;
            }
            if (offset != currentOffset) throw new InvalidDataException("数据块未按顺序到达");

            await using (var output = new FileStream(partPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                output.Position = currentOffset;
                await output.WriteAsync(chunk, cancellationToken);
                await output.FlushAsync(cancellationToken);
                output.Flush(flushToDisk: true);
            }
            try { File.SetLastWriteTimeUtc(manifestPath, DateTime.UtcNow); } catch { }
            return currentOffset + chunk.Length;
        }
        finally { _writeLock.Release(); }
    }

    public async Task<StoreResult> CompleteResumableUploadAsync(ResumableUpload upload,
        CancellationToken cancellationToken = default)
    {
        ValidateResumableUpload(upload);
        var partPath = GetResumePartPath(upload.TransferId);
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            if (TryGetCompletedUpload(upload, out var completed)) return new StoreResult(completed!, Created: false);
            var manifestPath = GetResumeManifestPath(upload.TransferId);
            if (!File.Exists(manifestPath)) throw new FileNotFoundException("找不到此文件的断点数据");
            EnsureSameUpload(await ReadResumeManifestAsync(manifestPath, cancellationToken), upload);
            var length = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
            if (length != upload.Size) throw new InvalidDataException("文件尚未全部接收");
            string actualHash;
            await using (var source = new FileStream(partPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                actualHash = Convert.ToHexString(await SHA256.HashDataAsync(source, cancellationToken));
            if (!string.Equals(actualHash, upload.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("文件完整性校验失败，断点数据已清除");

            var safeName = CleanFileName(upload.Name);
            var extension = Path.GetExtension(safeName);
            if (extension.Length > 16) extension = extension[..16];
            var id = $"{upload.TransferId}{extension}";
            var finalPath = GetFilePath(id);
            var item = new Entry(id, "file", upload.From, upload.FromId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                null, safeName, length, upload.ContentType, actualHash, upload.TransferId);
            File.Move(partPath, finalPath);
            try
            {
                await AddEntryCoreAsync(item, cancellationToken);
                TryDelete(manifestPath);
                return new StoreResult(item, Created: true);
            }
            catch
            {
                try { if (File.Exists(finalPath)) File.Move(finalPath, partPath, overwrite: true); } catch { }
                throw;
            }
        }
        catch (InvalidDataException ex) when (ex.Message.Contains("完整性校验失败", StringComparison.Ordinal))
        {
            TryDelete(partPath);
            TryDelete(GetResumeManifestPath(upload.TransferId));
            throw;
        }
        finally { _writeLock.Release(); }
    }

    private bool TryGetCompletedUpload(ResumableUpload upload, out Entry? entry)
    {
        lock (_stateLock) entry = _history.LastOrDefault(item => item.TransferId == upload.TransferId);
        if (entry is null) return false;
        EnsureSameUpload(entry, upload);
        return true;
    }

    private Entry? FindCompletedUpload(ResumableUpload upload)
    {
        lock (_stateLock) return _history.LastOrDefault(item => item.TransferId == upload.TransferId);
    }

    private async Task<bool> TryRecoverMovedUploadAsync(ResumableUpload upload, string manifestPath, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(CleanFileName(upload.Name));
        if (extension.Length > 16) extension = extension[..16];
        var id = $"{upload.TransferId}{extension}";
        var finalPath = GetFilePath(id);
        if (!File.Exists(finalPath)) return false;

        var info = new FileInfo(finalPath);
        string hash;
        await using (var file = new FileStream(finalPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            hash = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken));
        if (info.Length != upload.Size || !string.Equals(hash, upload.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(finalPath);
            TryDelete(manifestPath);
            return false;
        }

        var item = new Entry(id, "file", upload.From, upload.FromId, new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds(),
            null, CleanFileName(upload.Name), upload.Size, upload.ContentType, hash, upload.TransferId);
        await AddEntryCoreAsync(item, cancellationToken);
        TryDelete(manifestPath);
        return true;
    }

    private static void EnsureSameUpload(ResumableUpload existing, ResumableUpload requested)
    {
        if (existing.TransferId != requested.TransferId || existing.FromId != requested.FromId || existing.From != requested.From ||
            existing.Name != requested.Name || existing.Size != requested.Size ||
            !string.Equals(existing.Sha256, requested.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("传输编号已用于其他文件");
    }

    private static void EnsureSameUpload(Entry existing, ResumableUpload requested)
    {
        if (existing.FromId != requested.FromId || existing.From != requested.From || existing.Name != requested.Name ||
            existing.Size != requested.Size || !string.Equals(existing.Sha256, requested.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("传输编号已用于其他文件");
    }

    private static void ValidateResumableUpload(ResumableUpload upload)
    {
        if (!Guid.TryParseExact(upload.TransferId, "N", out _) || upload.Size <= 0 || upload.Size > MaxFileSize ||
            upload.Sha256.Length != 64 || !upload.Sha256.All(Uri.IsHexDigit) ||
            string.IsNullOrWhiteSpace(upload.Name) || string.IsNullOrWhiteSpace(upload.From) || string.IsNullOrWhiteSpace(upload.FromId))
            throw new InvalidDataException("断点传输信息无效");
    }

    private string GetResumePartPath(string transferId) => Path.Combine(_root, $".resume-{transferId}.part");
    private string GetResumeManifestPath(string transferId) => Path.Combine(_root, $".resume-{transferId}.json");

    private async Task WriteResumeManifestAsync(ResumableUpload upload, string path, CancellationToken cancellationToken)
    {
        var tempPath = Path.Combine(_root, $".resume-{upload.TransferId}-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await JsonSerializer.SerializeAsync(stream, upload, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            File.Move(tempPath, path, overwrite: true);
        }
        finally { TryDelete(tempPath); }
    }

    private static async Task<ResumableUpload> ReadResumeManifestAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<ResumableUpload>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("断点传输记录损坏");
    }

    public async Task<Entry> AddMessageAsync(string text, string from, string fromId, CancellationToken cancellationToken = default)
    {
        var item = new Entry(Guid.NewGuid().ToString("N"), "message", from, fromId,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), text, null, 0, null);
        await AppendMessageAsync(item, cancellationToken);
        return item;
    }

    public async Task<StoreResult> StoreFileAsync(Stream source, string originalName, long declaredLength,
        string from, string fromId, string? contentType, string? transferId = null, string? expectedSha256 = null,
        CancellationToken cancellationToken = default)
    {
        if (declaredLength <= 0 || declaredLength > MaxFileSize)
            throw new InvalidDataException("单个文件必须大于 0 且不能超过 1 GB");
        if (!string.IsNullOrWhiteSpace(transferId) && !Guid.TryParseExact(transferId, "N", out _))
            throw new InvalidDataException("传输编号无效");
        if (!string.IsNullOrWhiteSpace(expectedSha256) &&
            (expectedSha256.Length != 64 || !expectedSha256.All(Uri.IsHexDigit)))
            throw new InvalidDataException("文件校验值无效");

        var safeName = CleanFileName(originalName);
        var extension = Path.GetExtension(safeName);
        if (extension.Length > 16) extension = extension[..16];
        var id = $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{Guid.NewGuid():N}{extension}";
        var finalPath = GetFilePath(id);
        var tempPath = Path.Combine(_root, $".upload-{Guid.NewGuid():N}");
        long actualLength = 0;

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            if (!string.IsNullOrWhiteSpace(transferId))
            {
                Entry? existing;
                lock (_stateLock) existing = _history.LastOrDefault(item => item.TransferId == transferId);
                if (existing is not null)
                {
                    if (existing.FromId != fromId || existing.From != from || existing.Name != safeName || existing.Size != declaredLength ||
                        !string.IsNullOrWhiteSpace(expectedSha256) && !string.Equals(existing.Sha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("传输编号已用于其他文件");
                    return new StoreResult(existing, Created: false);
                }
            }

            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[128 * 1024];
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    actualLength += read;
                    if (actualLength > MaxFileSize || actualLength > declaredLength)
                        throw new InvalidDataException("上传内容超过声明大小或 1 GB 限制");
                    hasher.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
                await output.FlushAsync(cancellationToken);
                output.Flush(flushToDisk: true);
            }

            if (actualLength != declaredLength) throw new EndOfStreamException("上传内容长度与声明不一致");
            var actualHash = Convert.ToHexString(hasher.GetHashAndReset());
            if (!string.IsNullOrWhiteSpace(expectedSha256) &&
                !string.Equals(actualHash, expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("上传文件校验失败");
            File.Move(tempPath, finalPath);
            var item = new Entry(id, "file", from, fromId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                null, safeName, actualLength, string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType,
                actualHash, transferId);
            await AddEntryCoreAsync(item, cancellationToken);
            return new StoreResult(item, Created: true);
        }
        catch
        {
            TryDelete(tempPath);
            TryDelete(finalPath);
            throw;
        }
        finally { _writeLock.Release(); }
    }

    private async Task AppendMessageAsync(Entry item, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await AppendJournalEntryAsync(item, cancellationToken);
            lock (_stateLock) _history = [.. _history, item];
            try
            {
                if (new FileInfo(_journalPath).Length >= JournalCompactThreshold)
                {
                    await WriteIndexAsync(GetHistorySnapshot(), cancellationToken);
                    await ResetJournalAsync(cancellationToken);
                }
            }
            catch { /* The flushed journal remains the durable source if compaction fails. */ }
        }
        finally { _writeLock.Release(); }
    }

    private async Task AddEntryCoreAsync(Entry item, CancellationToken cancellationToken)
    {
        List<Entry> before;
        List<Entry> updated;
        lock (_stateLock)
        {
            before = _history;
            updated = [.. before, item];
            _history = updated;
        }

        try
        {
            await WriteIndexAsync(updated, cancellationToken);
            try { await ResetJournalAsync(cancellationToken); } catch { }
        }
        catch
        {
            lock (_stateLock) _history = before;
            throw;
        }
    }

    private async Task WriteIndexAsync(List<Entry> entries, CancellationToken cancellationToken)
    {
        var tempPath = Path.Combine(_root, $".index-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                32 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await JsonSerializer.SerializeAsync(stream, new IndexDocument(CurrentSchema, entries), JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            File.Move(tempPath, _indexPath, overwrite: true);
        }
        finally { TryDelete(tempPath); }
    }

    private async Task AppendJournalEntryAsync(Entry item, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(item, JsonOptions);
        var previousLength = File.Exists(_journalPath) ? new FileInfo(_journalPath).Length : 0;
        try
        {
            await using var stream = new FileStream(_journalPath, FileMode.Append, FileAccess.Write, FileShare.Read,
                16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await stream.WriteAsync(bytes, cancellationToken);
            await stream.WriteAsync("\n"u8.ToArray(), cancellationToken);
            await stream.FlushAsync(cancellationToken);
            stream.Flush(flushToDisk: true);
        }
        catch
        {
            try
            {
                await using var stream = new FileStream(_journalPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
                stream.SetLength(previousLength);
                stream.Flush(flushToDisk: true);
            }
            catch { }
            throw;
        }
    }

    private async Task ResetJournalAsync(CancellationToken cancellationToken)
    {
        var tempPath = Path.Combine(_root, $".journal-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous))
            {
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            File.Move(tempPath, _journalPath, overwrite: true);
        }
        finally { TryDelete(tempPath); }
    }

    private List<Entry> GetHistorySnapshot()
    {
        lock (_stateLock) return [.. _history];
    }

    private void RepairJournalTail()
    {
        if (!File.Exists(_journalPath)) return;
        try
        {
            using var stream = new FileStream(_journalPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            var lastByte = -1;
            for (long index = stream.Length - 1; index >= 0; index--)
            {
                stream.Position = index;
                lastByte = stream.ReadByte();
                if (lastByte == '\n') { stream.SetLength(index + 1); return; }
            }
            stream.SetLength(0);
        }
        catch { }
    }

    private void ReplayJournal()
    {
        if (!File.Exists(_journalPath)) return;
        try
        {
            var known = new HashSet<string>(GetHistorySnapshot().Select(item => item.Id), StringComparer.Ordinal);
            foreach (var line in File.ReadLines(_journalPath))
            {
                try
                {
                    var item = JsonSerializer.Deserialize<Entry>(line, JsonOptions);
                    if (item is not null && item.Kind == "message" && !string.IsNullOrWhiteSpace(item.Text) && known.Add(item.Id))
                        lock (_stateLock) _history.Add(item);
                }
                catch { }
            }
            lock (_stateLock) _history = _history.OrderBy(item => item.Time).ToList();
        }
        catch { }
    }

    private void LoadIndex()
    {
        if (!File.Exists(_indexPath)) return;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(_indexPath));
            List<Entry> entries;
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                // Migrate the original file-only index format.
                entries = (JsonSerializer.Deserialize<List<LegacyFileRecord>>(document.RootElement.GetRawText(), JsonOptions) ?? [])
                    .Where(file => IsSafeId(file.Id) && File.Exists(GetFilePath(file.Id)))
                    .Select(file => new Entry(file.Id, "file", file.From, file.FromId, file.Time, null,
                        CleanFileName(file.Name), file.Size, file.ContentType)).ToList();
            }
            else
            {
                var index = document.RootElement.Deserialize<IndexDocument>(JsonOptions);
                entries = index is { Schema: >= 1 and <= CurrentSchema } ? index.Items ?? [] : [];
                entries = entries.Where(IsValidEntry).ToList();
            }
            lock (_stateLock) _history = entries.OrderBy(item => item.Time).ToList();
        }
        catch
        {
            // A corrupt index must not prevent the receiver from starting; intact files remain on disk.
            lock (_stateLock) _history = [];
        }
    }

    private bool IsValidEntry(Entry item) => item is not null
        && !string.IsNullOrWhiteSpace(item.Id)
        && (item.Kind == "message" || item.Kind == "file" && IsSafeId(item.Id) && File.Exists(GetFilePath(item.Id)))
        && (item.Kind != "message" || !string.IsNullOrWhiteSpace(item.Text));

    private static bool IsSafeId(string? id) => !string.IsNullOrWhiteSpace(id)
        && id.Length <= 255 && Path.GetFileName(id) == id && id is not "." and not ".."
        && !id.Contains(Path.DirectorySeparatorChar) && !id.Contains(Path.AltDirectorySeparatorChar);

    private static string CleanFileName(string? name)
    {
        var value = (name ?? "文件").Replace('\\', '/');
        value = value[(value.LastIndexOf('/') + 1)..];
        value = new string(value.Where(ch => !char.IsControl(ch) && ch is not '<' and not '>').ToArray()).Trim();
        if (string.IsNullOrWhiteSpace(value)) value = "文件";
        return value.Length > 240 ? value[..240] : value;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private sealed class IndexDocument
    {
        public int Schema { get; set; }
        public List<Entry> Items { get; set; } = [];
        public IndexDocument() { }
        public IndexDocument(int schema, List<Entry> items) { Schema = schema; Items = items; }
    }

    private sealed class LegacyFileRecord
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public long Size { get; set; }
        public long Time { get; set; }
        public string From { get; set; } = "";
        public string FromId { get; set; } = "";
        public string ContentType { get; set; } = "application/octet-stream";
    }

    public sealed record StoreResult(Entry Entry, bool Created);
    public sealed record ResumableUpload(string TransferId, string Name, string From, string FromId,
        long Size, string Sha256, string ContentType);
    public sealed record ResumableUploadState(long Offset, Entry? Completed);

    public sealed record Entry(string Id, string Kind, string From, string FromId, long Time,
        string? Text, string? Name, long Size, string? ContentType, string? Sha256 = null, string? TransferId = null);
}
