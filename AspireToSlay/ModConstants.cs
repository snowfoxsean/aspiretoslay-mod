namespace AspireToSlay;

/// <summary>
/// Shared compile-time constants for the mod.
/// </summary>
internal static class ModConstants
{
    public const string ModId = "AspireToSlay";
    public const string ModVersion = "0.9.1";

    // Token storage file name (inside the mod folder)
    public const string TokenFileName = "token.txt";

    // Local record of already-uploaded run dedup keys, one per line
    public const string UploadedFileName = "uploaded.txt";

    // Upload queue manifest
    public const string QueueFileName = "upload_queue.json";

    // Retry settings
    public const int MaxUploadAttempts = 5;
    public static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(60),
        TimeSpan.FromSeconds(300),
        TimeSpan.FromSeconds(900),
    ];

    // Steam / game save directories to scan for *.run files
    // Relative segments appended to the base saves root:
    //   <base>/steam/<steamId>/<profileNum>/saves/history/          (vanilla)
    //   <base>/steam/<steamId>/modded/<profileNum>/saves/history/   (modded)
    public const string SteamSavesSubdir  = "steam";
    public const string ModdedSavesSubdir = "modded";
    public const string HistorySubdir     = "saves/history";
    public const string RunFileExtension  = "*.run";
}
