using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;
using AppConfig = AspireToSlay.Config.ModConfig;

namespace AspireToSlay.Patches;

// ── Inject the settings button when the info panel is first created ────────

[HarmonyPatch(typeof(NModInfoContainer), nameof(NModInfoContainer._Ready))]
internal static class ModInfoReadyPatch
{
    private const string ButtonNodeName = "AspireToSlaySettingsBtn";

    [HarmonyPostfix]
    public static void Postfix(NModInfoContainer __instance)
    {
        try
        {
            if (__instance.GetNodeOrNull<Button>(ButtonNodeName) != null) return;

            var btn = new Button
            {
                Name              = ButtonNodeName,
                Text              = "⚙ AspireToSlay Settings",
                MouseFilter       = Control.MouseFilterEnum.Stop,
                CustomMinimumSize = new Vector2(240, 40),
                // Anchor to bottom-left so the button sits at the bottom of the panel
                // regardless of the panel's actual height at _Ready time.
                AnchorLeft        = 0f,
                AnchorTop         = 1f,
                AnchorRight       = 0f,
                AnchorBottom      = 1f,
                GrowHorizontal    = Control.GrowDirection.End,
                GrowVertical      = Control.GrowDirection.Begin,
                OffsetTop         = -48f,   // 40px height + 8px margin
                OffsetBottom      = -8f,
                OffsetLeft        = 8f,
                OffsetRight       = 248f,   // 8 + 240
            };

            btn.Pressed += () => TokenPopup.Show(__instance);

            __instance.AddChild(btn);
            btn.Hide();
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"[ModInfoReadyPatch] Failed to inject settings button: {ex}");
        }
    }
}

// ── Show/hide the button whenever a row is selected ────────────────────────

[HarmonyPatch(typeof(NModInfoContainer), nameof(NModInfoContainer.Fill))]
internal static class ModInfoFillPatch
{
    private const string ButtonNodeName = "AspireToSlaySettingsBtn";

    [HarmonyPostfix]
    public static void Postfix(NModInfoContainer __instance, Mod mod)
    {
        try
        {
            var btn = __instance.GetNodeOrNull<Button>(ButtonNodeName);
            if (btn == null) return;

            bool isOurs = IsOurMod(mod);

            if (isOurs) btn.Show();
            else        btn.Hide();
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"[ModInfoFillPatch] Error in Postfix: {ex}");
        }
    }

    /// <summary>
    /// Determines whether <paramref name="mod"/> is the AspireToSlay mod.
    /// Uses reflection to check the loaded-state so the code compiles against
    /// any game version (the field was <c>bool wasLoaded</c> in older builds and
    /// <c>ModLoadState state</c> in a brief newer build).
    /// Identity check: <c>manifest.name</c> first, then <c>manifest.id</c> via
    /// reflection (present on newer builds that use JSON-manifest loading).
    /// </summary>
    private static bool IsOurMod(Mod mod)
    {
        if (!IsModLoaded(mod)) return false;

        var manifest = mod.manifest;
        if (manifest == null) return false;

        // `name` exists on all known game versions
        if (manifest.name == ModConstants.ModId) return true;

        // Fallback: check manifest.id via reflection (newer builds)
        try
        {
            var idField = manifest.GetType().GetField("id");
            if (idField != null && idField.GetValue(manifest) is string id && id == ModConstants.ModId)
                return true;
        }
        catch { /* reflection failed — not our mod */ }

        return false;
    }

    /// <summary>
    /// Returns true when <paramref name="mod"/> was successfully loaded by the
    /// game.  Handles both API shapes via reflection:
    ///   • <c>bool wasLoaded</c>  (current stable build)
    ///   • <c>ModLoadState state == Loaded</c>  (seen in a beta build)
    /// </summary>
    private static bool IsModLoaded(Mod mod)
    {
        var modType = mod.GetType();

        // Try bool wasLoaded first (current stable)
        var wasLoaded = modType.GetField("wasLoaded");
        if (wasLoaded != null && wasLoaded.FieldType == typeof(bool))
            return (bool)wasLoaded.GetValue(mod)!;

        // Try enum state field (seen in beta)
        var stateField = modType.GetField("state");
        if (stateField != null && stateField.FieldType.IsEnum)
        {
            var val = Convert.ToInt32(stateField.GetValue(mod));
            // ModLoadState.Loaded == 1
            return val == 1;
        }

        // Unknown shape — assume loaded if manifest is present
        return mod.manifest != null;
    }
}

// ── Token popup ────────────────────────────────────────────────────────────

internal static class TokenPopup
{
    private const string PopupName = "AspireToSlayTokenPopup";

    public static void Show(Node anchor)
    {
        var root = anchor.GetTree().Root;
        var existing = root.GetNodeOrNull<Window>(PopupName);
        if (existing != null) { existing.QueueFree(); return; }

        var popup = new PopupPanel { Name = PopupName };

        var margin = new MarginContainer();
        foreach (var side in new[] { "margin_top", "margin_bottom", "margin_left", "margin_right" })
            margin.AddThemeConstantOverride(side, 14);
        margin.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        popup.AddChild(margin);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 10);
        margin.AddChild(vbox);

        vbox.AddChild(new Label { Text = "AspireToSlay — Mod Token" });

        bool hasToken = AppConfig.LoadToken() is { Length: > 0 };
        var statusLabel = new Label
        {
            Text         = hasToken ? "Token saved. Paste a new one to replace it."
                                    : "Sign in at aspiretoslay.com/settings, then paste your token below.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        vbox.AddChild(statusLabel);

        var input = new LineEdit
        {
            PlaceholderText     = hasToken ? "(token saved)" : "Paste token here…",
            Secret              = true,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        vbox.AddChild(input);

        var hbox = new HBoxContainer();
        hbox.AddThemeConstantOverride("separation", 8);
        vbox.AddChild(hbox);

        var saveBtn   = new Button { Text = "Save"   };
        var cancelBtn = new Button { Text = "Cancel" };
        hbox.AddChild(saveBtn);
        hbox.AddChild(cancelBtn);

        void Commit()
        {
            var t = input.Text.Trim();
            if (string.IsNullOrEmpty(t)) { popup.QueueFree(); return; }
            AppConfig.SaveToken(t);
            statusLabel.Text = "✓ Token saved!";
            popup.QueueFree();

            // Dismiss the warning label in the main menu (if visible)
            MainMenuWarningPatch.DismissWarning();
        }

        saveBtn.Pressed     += Commit;
        input.TextSubmitted += _ => Commit();
        cancelBtn.Pressed   += popup.QueueFree;

        root.AddChild(popup);
        popup.PopupCentered(new Vector2I(480, 200));
        input.GrabFocus();
    }
}
