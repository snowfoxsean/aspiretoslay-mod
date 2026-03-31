using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using AppConfig = AspireToSlay.Config.ModConfig;

namespace AspireToSlay.Patches;

/// <summary>
/// Message types that can be shown on the main menu, in descending priority.
/// Only the highest-priority message is displayed at any time.
/// </summary>
internal enum WarningPriority
{
    None            = 0,
    UpdateAvailable = 1,
    TokenMissing    = 2,
    TokenInvalid    = 3,
    RequiresUpdate  = 4,
}

/// <summary>
/// Injects a warning/info label into the main menu.
/// Supports a priority hierarchy — only the highest-priority pending message
/// is rendered.  The label is dismissed when the user enters a run
/// (MapScreen._Ready).
///
/// Message priorities (highest first):
///   requires_update  — mod too old, uploads blocked
///   token_invalid    — token revoked/bogus
///   token_missing    — no token configured
///   update_available — newer version exists (informational)
/// </summary>
[HarmonyPatch(typeof(NMainMenu), nameof(NMainMenu._Ready))]
internal static class MainMenuWarningPatch
{
    private const string LabelNodeName = "AspireToSlayWarning";

    // Strong reference to the live label so we can free it without a scene-tree search.
    private static Label? _activeLabel;

    // ── Pending message state ──────────────────────────────────────────────

    private static WarningPriority _pendingPriority = WarningPriority.None;
    private static string?         _pendingMessage;

    /// <summary>
    /// Sets a warning message to be shown on the next main menu render.
    /// If a higher-priority message is already pending, this call is ignored.
    /// </summary>
    public static void SetWarning(WarningPriority priority, string message)
    {
        if (priority > _pendingPriority)
        {
            _pendingPriority = priority;
            _pendingMessage  = message;
        }
    }

    /// <summary>
    /// Removes the warning label immediately. Safe to call from anywhere; no-op if already gone.
    /// </summary>
    public static void DismissWarning()
    {
        if (_activeLabel != null && GodotObject.IsInstanceValid(_activeLabel))
            _activeLabel.QueueFree();
        _activeLabel = null;
    }

    /// <summary>
    /// Returns the current pending priority (used by MainFile to decide
    /// whether uploads should be blocked).
    /// </summary>
    public static WarningPriority CurrentPriority => _pendingPriority;

    [HarmonyPostfix]
    public static void Postfix(NMainMenu __instance)
    {
        // Clean up any previous label (e.g. player returned to main menu)
        DismissWarning();

        // If no explicit message was set, check for token-missing as fallback
        if (_pendingPriority == WarningPriority.None)
        {
            if (string.IsNullOrEmpty(AppConfig.LoadToken()))
            {
                _pendingPriority = WarningPriority.TokenMissing;
                _pendingMessage  = "AspireToSlay: Token not configured. See www.aspiretoslay.com/GettingStarted";
            }
        }

        if (_pendingPriority == WarningPriority.None || _pendingMessage is null)
            return;

        var color = _pendingPriority >= WarningPriority.TokenInvalid
            ? new Color(1f, 0.25f, 0.25f)   // red for errors
            : new Color(1f, 0.85f, 0.2f);   // yellow for info

        var label = new Label
        {
            Name             = LabelNodeName,
            Text             = _pendingMessage,
            AutowrapMode     = TextServer.AutowrapMode.Off,
            MouseFilter      = Control.MouseFilterEnum.Ignore,

            // Anchor bottom-right
            AnchorLeft       = 1f,
            AnchorTop        = 1f,
            AnchorRight      = 1f,
            AnchorBottom     = 1f,
            GrowHorizontal   = Control.GrowDirection.Begin,
            GrowVertical     = Control.GrowDirection.Begin,

            // Sit above the game's "N mods loaded" line (~24 px per line + 6 px margin)
            OffsetRight      = -8f,
            OffsetBottom     = -30f,
        };

        label.AddThemeColorOverride("font_color", color);

        __instance.AddChild(label);
        _activeLabel = label;
    }
}

/// <summary>
/// Dismisses the warning when the map screen opens (i.e. the player has entered a run).
/// </summary>
[HarmonyPatch(typeof(NMapScreen), nameof(NMapScreen._Ready))]
internal static class MapScreenPatch
{
    [HarmonyPostfix]
    public static void Postfix() => MainMenuWarningPatch.DismissWarning();
}
