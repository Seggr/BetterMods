using System;
using System.Collections.Generic;
using BetterMods.Gui;
using BetterMods.Playstyles;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.Client;
using Vintagestory.Common;

namespace BetterMods.Patches
{
    // Custom playstyles on Create New World ("Accepted feature creep"). Every target below was checked
    // against the live 1.23.0 VintagestoryLib.dll IL. Each class guards its own target, so a missing
    // one leaves that piece - and, through CustomPlaystyles.FeatureArmed, the whole feature where it
    // matters - vanilla.

    /// <summary>
    /// Appends the custom playstyles to a NEW-WORLD <see cref="WorldConfig"/>'s list, after vanilla's
    /// sort. Gated on <c>IsNewWorld</c>, which only <c>GuiScreenSingleplayerNewWorld</c>'s constructor
    /// sets: the world-modify screen, the server and <c>ModContainer.WorldConfig.PlayStyles</c> (the
    /// server, <c>WorldAPI</c>, the <c>--playStyle</c> autostart and the world list all read that) never
    /// see a custom entry. Covers the customize screen too, which reloads its own clone's list.
    /// </summary>
    [HarmonyPatch(typeof(WorldConfig), "LoadPlayStyles")]
    public static class WorldConfig_LoadPlayStyles_Patch
    {
        [HarmonyPrepare]
        public static bool Prepare() => PatchTarget.Exists(typeof(WorldConfig), "LoadPlayStyles", Type.EmptyTypes);

        [HarmonyPostfix]
        public static void Postfix(WorldConfig __instance)
        {
            if (__instance == null || !__instance.IsNewWorld) return;
            CustomPlaystyles.AppendTo(__instance);
        }
    }

    /// <summary>
    /// <c>Clone()</c> builds <c>new WorldConfig(mods)</c> - whose constructor loads the list while
    /// <c>IsNewWorld</c> is still false - and copies <c>IsNewWorld</c> afterwards. Reload so a
    /// new-world clone (what the customize screen works on) carries the custom entries from the start.
    /// </summary>
    [HarmonyPatch(typeof(WorldConfig), "Clone")]
    public static class WorldConfig_Clone_Patch
    {
        [HarmonyPrepare]
        public static bool Prepare() => PatchTarget.Exists(typeof(WorldConfig), "Clone", Type.EmptyTypes);

        [HarmonyPostfix]
        public static void Postfix(WorldConfig __result)
        {
            try
            {
                if (__result != null && __result.IsNewWorld) __result.LoadPlayStyles();
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not reload a cloned world config's playstyles: " + e.Message);
            }
        }
    }

    /// <summary>
    /// The prefix keeps the cached screen's playstyle INDEX valid for the rebuilt list (and refreshes
    /// the library); the postfix adds the Save / Delete buttons. One class, so both are in or neither.
    /// </summary>
    [HarmonyPatch(typeof(GuiScreenSingleplayerNewWorld), "InitGui")]
    public static class GuiScreenSingleplayerNewWorld_InitGui_Patch
    {
        [HarmonyPrepare]
        public static bool Prepare() => PatchTarget.Exists(typeof(GuiScreenSingleplayerNewWorld), "InitGui", Type.EmptyTypes);

        [HarmonyPrefix]
        public static void Prefix(GuiScreenSingleplayerNewWorld __instance)
        {
            NewWorldPlaystyleUi.BeforeInitGui(__instance);
        }

        [HarmonyPostfix]
        public static void Postfix(GuiScreenSingleplayerNewWorld __instance)
        {
            NewWorldPlaystyleUi.AfterInitGui(__instance);
        }
    }

    /// <summary>Names, descriptions and tags for the custom rows of the new-world list.</summary>
    [HarmonyPatch(typeof(GuiScreenSingleplayerNewWorld), "loadPlaystyleCells")]
    public static class GuiScreenSingleplayerNewWorld_loadPlaystyleCells_Patch
    {
        [HarmonyPrepare]
        public static bool Prepare() => PatchTarget.Exists(typeof(GuiScreenSingleplayerNewWorld), "loadPlaystyleCells", Type.EmptyTypes);

        [HarmonyPostfix]
        public static void Postfix(List<PlaystyleListEntry> __result)
        {
            NewWorldPlaystyleUi.DressNewWorldCells(__result);
        }
    }

    /// <summary>
    /// Refuses clicks on unusable entries and applies / clears a preset's settings and height when the
    /// selection moves onto or off one; the postfix updates the Delete button.
    /// </summary>
    [HarmonyPatch(typeof(GuiScreenSingleplayerNewWorld), "OnClickCellLeft")]
    public static class GuiScreenSingleplayerNewWorld_OnClickCellLeft_Patch
    {
        [HarmonyPrepare]
        public static bool Prepare() => PatchTarget.Exists(typeof(GuiScreenSingleplayerNewWorld), "OnClickCellLeft", new[] { typeof(int) });

        [HarmonyPrefix]
        public static bool Prefix(GuiScreenSingleplayerNewWorld __instance, int cellIndex)
        {
            return NewWorldPlaystyleUi.BeforeCellClick(__instance, cellIndex);
        }

        [HarmonyPostfix]
        public static void Postfix(GuiScreenSingleplayerNewWorld __instance)
        {
            NewWorldPlaystyleUi.UpdateButtons(__instance);
        }
    }

    /// <summary>
    /// A custom entry's tooltip on the new-world list gets one extra line naming the settings that were
    /// skipped (missing mods, unknown keys, invalid values) - the row itself stays clean. The new-world
    /// screen is the only caller of <c>ToRichText</c> (InitGui and OnClickCellLeft); signature checked
    /// against the live 1.23 VintagestoryLib.dll: <c>string ToRichText(PlayStyle playstyle, bool withCustomConfigs)</c>.
    /// </summary>
    [HarmonyPatch(typeof(WorldConfig), nameof(WorldConfig.ToRichText), new[] { typeof(PlayStyle), typeof(bool) })]
    public static class WorldConfig_ToRichText_Patch
    {
        [HarmonyPrepare]
        public static bool Prepare() => PatchTarget.Exists(typeof(WorldConfig), nameof(WorldConfig.ToRichText), new[] { typeof(PlayStyle), typeof(bool) });

        // __0, not the parameter's name, so a rename in a game update cannot break the binding.
        [HarmonyPostfix]
        public static void Postfix(PlayStyle __0, ref string __result)
        {
            NewWorldPlaystyleUi.AppendTooltipNotes(__0, ref __result);
        }
    }

    /// <summary>The customize screen's playstyle dropdown: custom names, unusable entries left out.</summary>
    [HarmonyPatch(typeof(GuiScreenWorldCustomize), "loadPlaystyleCells")]
    public static class GuiScreenWorldCustomize_loadPlaystyleCells_Patch
    {
        [HarmonyPrepare]
        public static bool Prepare() => PatchTarget.Exists(typeof(GuiScreenWorldCustomize), "loadPlaystyleCells", Type.EmptyTypes);

        [HarmonyPostfix]
        public static void Postfix(List<PlaystyleListEntry> __result)
        {
            NewWorldPlaystyleUi.DressCustomizeCells(__result);
        }
    }

    /// <summary>Picking a preset in the customize screen's dropdown applies its world height.</summary>
    [HarmonyPatch(typeof(GuiScreenWorldCustomize), "onPlayStyleChanged")]
    public static class GuiScreenWorldCustomize_onPlayStyleChanged_Patch
    {
        [HarmonyPrepare]
        public static bool Prepare() => PatchTarget.Exists(typeof(GuiScreenWorldCustomize), "onPlayStyleChanged", new[] { typeof(string), typeof(bool) });

        [HarmonyPrefix]
        public static void Prefix(GuiScreenWorldCustomize __instance, string code)
        {
            NewWorldPlaystyleUi.BeforeCustomizePlaystyleChanged(__instance, code);
        }
    }
}
