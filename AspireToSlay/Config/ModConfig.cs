using System.Reflection;

namespace AspireToSlay.Config;

/// <summary>
/// Persistent mod configuration.
/// Runtime files (token, queue, uploaded set) are stored in:
///   <c>~/Library/Application Support/SlayTheSpire2/AspireToSlay/</c>  (macOS)
///   <c>%APPDATA%\SlayTheSpire2\AspireToSlay\</c>                      (Windows)
/// i.e. alongside the game's own save data, NOT inside the mod folder.
/// The game's mod manager scans the entire mod folder tree for manifests,
/// so any JSON file placed there causes a deserialization crash.
/// </summary>
internal sealed class ModConfig
{
    private static readonly string DataDir = GetDataDir();
    private static readonly string TokenPath = Path.Combine(DataDir, ModConstants.TokenFileName);

    // ── Token ──────────────────────────────────────────────────────────

    /// <summary>Returns the stored mod JWT, or null if none has been set.</summary>
    public static string? LoadToken()
    {
        try
        {
            if (!File.Exists(TokenPath)) return null;
            var token = File.ReadAllText(TokenPath).Trim();
            return string.IsNullOrEmpty(token) ? null : token;
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"[Config] Failed to read token: {ex.Message}");
            return null;
        }
    }

    /// <summary>Persists a new mod JWT to disk.</summary>
    public static void SaveToken(string token)
    {
        try
        {
            EnsureDataDir();
            File.WriteAllText(TokenPath, token.Trim());
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"[Config] Failed to save token: {ex.Message}");
        }
    }

    /// <summary>Removes the stored token (e.g. on sign-out).</summary>
    public static void ClearToken()
    {
        try { if (File.Exists(TokenPath)) File.Delete(TokenPath); }
        catch { /* best-effort */ }
    }

    // ── Paths ──────────────────────────────────────────────────────────

    public static string DataDirectory => DataDir;

    public static string QueueFilePath => Path.Combine(DataDir, ModConstants.QueueFileName);

    /// <summary>
    /// Path to the flat text file that records dedup keys of already-uploaded runs
    /// (one key per line: <c>filename#steamId#profileNum#vanilla|modded</c>).
    /// </summary>
    public static string UploadedFilePath => Path.Combine(DataDir, ModConstants.UploadedFileName);

    /// <summary>
    /// Root directory where the game stores its save data.
    /// On all platforms this is the game's own app-data folder (not Godot's
    /// generic userdata path):
    ///   macOS:   ~/Library/Application Support/SlayTheSpire2
    ///   Windows: %APPDATA%\SlayTheSpire2
    ///   Linux:   ~/.local/share/SlayTheSpire2
    ///
    /// At runtime we ask Godot for the path directly via <c>OS.GetUserDataDir()</c>,
    /// which returns the correct game-specific folder.  The platform strings below
    /// are only used as a fallback if that call fails.
    /// </summary>
    public static string GameSavesRoot
    {
        get
        {
            // The mod runs inside the Godot process, so this call should always succeed.
            try
            {
                var userDir = Godot.OS.GetUserDataDir();
                if (!string.IsNullOrEmpty(userDir))
                    return userDir;
            }
            catch { /* fall through to platform fallback */ }

            // Platform fallbacks — Godot stores game userdata here by default,
            // but STS2 uses its own app name rather than the generic Godot path.
            var home = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
            const string GameFolderName = "SlayTheSpire2";

            return OperatingSystem.IsMacOS()
                ? Path.Combine(home, "Library", "Application Support", GameFolderName)
                : OperatingSystem.IsWindows()
                    ? Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        GameFolderName)
                    : Path.Combine(home, ".local", "share", GameFolderName);
        }
    }

    // ── Internals ──────────────────────────────────────────────────────

    private static string GetDataDir()
    {
        // Store runtime data inside the game's save-data directory, NOT inside
        // the mod folder.  The game's mod manager scans the entire mod folder
        // tree (including subdirectories) for mod manifests, so any JSON file
        // placed there causes a deserialization crash on startup.
        //
        // Target: <GameSavesRoot>/AspireToSlay/
        //   macOS:   ~/Library/Application Support/SlayTheSpire2/AspireToSlay/
        //   Windows: %APPDATA%\SlayTheSpire2\AspireToSlay\
        //   Linux:   ~/.local/share/SlayTheSpire2/AspireToSlay/
        try
        {
            var savesRoot = GameSavesRoot;
            if (!string.IsNullOrEmpty(savesRoot))
            {
                var dataDir = Path.Combine(savesRoot, ModConstants.ModId);
                // One-time migration from the old mod-folder data/ location
                try
                {
                    var asmPath = Assembly.GetExecutingAssembly().Location;
                    if (!string.IsNullOrEmpty(asmPath))
                    {
                        var modFolder = Path.GetDirectoryName(asmPath);
                        if (!string.IsNullOrEmpty(modFolder))
                            MigrateFilesToDataDir(Path.Combine(modFolder, "data"), dataDir);
                    }
                }
                catch { /* best-effort migration */ }
                return dataDir;
            }
        }
        catch { /* fall through */ }

        // Fallback: %APPDATA%\AspireToSlay
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, ModConstants.ModId);
    }

    /// <summary>
    /// One-time migration: move any runtime files from <paramref name="sourceDir"/>
    /// to <paramref name="destDir"/> if they don't already exist at the destination.
    /// Used to migrate from the old mod-folder <c>data/</c> location to the new
    /// Godot user-data location.
    /// </summary>
    private static void MigrateFilesToDataDir(string sourceDir, string destDir)
    {
        string[] fileNames =
        [
            ModConstants.TokenFileName,
            ModConstants.UploadedFileName,
            ModConstants.QueueFileName,
        ];

        foreach (var name in fileNames)
        {
            var oldPath = Path.Combine(sourceDir, name);
            var newPath = Path.Combine(destDir, name);
            try
            {
                if (File.Exists(oldPath) && !File.Exists(newPath))
                {
                    Directory.CreateDirectory(destDir);
                    File.Move(oldPath, newPath);
                }
            }
            catch { /* best-effort migration */ }
        }
    }

    private static void EnsureDataDir() => Directory.CreateDirectory(DataDir);
}
