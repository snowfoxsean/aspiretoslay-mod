using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;
using AspireToSlay.Config;
using AspireToSlay.Patches;
using AspireToSlay.Upload;

namespace AspireToSlay;

/// <summary>
/// Mod entry point.  The <see cref="ModInitializerAttribute"/> tells the game
/// loader to call <see cref="Initialize"/> when this mod is loaded.
/// </summary>
[ModInitializer(nameof(Initialize))]
public partial class MainFile : Node
{
    public const string ModId = ModConstants.ModId;

    public static MegaCrit.Sts2.Core.Logging.Logger Logger { get; } =
        new(ModId, MegaCrit.Sts2.Core.Logging.LogType.Generic);

    /// <summary>
    /// When true, uploads are blocked because the mod version is below
    /// <c>MOD_MIN_VERSION</c> on the server.
    /// Checked by <see cref="UploadManager"/> before processing the queue.
    /// </summary>
    public static bool UploadsBlocked { get; private set; }

    public static void Initialize()
    {
        Logger.Info($"[{ModId}] Initialising v{ModConstants.ModVersion}");

        // Register Harmony patches (settings panel, warnings, run-end upload)
        var harmony = new Harmony(ModId);
        harmony.PatchAll();

        Logger.Info($"[{ModId}] Harmony patches applied.");

        // Run startup checks (token + version validation) then scan & upload.
        // Fire-and-forget — the checks set warning state that the main menu
        // Harmony patch will read on the next NMainMenu._Ready.
        _ = RunStartupAsync();

        Logger.Info($"[{ModId}] Ready.");
    }

    private static async Task RunStartupAsync()
    {
        using var api = new ApiClient();
        var token = ModConfig.LoadToken();

        // ── 1. Version validation ──────────────────────────────────────────
        try
        {
            var versionResult = await api.CheckModVersionAsync();
            if (versionResult is not null)
            {
                switch (versionResult.Status)
                {
                    case "requires_update":
                        UploadsBlocked = true;
                        MainMenuWarningPatch.SetWarning(
                            WarningPriority.RequiresUpdate,
                            versionResult.Message ?? "AspireToSlay: please download newest version at AspireToSlay.com");
                        Logger.Warn($"[{ModId}] Version {ModConstants.ModVersion} is below minimum — uploads blocked.");
                        break;

                    case "update_available":
                        MainMenuWarningPatch.SetWarning(
                            WarningPriority.UpdateAvailable,
                            versionResult.Message ?? "AspireToSlay: new version available at AspireToSlay.com");
                        Logger.Info($"[{ModId}] Newer version available.");
                        break;

                    case "ok":
                        Logger.Info($"[{ModId}] Mod version is current.");
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"[{ModId}] Version check failed: {ex.Message}");
        }

        // ── 2. Token validation ────────────────────────────────────────────
        if (token is { Length: > 0 })
        {
            try
            {
                var tokenResult = await api.ValidateTokenAsync(token);
                if (tokenResult is not null)
                {
                    switch (tokenResult.Status)
                    {
                        case "valid":
                            Logger.Info($"[{ModId}] Token is valid.");
                            break;

                        case "refreshed":
                            if (tokenResult.Token is { Length: > 0 })
                            {
                                ModConfig.SaveToken(tokenResult.Token);
                                Logger.Info($"[{ModId}] Token refreshed and saved.");
                            }
                            break;

                        case "invalid":
                            MainMenuWarningPatch.SetWarning(
                                WarningPriority.TokenInvalid,
                                "AspireToSlay: Token invalid. Go to AspireToSlay.com to get a new token.");
                            Logger.Warn($"[{ModId}] Token is invalid — user must re-authenticate.");
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[{ModId}] Token validation failed: {ex.Message}");
            }
        }
        // If no token, the MainMenuWarningPatch will show "token missing" automatically.

        // ── 3. Refresh the warning label on the live main menu ────────────
        // The NMainMenu._Ready patch fires when the scene loads, but startup
        // checks are async — by the time they complete the main menu is already
        // visible and _Ready has already run.  Calling RefreshWarning() here
        // pushes the label into the live scene tree without waiting for _Ready.
        MainMenuWarningPatch.RefreshWarning();

        // ── 4. Scan and upload (unless blocked) ────────────────────────────
        if (!UploadsBlocked)
        {
            RunTracker.Instance.ScanAndUpload();
        }
        else
        {
            Logger.Warn($"[{ModId}] Uploads blocked — skipping scan.");
        }
    }
}
