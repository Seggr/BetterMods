using System;
using System.Collections.Generic;
using System.Reflection;
using BetterMods.Gui;
using BetterMods.Profiles;
using BetterMods.Updates;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;
using Vintagestory.Common;

namespace BetterMods.Patches
{
    /// <summary>
    /// Shared guard for the mods-screen patches.
    /// <para>
    /// Every patch class resolves its target through here from a <c>[HarmonyPrepare]</c> hook.
    /// Returning false from Prepare makes Harmony skip that one patch, which is what we want when a
    /// future game build renames a METHOD: one broken feature instead of a main menu that will not
    /// open.
    /// </para>
    /// <para>
    /// That is all <c>[HarmonyPrepare]</c> buys, and it is only half the story. A renamed or moved
    /// TYPE throws <c>TypeLoadException</c> out of Harmony's class processor before <c>Prepare()</c>
    /// can be found, let alone run. Keeping THAT to one patch class is
    /// <c>PatchBootstrap.PatchEachClassSeparately</c>'s job, not this one's: it replaces
    /// <c>PatchAll(assembly)</c> with a per-class loop, each in its own try.
    /// </para>
    /// </summary>
    internal static class PatchTarget
    {
        public static bool Exists(string methodName, Type[] parameters = null)
        {
            return Exists(typeof(GuiScreenMods), methodName, parameters);
        }

        public static bool Exists(Type type, string methodName, Type[] parameters = null)
        {
            try
            {
                MethodInfo method = AccessTools.Method(type, methodName, parameters);
                if (method != null) return true;
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Error("[BetterMods] Looking up " + type.Name + "." + methodName + " failed: " + e.Message);
                return false;
            }

            BetterModsModSystem.Logger?.Error("[BetterMods] " + type.Name + "." + methodName + " not found on this game version; that patch is skipped.");
            return false;
        }
    }

    /// <summary>Adds the profile tab row and the Apply Profile button to the composed screen.</summary>
    [HarmonyPatch(typeof(GuiScreenMods), "InitGui")]
    public static class GuiScreenMods_InitGui_Patch
    {
        [HarmonyPrepare]
        public static bool Prepare() => PatchTarget.Exists("InitGui");

        [HarmonyPostfix]
        public static void Postfix(GuiScreenMods __instance)
        {
            // Decorate() has its own try/catch and degrades to the vanilla screen.
            ModsScreenProfileUi.Decorate(__instance);
        }
    }

    /// <summary>
    /// Makes the cell on/off switches show the VIEWED profile rather than <c>ClientSettings</c>, and
    /// collapses the rows vanilla produced per INSTALLED FILE into one row per mod id.
    /// <para>
    /// Rewriting the result list is the whole job: vanilla walks <c>ScreenManager.allMods</c> and
    /// emits a cell for every folder/zip/dll it found, so two installed versions of one mod are two
    /// rows. We keep the row belonging to the version the profile selected and drop its siblings,
    /// which leaves vanilla's own title, detail and error text untouched on the surviving row.
    /// </para>
    /// </summary>
    [HarmonyPatch(typeof(GuiScreenMods), "LoadModCells")]
    public static class GuiScreenMods_LoadModCells_Patch
    {
        [HarmonyPrepare]
        public static bool Prepare() => PatchTarget.Exists("LoadModCells");

        [HarmonyPostfix]
        public static void Postfix(GuiScreenMods __instance, List<ModCellEntry> __result)
        {
            try
            {
                if (__result == null) return;

                List<ModContainer> mods = new List<ModContainer>();
                foreach (ModCellEntry cell in __result)
                {
                    if (cell?.Mod?.Info != null) mods.Add(cell.Mod);
                }

                List<ModVersionGroup> groups = ModGrouping.Build(mods, ModsScreenProfileUi.ViewedProfileId);
                ModsScreenProfileUi.SetGroups(groups);

                Dictionary<ModContainer, ModVersionGroup> selected = new Dictionary<ModContainer, ModVersionGroup>();
                HashSet<ModContainer> grouped = new HashSet<ModContainer>();
                foreach (ModVersionGroup group in groups)
                {
                    if (group.Selected != null) selected[group.Selected] = group;
                    foreach (ModContainer version in group.Versions) grouped.Add(version);

                    // Two identical modid@version copies on disk are one choice and must be one row.
                    // The duplicate is not in Versions, so without this its row would fall through to
                    // the "not one of ours" branch below and the player would see the mod twice, once
                    // showing profile state and once showing ClientSettings.
                    foreach (ModContainer duplicate in group.Duplicates) grouped.Add(duplicate);
                }

                List<ModCellEntry> kept = new List<ModCellEntry>();
                foreach (ModCellEntry cell in __result)
                {
                    ModContainer mod = cell?.Mod;

                    // Vanilla builds its key from Info?.ModID + "@" + Info?.Version; a mod with no
                    // usable id could never be matched by a profile, so leave its answer alone. It
                    // still gets the card's layout and tooltip - just no group to read updates from.
                    if (mod == null || !grouped.Contains(mod))
                    {
                        if (cell != null) ModsScreenProfileUi.DressCard(__instance, cell, null);
                        kept.Add(cell);
                        continue;
                    }

                    if (!selected.TryGetValue(mod, out ModVersionGroup group)) continue;

                    cell.Enabled = group.Enabled;

                    // Only touched when there is a choice - several installed versions, or a mod DB
                    // mod with releases to offer - so any other row stays byte for byte the row
                    // vanilla built. The label itself is still just the version - the mark is what
                    // earns the row a drawn dropdown trigger in GuiElementModCell.
                    if (VersionMenuSession.HasMenu(group))
                    {
                        // Elided, because the trigger box is sized around this string and an opaque
                        // box as wide as an exotic version string is a slab over the whole row.
                        cell.RightTopText = ModCellLayout.ElideVersionLabel(group.VersionLabel, cell.DetailTextFont);
                        ModCellLayout.MarkVersionPicker(cell);
                    }

                    // The card's tooltip, buttons and update marker (or, where the card cannot be
                    // drawn, vanilla detail lines) for the group's cached state. Read from the session
                    // cache only - never a request.
                    ModsScreenProfileUi.DressCard(__instance, cell, group);

                    kept.Add(cell);
                }

                __result.Clear();
                __result.AddRange(kept);
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Error("[BetterMods] Could not apply profile state to the mod list.");
                BetterModsModSystem.Logger?.Error(e);
            }
        }
    }

    /// <summary>
    /// Routes a left-half click on a mod card: first to the card's button row
    /// (<see cref="ModCard.TryClick"/> - Open mod page, Update to x), then to the version picker on a
    /// multi-version row.
    /// <para>
    /// The picker itself is a composer element added outside the list's clip
    /// (<see cref="GuiElementVersionMenu"/>); all this does is decide that the click was on the
    /// version and hand it the row to anchor to.
    /// </para>
    /// <para>
    /// The version area sits left of the point where <c>GuiElementModCell.OnMouseUpOnElement</c>
    /// splits a row, so this can never shadow the on/off switch.
    /// </para>
    /// <para>
    /// On 1.22 this was a prefix on <c>GuiScreenMods.OnClickCellLeft</c>, an empty stub vanilla
    /// wired to every row's <c>OnMouseDownOnCellLeft</c>. 1.23 deleted both the method and the
    /// wiring - <c>createCellElem</c> now only assigns <c>OnMouseDownOnCellRight</c>, and only for a
    /// row that is not blocked - so a left-half click reaches nobody. The row's left-click delegate
    /// is therefore BetterMods' own now, installed on every cell as it is created. Whatever was in it
    /// already (nothing on 1.23, but another mod could have put something there) still runs for
    /// every click that was not aimed at the trigger box BetterMods drew.
    /// </para>
    /// </summary>
    [HarmonyPatch(typeof(GuiScreenMods), "createCellElem")]
    public static class GuiScreenMods_createCellElem_Patch
    {
        [HarmonyPrepare]
        public static bool Prepare() => PatchTarget.Exists("createCellElem", new[] { typeof(ModCellEntry), typeof(ElementBounds) });

        [HarmonyPostfix]
        public static void Postfix(GuiScreenMods __instance, IGuiElementCell __result)
        {
            try
            {
                if (__instance == null || !(__result is GuiElementModCell element)) return;

                GuiScreenMods screen = __instance;
                Action<int> previous = element.OnMouseDownOnCellLeft;
                element.OnMouseDownOnCellLeft = cellIndex => OnClickCellLeft(screen, element, cellIndex, previous);
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Error("[BetterMods] Could not wire the version picker to a mod row.");
                BetterModsModSystem.Logger?.Error(e);
            }
        }

        /// <summary>
        /// Runs from <c>GuiElementModCell.OnMouseUpOnElement</c> - a mouse-UP, despite the name.
        /// <paramref name="element"/> is the cell the delegate was installed on, which is the cell
        /// that was clicked; the index is only passed through to <paramref name="previous"/>.
        /// </summary>
        private static void OnClickCellLeft(GuiScreenMods screen, GuiElementModCell element, int cellIndex, Action<int> previous)
        {
            try
            {
                // The click that a popup swallowed landed on this row by accident. Nothing else may
                // see it either - it is not a click on this row at all.
                if (PopupClickGate.ClickConsumed) return;

                // The card's button row (Open mod page, Update to x, ...). A click on any button or
                // label there is consumed, whether or not it did anything. The buttons sit below the
                // version slot, so the two never compete.
                ICoreClientAPI api = screen.ElementComposer?.Api;
                if (api != null && ModCard.TryClick(element, api.Input.MouseX, api.Input.MouseY)) return;

                if (TryOpenVersionMenu(screen, element)) return;
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Error("[BetterMods] Switching mod version failed.");
                BetterModsModSystem.Logger?.Error(e);
            }

            try
            {
                previous?.Invoke(cellIndex);
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Error("[BetterMods] A mod row's left-click handler failed.");
                BetterModsModSystem.Logger?.Error(e);
            }
        }

        private static bool TryOpenVersionMenu(GuiScreenMods screen, GuiElementModCell element)
        {
            ModVersionGroup group = ModsScreenProfileUi.GroupFor(element?.cell?.Mod);
            if (group == null || !ModCellLayout.HasVersionPicker(element.cell)) return false;

            ICoreClientAPI api = screen.ElementComposer?.Api;
            if (api == null) return false;
            if (!ModCellLayout.IsInVersionZone(element, api.Input.MouseX, api.Input.MouseY)) return false;

            ModsScreenProfileUi.OpenVersionMenu(screen, element, group);
            return true;
        }
    }

    /// <summary>
    /// Re-decides vanilla's core-mod confirmation against the VIEWED PROFILE instead of the live
    /// mod status.
    /// <para>
    /// Vanilla routes a right-half click as <c>mod.Info.CoreMod &amp;&amp; mod.Status == Enabled</c>
    /// to the confirmation and everything else straight to <c>SwitchModStatus</c>
    /// (GuiScreenMods.cs:196-208). <c>Status</c> is the LIVE state, and BetterMods only mirrors a
    /// toggle into it when the profile on screen is the active one - so while another profile is
    /// being edited, vanilla's test answers a question about a different profile than the one the row
    /// was drawn from. A core mod the viewed profile has OFF but which is live-enabled asks "are you
    /// sure you want to disable this" as you switch it ON, and the inverse case switches a core mod
    /// off with no prompt at all.
    /// </para>
    /// <para>
    /// The guard used here is the one that matches the surface: confirm when THIS PROFILE is about to
    /// disable a core mod, i.e. the group is on and the click turns it off. Turning one back on is
    /// never a warning. A mod that could not be grouped is left to vanilla's own routing, which for it
    /// is still correct - nothing about it is profile-managed.
    /// </para>
    /// <para>
    /// The two branches are the private methods vanilla itself calls, invoked by reflection rather
    /// than reimplemented: <c>SwitchModStatus</c> is where BetterMods' own toggle prefix lives, and
    /// <c>ShowConfirmationDialog</c> owns the <c>GuiScreenConfirmAction</c> round trip (that screen is
    /// internal and cannot be constructed from a mod).
    /// </para>
    /// </summary>
    [HarmonyPatch(typeof(GuiScreenMods), "OnClickCellRight")]
    public static class GuiScreenMods_OnClickCellRight_Patch
    {
        private static readonly Type[] ToggleArgs = { typeof(GuiElementModCell), typeof(ModContainer) };

        private static readonly MethodInfo SwitchModStatus = AccessTools.Method(typeof(GuiScreenMods), "SwitchModStatus", ToggleArgs);
        private static readonly MethodInfo ShowConfirmationDialog = AccessTools.Method(typeof(GuiScreenMods), "ShowConfirmationDialog", ToggleArgs);

        [HarmonyPrepare]
        public static bool Prepare()
        {
            // All three, because this patch replaces the routing and then has to perform whichever
            // branch it picked. Missing any one of them means the whole decision stays vanilla.
            return PatchTarget.Exists("OnClickCellRight", new[] { typeof(int) })
                && PatchTarget.Exists("SwitchModStatus", ToggleArgs)
                && PatchTarget.Exists("ShowConfirmationDialog", ToggleArgs);
        }

        [HarmonyPrefix]
        public static bool Prefix(GuiScreenMods __instance, int cellIndex)
        {
            GuiElementModCell guicell;
            ModContainer mod;
            bool confirm;

            // Split in two so a failure while DECIDING falls back to vanilla, while a failure while
            // PERFORMING does not - by then vanilla must not also run, or the toggle happens twice.
            try
            {
                // The click that a popup swallowed landed on this row by accident; it is not a click
                // on this row at all, so neither branch may run.
                if (PopupClickGate.ClickConsumed) return false;

                if (SwitchModStatus == null || ShowConfirmationDialog == null) return true;

                GuiElementCellList<ModCellEntry> list = __instance?.ElementComposer?.GetCellList<ModCellEntry>("modstable");
                if (list == null || cellIndex < 0 || cellIndex >= list.elementCells.Count) return true;

                guicell = list.elementCells[cellIndex] as GuiElementModCell;
                mod = guicell?.cell?.Mod;
                if (guicell == null || mod == null) return true;

                ModVersionGroup group = ModsScreenProfileUi.GroupFor(mod);
                if (group == null) return true;

                confirm = mod.Info != null && mod.Info.CoreMod && group.Enabled;
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Error("[BetterMods] Could not route a mod row click; falling back to vanilla.");
                BetterModsModSystem.Logger?.Error(e);
                return true;
            }

            try
            {
                (confirm ? ShowConfirmationDialog : SwitchModStatus).Invoke(__instance, new object[] { guicell, mod });
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Error("[BetterMods] Handling a mod row click failed.");
                BetterModsModSystem.Logger?.Error(e);
            }

            return false;
        }
    }

    /// <summary>
    /// Routes a mod toggle into the viewed profile, for the whole version group rather than the one
    /// installed file the row happens to show.
    /// <para>
    /// The vanilla body is replaced rather than wrapped, because it works on a single
    /// <c>"modid@version"</c> and cannot express "this version on, its siblings off". Everything it
    /// did is still done: the profile file always gets the change, and when the viewed profile IS
    /// the active one <c>ClientSettings</c> and the live <c>ModContainer.Status</c> are updated too.
    /// Editing any other profile touches neither, because no mod is actually being enabled or
    /// disabled right now.
    /// </para>
    /// <para>
    /// If the mod could not be grouped - no usable mod id, or our <c>LoadModCells</c> postfix failed
    /// - the prefix returns true and vanilla handles the toggle exactly as it always did. Vanilla
    /// writes straight to <c>ClientSettings</c> and knows nothing about profiles, so the postfix
    /// folds that write into the active profile. Without it the toggle looked like it worked and the
    /// next Apply silently put it back.
    /// </para>
    /// </summary>
    [HarmonyPatch(typeof(GuiScreenMods), "SwitchModStatus")]
    public static class GuiScreenMods_SwitchModStatus_Patch
    {
        [HarmonyPrepare]
        public static bool Prepare() => PatchTarget.Exists("SwitchModStatus", new[] { typeof(GuiElementModCell), typeof(ModContainer) });

        [HarmonyPrefix]
        public static bool Prefix(GuiElementModCell guicell, ModContainer mod, ref bool __state)
        {
            __state = false;

            try
            {
                // A click that belonged to an open popup must not toggle anything.
                if (PopupClickGate.ClickConsumed) return false;

                string viewedId = ModsScreenProfileUi.ViewedProfileId;
                ModVersionGroup group = ModsScreenProfileUi.GroupFor(mod);
                if (viewedId == null || group == null)
                {
                    __state = true;
                    return true;
                }

                bool on = !guicell.On;
                guicell.On = on;
                if (guicell.cell != null) guicell.cell.Enabled = on;

                ModGrouping.SetEnabled(viewedId, group, on);
                ModsScreenProfileUi.MirrorToClientSettingsIfActive(group);

                return false;
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Error("[BetterMods] Toggling a mod against the viewed profile failed; falling back to vanilla.");
                BetterModsModSystem.Logger?.Error(e);
                __state = true;
                return true;
            }
        }

        /// <summary>
        /// Only runs when the prefix stood aside. Vanilla has just written
        /// <c>ClientSettings.DisabledMods</c> for a mod no profile can address by mod id — its
        /// <c>ModInfo.ModID</c> is null, or the name it would be derived from is unusable — so the
        /// change is folded into the ACTIVE profile, which is the profile <c>ClientSettings</c>
        /// represents. That is the honest owner even when another profile is being viewed: what
        /// vanilla changed is the live mod set, not the profile on screen.
        /// </summary>
        [HarmonyPostfix]
        public static void Postfix(bool __state)
        {
            if (!__state) return;

            try
            {
                ProfileStore.ReconcileActiveFromClientSettings();
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Error("[BetterMods] Could not record vanilla's mod toggle against the active profile.");
                BetterModsModSystem.Logger?.Error(e);
            }
        }
    }

    /// <summary>
    /// Opening the mods screen always lands on the active profile's tab, at the top of the list.
    /// <para>
    /// This is also the one funnel every in-place rebuild goes through - <c>OnScreenLoaded</c>
    /// re-runs <c>InitGui</c>, which disposes the composer and takes the scroll position with it -
    /// so the scroll position is captured here and put back in the postfix, once vanilla's own
    /// <c>SetHeights</c> tail has told the new scrollbar how tall the new list is.
    /// </para>
    /// </summary>
    [HarmonyPatch(typeof(GuiScreenMods), "OnScreenLoaded")]
    public static class GuiScreenMods_OnScreenLoaded_Patch
    {
        [HarmonyPrepare]
        public static bool Prepare() => PatchTarget.Exists("OnScreenLoaded");

        [HarmonyPrefix]
        public static void Prefix(GuiScreenMods __instance)
        {
            try
            {
                // An update install that finished while a world was running could not rebuild the
                // menu's mod list then; this is the first chance. Before anything reads allMods.
                if (ModUpdateService.ConsumeMenuReload()) __instance.ScreenManager.loadMods();

                // The mods screen is the first place the player looks after installing or enabling a
                // mod from the online browser, which does that by writing ClientSettings directly
                // (ModDbUtil.MaybeEnableMod). Fold anything like that into the active profile BEFORE
                // the list is built, or the row renders from the profile and shows the mod as off.
                ProfileStore.ReconcileActiveFromClientSettings();

                // Re-entries (returning from a sub-screen, reloading the list, switching tab,
                // picking a version) keep the current tab and stay where they were scrolled to.
                if (ModsScreenProfileUi.ConsumeViewResetSuppression())
                {
                    ModsScreenProfileUi.CaptureScroll(__instance);
                    return;
                }

                ModsScreenProfileUi.DiscardCapturedScroll();
                ModsScreenProfileUi.ResetViewToActive();

                // A genuine (re)open is never in the middle of a click, so this is the safe place to
                // clear a gate whose mouse-up went somewhere else entirely - the window losing focus
                // mid-click is the realistic way that happens. See PopupClickGate.
                PopupClickGate.Release();
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Error("[BetterMods] Could not reset the viewed profile.");
                BetterModsModSystem.Logger?.Error(e);
            }
        }

        [HarmonyPostfix]
        public static void Postfix(GuiScreenMods __instance)
        {
            // Has its own try/catch and is a no-op when nothing was captured.
            ModsScreenProfileUi.RestoreScroll(__instance);
        }
    }

    /// <summary>
    /// The vanilla core-mod confirmation pushes a screen and comes back via
    /// <c>LoadScreen(this)</c>, which re-runs OnScreenLoaded. Keep the player on their tab.
    /// </summary>
    [HarmonyPatch(typeof(GuiScreenMods), "ShowConfirmationDialog")]
    public static class GuiScreenMods_ShowConfirmationDialog_Patch
    {
        [HarmonyPrepare]
        public static bool Prepare() => PatchTarget.Exists("ShowConfirmationDialog", new[] { typeof(GuiElementModCell), typeof(ModContainer) });

        [HarmonyPrefix]
        public static void Prefix()
        {
            ModsScreenProfileUi.SuppressNextViewReset();
        }
    }

    /// <summary>
    /// "Reload Mods" calls OnScreenLoaded directly; that is a refresh, not a reopen. It is also the
    /// one moment the player tells us the installed set changed under us (a file copied in by hand),
    /// so the update check runs again.
    /// </summary>
    [HarmonyPatch(typeof(GuiScreenMods), "OnReloadMods")]
    public static class GuiScreenMods_OnReloadMods_Patch
    {
        [HarmonyPrepare]
        public static bool Prepare() => PatchTarget.Exists("OnReloadMods");

        [HarmonyPrefix]
        public static void Prefix()
        {
            ModsScreenProfileUi.SuppressNextViewReset();
        }

        [HarmonyPostfix]
        public static void Postfix()
        {
            try
            {
                // After vanilla's loadMods(), so the snapshot sees the reloaded list.
                ModUpdateService.Recheck();
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Error("[BetterMods] Could not re-run the mod update check after Reload Mods.");
                BetterModsModSystem.Logger?.Error(e);
            }
        }
    }
}
