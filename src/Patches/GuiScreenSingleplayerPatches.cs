using System;
using BetterMods.Profiles;
using HarmonyLib;
using Vintagestory.Client;

namespace BetterMods.Patches
{
    /// <summary>
    /// Appends the world's bound profile and recorded mod list to the singleplayer save list's hover
    /// tooltip.
    /// <para>
    /// <c>private string GuiScreenSingleplayer.getHoverText(SaveGame savegame)</c> (verified on the
    /// live 1.22.5 and 1.23.0 <c>VintagestoryLib.dll</c>) builds the tooltip string; <c>LoadSaveGameCells</c>
    /// hangs it on the row's <c>SavegameCellEntry.HoverText</c> and <c>InitGui</c> bakes it into an
    /// <c>AddHoverText(..., "hover-" + i)</c>. Appending to the return value is therefore the whole
    /// job, and it is done from a POSTFIX so the vanilla text is produced exactly as before.
    /// </para>
    /// <para>
    /// The method is handed the <c>SaveGame</c> itself, so <c>SavegameIdentifier</c> is available
    /// directly — none of the reflection over the private, internally-typed
    /// <c>GuiScreenSingleplayer.entries</c> array that annotating the rows any other way would need.
    /// </para>
    /// <para>
    /// Vanilla's <c>getHoverText</c> has been seen to throw on a malformed save, in which case this
    /// postfix simply never runs and the row degrades exactly as it does without BetterMods. What this
    /// class must not do is add a SECOND way for the save list to fail, hence the catch-all: on any
    /// error the original string is returned untouched.
    /// </para>
    /// </summary>
    [HarmonyPatch(typeof(GuiScreenSingleplayer), "getHoverText")]
    public static class GuiScreenSingleplayer_getHoverText_Patch
    {
        [HarmonyPrepare]
        public static bool Prepare()
        {
            return PatchTarget.Exists(typeof(GuiScreenSingleplayer), "getHoverText", new[] { typeof(SaveGame) });
        }

        [HarmonyPostfix]
        public static void Postfix(GuiScreenSingleplayer __instance, SaveGame savegame, ref string __result)
        {
            try
            {
                // GuiScreen.ScreenManager is a public field; it is only needed for the "would the
                // active profile still enable these" line, which is skipped when it is unavailable.
                string appended = SavegameHoverText.Build(__instance?.ScreenManager, savegame);
                if (string.IsNullOrEmpty(appended)) return;

                __result = (__result ?? "") + "\n\n" + appended;
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning(
                    "[BetterMods] Could not annotate a savegame tooltip; leaving it as vanilla built it: " + e.Message);
            }
        }
    }
}
