using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.GameOverScreen;

namespace AspireToSlay.Patches;

/// <summary>
/// Fires an upload scan whenever the GameOver screen opens.
///
/// <para>
/// STS2 uses <see cref="NGameOverScreen"/> for every run conclusion —
/// victory, defeat, and abandonment.  The game writes the run's
/// <c>.run</c> history file <em>before</em> instantiating this screen,
/// so by the time <c>_Ready</c> fires the file is already on disk and
/// <see cref="RunTracker.Instance.ScanAndUpload"/> will find it.
/// </para>
/// </summary>
[HarmonyPatch(typeof(NGameOverScreen), nameof(NGameOverScreen._Ready))]
internal static class RunEndPatch
{
    [HarmonyPostfix]
    public static void Postfix()
    {
        MainFile.Logger.Info("[RunEndPatch] Run ended — triggering upload scan.");
        RunTracker.Instance.ScanAndUpload();
    }
}
