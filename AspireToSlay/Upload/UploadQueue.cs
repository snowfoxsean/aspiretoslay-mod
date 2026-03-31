using System.Text.Json;
using System.Text.Json.Serialization;
using AspireToSlay.Config;

namespace AspireToSlay.Upload;

/// <summary>
/// Represents one pending upload in the local queue file.
/// Keyed on the dedup key (<see cref="DedupKey"/>) so the same .run file
/// is never enqueued twice.
/// </summary>
internal sealed class QueueEntry
{
    // ── Run identity ───────────────────────────────────────────────────────
    [JsonPropertyName("dedup_key")]    public string DedupKey    { get; set; } = "";
    [JsonPropertyName("file_path")]    public string FilePath    { get; set; } = "";
    [JsonPropertyName("filename")]     public string Filename    { get; set; } = "";
    [JsonPropertyName("steam_id")]     public string SteamId     { get; set; } = "";
    [JsonPropertyName("profile_id")]   public string ProfileId   { get; set; } = "";
    [JsonPropertyName("modded")]       public bool   Modded      { get; set; }
    [JsonPropertyName("content_hash")] public string ContentHash { get; set; } = "";
    [JsonPropertyName("content_length")] public long ContentLength { get; set; }
    [JsonPropertyName("mod_version")]  public string ModVersion  { get; set; } = ModConstants.ModVersion;

    // ── Retry state ────────────────────────────────────────────────────────
    [JsonPropertyName("attempts")]           public int      Attempts          { get; set; }
    [JsonPropertyName("next_attempt_after")] public DateTime NextAttemptAfter  { get; set; } = DateTime.UtcNow;
    [JsonPropertyName("queued_at")]          public DateTime QueuedAt          { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Thread-safe persistent upload queue backed by a local JSON file.
/// Entries survive app/game restarts and are retried with exponential backoff.
/// </summary>
internal sealed class UploadQueue
{
    private readonly string _path;
    private readonly object _lock = new();
    private List<QueueEntry> _entries = [];

    private static readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public UploadQueue()
    {
        _path = ModConfig.QueueFilePath;
        Load();
    }

    // ── Public API ─────────────────────────────────────────────────────────

    public void Enqueue(QueueEntry entry)
    {
        lock (_lock)
        {
            // Deduplicate by dedup_key
            _entries.RemoveAll(e => e.DedupKey == entry.DedupKey);
            _entries.Add(entry);
            Save();
        }
    }

    /// <summary>Returns entries whose <c>NextAttemptAfter</c> is due and have remaining attempts.</summary>
    public List<QueueEntry> GetDue()
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            return [.. _entries.Where(e =>
                e.Attempts < ModConstants.MaxUploadAttempts &&
                e.NextAttemptAfter <= now)];
        }
    }

    public void MarkSuccess(string dedupKey)
    {
        lock (_lock)
        {
            _entries.RemoveAll(e => e.DedupKey == dedupKey);
            Save();
        }
    }

    public void MarkFailed(string dedupKey)
    {
        lock (_lock)
        {
            var entry = _entries.FirstOrDefault(e => e.DedupKey == dedupKey);
            if (entry is null) return;

            entry.Attempts++;
            if (entry.Attempts >= ModConstants.MaxUploadAttempts)
            {
                MainFile.Logger.Warn(
                    $"[Queue] {dedupKey} exhausted {ModConstants.MaxUploadAttempts} attempts — giving up.");
                _entries.Remove(entry);
            }
            else
            {
                var delay = ModConstants.RetryDelays[
                    Math.Min(entry.Attempts, ModConstants.RetryDelays.Length - 1)];
                entry.NextAttemptAfter = DateTime.UtcNow + delay;
                MainFile.Logger.Info(
                    $"[Queue] {dedupKey} attempt {entry.Attempts} failed; retry in {delay.TotalSeconds}s.");
            }
            Save();
        }
    }

    public int PendingCount
    {
        get { lock (_lock) return _entries.Count; }
    }

    // ── Persistence ────────────────────────────────────────────────────────

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var json = File.ReadAllText(_path);
            _entries = JsonSerializer.Deserialize<List<QueueEntry>>(json, _json) ?? [];
            // Prune entries whose source files have disappeared
            _entries.RemoveAll(e => !File.Exists(e.FilePath));
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"[Queue] Load error: {ex.Message}");
            _entries = [];
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_entries, _json));
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"[Queue] Save error: {ex.Message}");
        }
    }
}
