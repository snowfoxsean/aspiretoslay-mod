using System.IO.Compression;
using System.Security.Cryptography;
using AspireToSlay.Config;

namespace AspireToSlay.Upload;

/// <summary>
/// Orchestrates uploading native <c>.run</c> files to the backend.
///
/// Flow per file:
///   1. Gzip-compress the .run file to a temp path.
///   2. Compute SHA-256 of the compressed bytes.
///   3. POST /upload-grants → get presigned S3 URL.
///      If the server returns <c>skipped=true</c> the file is already known —
///      mark it done locally without uploading to S3.
///   4. PUT gzipped bytes to S3.
///   5. Record the dedup key in <c>uploaded.txt</c> and remove from queue.
///
/// Rate limiting (Option D):
///   At most one run is uploaded every <see cref="UploadIntervalMs"/> ms.
///   This keeps worker S3 ETag conflicts rare even when hundreds of historic
///   runs are queued on first install (500 runs ≈ 40 minutes to drain).
///
/// Backup URL cycling:
///   On upload-grant failure the ApiClient flips between main and backup URLs.
///   Try main → fail → try backup → fail → try main → …
/// </summary>
internal sealed class UploadManager : IDisposable
{
    private readonly UploadQueue _queue   = new();
    private readonly ApiClient   _api     = new();
    private readonly SemaphoreSlim _sem   = new(1, 1);
    private bool _disposed;
    private string? _currentToken;

    /// <summary>
    /// Minimum gap between consecutive uploads.
    /// Worker serialisation is now handled by a per-user DynamoDB lease, so
    /// the mod-side rate limit only needs to prevent hammering the ingest API.
    /// </summary>
    private const int UploadIntervalMs = 50;

    /// <summary>
    /// Wall-clock time after which the next upload is permitted.
    /// Guarded by <see cref="_sem"/> — only accessed inside the flush loop.
    /// </summary>
    private DateTime _nextUploadAllowedAt = DateTime.MinValue;

    // ── Public API ─────────────────────────────────────────────────────────

    /// <summary>
    /// Adds a discovered run file to the persistent queue without immediately
    /// flushing.  Call <see cref="FlushAsync"/> after all files are enqueued.
    /// </summary>
    public void EnqueueFile(RunFileInfo file, string modToken)
    {
        // Gzip + hash synchronously here so the queue entry is self-contained.
        string compressedPath;
        string contentHash;
        long   contentLength;

        try
        {
            (compressedPath, contentHash, contentLength) = CompressAndHash(file.FilePath);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"[Upload] Failed to compress {file.FileName}: {ex.Message}");
            return;
        }

        var entry = new QueueEntry
        {
            DedupKey      = file.DedupKey,
            FilePath      = compressedPath,   // path to the temp .gz file
            Filename      = file.FileName,
            SteamId       = file.SteamId,
            ProfileId     = file.ProfileNum,
            Modded        = file.Modded,
            ContentHash   = contentHash,
            ContentLength = contentLength,
            ModVersion    = ModConstants.ModVersion,
        };

        _queue.Enqueue(entry);
        MainFile.Logger.Info($"[Upload] Queued {file.FileName} ({contentLength:N0} bytes compressed).");
    }

    /// <summary>Attempt to upload all due queue entries.</summary>
    public void FlushAsync(string? modToken)
    {
        if (modToken is null) return;

        // Block uploads when the mod version is below minimum
        if (MainFile.UploadsBlocked)
        {
            MainFile.Logger.Warn("[Upload] Uploads blocked (mod version too old) — skipping flush.");
            return;
        }

        _ = FlushQueueAsync(modToken);
    }

    // ── Queue processing ───────────────────────────────────────────────────

    private async Task FlushQueueAsync(string modToken)
    {
        if (!await _sem.WaitAsync(0)) return;
        _currentToken = modToken;
        try
        {
            var due = _queue.GetDue();
            if (due.Count == 0) return;

            MainFile.Logger.Info($"[Upload] Processing {due.Count} pending upload(s).");
            foreach (var entry in due)
            {
                // ── Rate limit (Option D) ──────────────────────────────────
                var now  = DateTime.UtcNow;
                var wait = _nextUploadAllowedAt - now;
                if (wait > TimeSpan.Zero)
                {
                    MainFile.Logger.Info(
                        $"[Upload] Rate-limit: waiting {wait.TotalSeconds:F1}s before next upload.");
                    await Task.Delay(wait);
                }

                await ProcessEntryAsync(entry, _currentToken!);

                // Advance the cooldown window regardless of success/failure.
                _nextUploadAllowedAt = DateTime.UtcNow + TimeSpan.FromMilliseconds(UploadIntervalMs);
            }
        }
        finally
        {
            _sem.Release();
        }
    }

    private async Task ProcessEntryAsync(QueueEntry entry, string modToken)
    {
        if (!File.Exists(entry.FilePath))
        {
            MainFile.Logger.Warn($"[Upload] Compressed file missing for {entry.DedupKey} — removing from queue without marking uploaded.");
            _queue.MarkSuccess(entry.DedupKey);
            return;
        }

        // 1. Request presigned upload URL (or "skipped" if already uploaded)
        var grantReq = new UploadGrantRequest
        {
            SteamId       = entry.SteamId,
            ProfileId     = entry.ProfileId,
            Modded        = entry.Modded,
            Filename      = entry.Filename,
            ContentHash   = entry.ContentHash,
            ContentLength = entry.ContentLength,
            ModVersion    = entry.ModVersion,
        };
        var (grant, httpStatus) = await _api.RequestUploadGrantWithStatusAsync(modToken, grantReq);

        // Handle token expiry: attempt refresh and retry once
        if (httpStatus == 401 && grant is null)
        {
            if (await TryRefreshTokenAsync(modToken))
            {
                (grant, httpStatus) = await _api.RequestUploadGrantWithStatusAsync(_currentToken!, grantReq);
            }
        }

        // If the request failed (non-401 or post-refresh), flip to backup URL
        if (grant is null)
        {
            _api.FlipUrl();
            MainFile.Logger.Info("[Upload] Flipped to backup/main URL for next attempt.");
            _queue.MarkFailed(entry.DedupKey);
            return;
        }

        // Server says this run already exists — no need to re-upload
        if (grant.Skipped)
        {
            MainFile.Logger.Info($"[Upload] {entry.Filename} already on server — skipping S3 upload.");
            FinaliseSuccess(entry, _queue);
            return;
        }

        // 2. PUT gzipped bytes to S3
        var ok = await _api.UploadToS3Async(
            grant.UploadUrl,
            grant.RequiredHeaders,
            entry.FilePath);

        if (!ok)
        {
            _queue.MarkFailed(entry.DedupKey);
            return;
        }

        MainFile.Logger.Info($"[Upload] {entry.Filename} uploaded successfully (run={grant.RunId}).");
        FinaliseSuccess(entry, _queue);
    }

    private static void FinaliseSuccess(QueueEntry entry, UploadQueue queue)
    {
        // Persist the dedup key so we never try this file again
        UploadedSet.Add(entry.DedupKey);

        // Remove from the persistent queue
        queue.MarkSuccess(entry.DedupKey);

        // Clean up the temporary compressed file
        try { File.Delete(entry.FilePath); }
        catch { /* best-effort */ }
    }


    /// <summary>
    /// Attempts to refresh the mod token if the backend rejected it as expired.
    /// On success, persists the new token and updates the in-memory copy.
    /// </summary>
    private async Task<bool> TryRefreshTokenAsync(string currentToken, CancellationToken ct = default)
    {
        MainFile.Logger.Info("[Upload] Token expired — attempting refresh...");
        var newToken = await _api.RefreshTokenAsync(currentToken, ct);
        if (newToken is null)
        {
            MainFile.Logger.Error("[Upload] Token refresh failed — user must re-authenticate via website.");
            return false;
        }
        _currentToken = newToken;
        ModConfig.SaveToken(newToken);
        MainFile.Logger.Info("[Upload] Token refreshed and saved.");
        return true;
    }

    // ── Compression + hashing ──────────────────────────────────────────────

    /// <summary>
    /// Gzip-compresses <paramref name="sourcePath"/> to a temp file and returns
    /// <c>(compressedPath, sha256Hex, compressedLength)</c>.
    /// </summary>
    private static (string path, string sha256, long length) CompressAndHash(string sourcePath)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"ats_{Path.GetFileName(sourcePath)}.gz");

        using (var src  = File.OpenRead(sourcePath))
        using (var dst  = File.Create(tempPath))
        using (var gzip = new GZipStream(dst, CompressionLevel.Optimal, leaveOpen: false))
        {
            src.CopyTo(gzip);
        }

        // Compute SHA-256 of the compressed bytes
        using var sha = SHA256.Create();
        using var fs  = File.OpenRead(tempPath);
        var hash = sha.ComputeHash(fs);
        var hex  = Convert.ToHexString(hash).ToLowerInvariant();

        return (tempPath, hex, new FileInfo(tempPath).Length);
    }

    // ── IDisposable ────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sem.Dispose();
        _api.Dispose();
    }
}
