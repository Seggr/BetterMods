using BetterMods.Profiles;
using HarmonyLib;
using Vintagestory.Client;
using Vintagestory.Common;

namespace BetterMods.Patches
{
    /// <summary>
    /// Gates every singleplayer world start on the profile the savegame is bound to.
    /// <para>
    /// <c>ConnectToSingleplayer</c> is public and is the single funnel for all six start paths, so one
    /// prefix covers the normal start, the corrupt-save repair, the database upgrade, both routes out
    /// of the world-modify screen, the mod-download reconnect and the command-line auto-start.
    /// </para>
    /// <para>
    /// Returning false swallows the start and puts the mismatch prompt on screen; the prompt re-enters
    /// this same method once the player has answered. Any failure inside the gate returns true, so a
    /// broken binding degrades to vanilla rather than trapping the player in the menu.
    /// </para>
    /// </summary>
    [HarmonyPatch(typeof(ScreenManager), "ConnectToSingleplayer")]
    public static class ScreenManager_ConnectToSingleplayer_Patch
    {
        [HarmonyPrepare]
        public static bool Prepare() => PatchTarget.Exists(typeof(ScreenManager), "ConnectToSingleplayer", new[] { typeof(StartServerArgs) });

        [HarmonyPrefix]
        public static bool Prefix(ScreenManager __instance, StartServerArgs serverargs)
        {
            return SavegameProfileGate.OnConnectToSingleplayer(__instance, serverargs);
        }
    }
}
