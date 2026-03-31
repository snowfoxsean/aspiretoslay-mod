using System.Text.Json;
using AspireToSlay.Upload;
using AspireToSlay.Config;

namespace AspireToSlay;

/// <summary>
/// Scans the game's native save history directories for <c>*.run</c> files and
/// uploads any that haven't been sent to the backend yet.
///
/// Called once at mod start-up.  Each <c>*.run</c> file is identified by a
/// dedup key:  <c>&lt;filename&gt;#&lt;steamId&gt;#&lt;profileNum&gt;#vanilla|modded</c>
/// Keys of successfully uploaded runs are persisted to <c>uploaded.txt</c> so
/// the mod never retries the same file.
/// </summary>
internal sealed class RunTracker
{
    // ── Singleton ──────────────────────────────────────────────────────────

    private static RunTracker? _instance;
    public static RunTracker Instance => _instance ??= new RunTracker();

    private readonly UploadManager _uploader = new();

    // ── Entry point ────────────────────────────────────────────────────────

    public void ScanAndUpload()
    {
        var token = ModConfig.LoadToken();
        if (token is null)
        {
            MainFile.Logger.Info("[Scanner] No mod token configured — skipping scan.");
            return;
        }

        var savesRoot = ModConfig.GameSavesRoot;
        if (!Directory.Exists(savesRoot))
        {
            MainFile.Logger.Warn($"[Scanner] Game saves root not found: {savesRoot}");
            return;
        }

        var uploaded = UploadedSet.Load();
        var found = DiscoverRunFiles(savesRoot);

        int queued = 0;
        int skippedInProgress = 0;
        foreach (var file in found)
        {
            if (uploaded.Contains(file.DedupKey)) continue;

            if (!IsRunFinished(file.FilePath))
            {
                skippedInProgress++;
                continue;
            }

            _uploader.EnqueueFile(file, token);
            queued++;
        }

        MainFile.Logger.Info($"[Scanner] Found {found.Count} run file(s); queued {queued} new upload(s)" +
                             (skippedInProgress > 0 ? $"; skipped {skippedInProgress} in-progress run(s)." : "."));
        _uploader.FlushAsync(token);
    }

    // ── Discovery ──────────────────────────────────────────────────────────

    /// <summary>
    /// Scans for all <c>*.run</c> files under:
    ///   &lt;savesRoot&gt;/steam/&lt;steamId&gt;/&lt;profileNum&gt;/saves/history/   (vanilla)
    ///   &lt;savesRoot&gt;/steam/&lt;steamId&gt;/modded/&lt;profileNum&gt;/saves/history/  (modded)
    /// </summary>
    private static List<RunFileInfo> DiscoverRunFiles(string savesRoot)
    {
        var results = new List<RunFileInfo>();

        // ── steam/<steamId> subtree ────────────────────────────────────────
        // vanilla: steam/<steamId>/<profileNum>/saves/history/
        // modded:  steam/<steamId>/modded/<profileNum>/saves/history/
        var steamRoot = Path.Combine(savesRoot, ModConstants.SteamSavesSubdir);
        if (Directory.Exists(steamRoot))
        {
            foreach (var steamIdDir in SafeEnumDirs(steamRoot))
            {
                var steamId = Path.GetFileName(steamIdDir);

                // vanilla profiles sit directly under <steamId>/
                foreach (var profileDir in SafeEnumDirs(steamIdDir))
                {
                    // Skip the "modded" subdirectory — handled below
                    if (string.Equals(Path.GetFileName(profileDir),
                                      ModConstants.ModdedSavesSubdir,
                                      StringComparison.OrdinalIgnoreCase))
                        continue;

                    var profileNum = Path.GetFileName(profileDir);
                    var historyDir = Path.Combine(profileDir, "saves", "history");
                    foreach (var file in SafeEnumFiles(historyDir, "*.run"))
                    {
                        results.Add(new RunFileInfo(
                            FilePath:   file,
                            FileName:   Path.GetFileName(file),
                            SteamId:    steamId,
                            ProfileNum: profileNum,
                            Modded:     false));
                    }
                }

                // modded profiles sit under <steamId>/modded/<profileNum>/
                var moddedUnderSteam = Path.Combine(steamIdDir, ModConstants.ModdedSavesSubdir);
                foreach (var profileDir in SafeEnumDirs(moddedUnderSteam))
                {
                    var profileNum = Path.GetFileName(profileDir);
                    var historyDir = Path.Combine(profileDir, "saves", "history");
                    foreach (var file in SafeEnumFiles(historyDir, "*.run"))
                    {
                        results.Add(new RunFileInfo(
                            FilePath:   file,
                            FileName:   Path.GetFileName(file),
                            SteamId:    steamId,
                            ProfileNum: profileNum,
                            Modded:     true));
                    }
                }
            }
        }

        return results;
    }

    // ── Run completion check ───────────────────────────────────────────────

    /// <summary>
    /// Returns <c>true</c> if the .run file represents a finished run —
    /// i.e. the player won, was abandoned, or died (killed by encounter/event).
    ///
    /// Runs that are still in progress have all four fields at their default
    /// values (<c>win=false</c>, <c>was_abandoned=false</c>,
    /// <c>killed_by_encounter="NONE.NONE"</c>, <c>killed_by_event="NONE.NONE"</c>).
    /// We skip those to avoid uploading partial data.
    /// </summary>
    private static bool IsRunFinished(string filePath)
    {
        try
        {
            var json = File.ReadAllText(filePath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("win", out var win) && win.GetBoolean())
                return true;

            if (root.TryGetProperty("was_abandoned", out var abandoned) && abandoned.GetBoolean())
                return true;

            if (root.TryGetProperty("killed_by_encounter", out var kbe))
            {
                var val = kbe.GetString();
                if (!string.IsNullOrEmpty(val) && val != "NONE.NONE")
                    return true;
            }

            if (root.TryGetProperty("killed_by_event", out var kbev))
            {
                var val = kbev.GetString();
                if (!string.IsNullOrEmpty(val) && val != "NONE.NONE")
                    return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Scanner] Could not check completion for {Path.GetFileName(filePath)}: {ex.Message}");
            // If we can't read the file, skip it — don't upload potentially corrupt data.
            return false;
        }
    }

    // ── Safe I/O helpers ───────────────────────────────────────────────────

    private static IEnumerable<string> SafeEnumDirs(string path)
    {
        try { return Directory.Exists(path) ? Directory.EnumerateDirectories(path) : []; }
        catch { return []; }
    }

    private static IEnumerable<string> SafeEnumFiles(string path, string pattern)
    {
        try { return Directory.Exists(path) ? Directory.EnumerateFiles(path, pattern) : []; }
        catch { return []; }
    }
}

// ── Value objects ──────────────────────────────────────────────────────────

/// <summary>Describes a single <c>.run</c> file discovered on disk.</summary>
internal sealed record RunFileInfo(
    string FilePath,
    string FileName,
    string SteamId,
    string ProfileNum,
    bool   Modded)
{
    /// <summary>
    /// Stable dedup key used to avoid re-uploading: <c>filename#steamId#profileNum#vanilla|modded</c>
    /// </summary>
    public string DedupKey =>
        $"{FileName}#{SteamId}#{ProfileNum}#{(Modded ? "modded" : "vanilla")}";
}

// ── Uploaded set ───────────────────────────────────────────────────────────

/// <summary>
/// Persistent set of dedup keys for runs that have already been uploaded
/// successfully.  Stored as a plain-text file (one key per line) so it
/// survives app restarts without the overhead of JSON parsing.
/// </summary>
internal static class UploadedSet
{
    private static readonly object _lock = new();
    private static HashSet<string>? _cache;

    public static HashSet<string> Load()
    {
        lock (_lock)
        {
            if (_cache is not null) return _cache;
            try
            {
                var path = ModConfig.UploadedFilePath;
                _cache = File.Exists(path)
                    ? new HashSet<string>(File.ReadAllLines(path), StringComparer.Ordinal)
                    : [];
            }
            catch { _cache = []; }
            return _cache;
        }
    }

    public static void Add(string dedupKey)
    {
        lock (_lock)
        {
            var set = Load();
            if (!set.Add(dedupKey)) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ModConfig.UploadedFilePath)!);
                File.AppendAllText(ModConfig.UploadedFilePath, dedupKey + Environment.NewLine);

                // Compact the file when it grows beyond 100 entries.
                // We only keep the entry with the smallest filename (earliest timestamp)
                // as a "min watermark" — the scanner will never encounter filenames
                // below this watermark, so we don't need to store them individually.
                // All entries above the watermark are kept so the scanner can skip them.
                if (set.Count > 100)
                {
                    Compact(set);
                }
            }
            catch (Exception ex)
            {
                MainFile.Logger.Error($"[UploadedSet] Failed to persist key: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Rewrites uploaded.txt keeping only entries whose filename component
    /// (the part before the first '#') is at or above the minimum filename
    /// across all entries.  Since filenames are Unix timestamps (e.g.
    /// "1773176021.run"), the minimum is the oldest run — anything older
    /// can never appear again.
    /// </summary>
    private static void Compact(HashSet<string> set)
    {
        try
        {
            // Find the entry with the smallest filename (first segment before '#')
            string? minFilename = null;
            foreach (var key in set)
            {
                var filename = key.Split('#')[0];
                if (minFilename is null || string.Compare(filename, minFilename, StringComparison.Ordinal) < 0)
                    minFilename = filename;
            }

            if (minFilename is null) return;

            // Rewrite the file with all current entries.  The in-memory set
            // is already de-duped, so this is a clean compaction.
            var lines = new List<string>(set.Count);
            foreach (var key in set)
                lines.Add(key);
            lines.Sort(StringComparer.Ordinal);

            File.WriteAllLines(ModConfig.UploadedFilePath, lines);
            MainFile.Logger.Info($"[UploadedSet] Compacted to {lines.Count} entries (min filename: {minFilename}).");
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"[UploadedSet] Compaction failed: {ex.Message}");
        }
    }
}
