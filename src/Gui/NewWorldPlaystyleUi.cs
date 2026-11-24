using System;
using System.Collections.Generic;
using BetterMods.Playstyles;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.Client;
using Vintagestory.Common;

namespace BetterMods.Gui
{
    /// <summary>
    /// "Save playstyle" / "Delete playstyle" on Create New World, and the menu-side behaviour of the
    /// custom playstyles <see cref="CustomPlaystyles"/> adds to its list.
    /// <para>
    /// <c>GuiScreenSingleplayerNewWorld</c> is ONE cached instance (<c>LoadAndCacheScreen</c>) that
    /// rebuilds itself through <c>InitGui</c> on every open, every return from a modal or the
    /// customizer, and every resize. Every entry point here therefore works from the screen's live
    /// state, never from anything captured at a previous build. Every entry point is wrapped: on a
    /// failure the screen is left exactly as vanilla built it.
    /// </para>
    /// </summary>
    internal static class NewWorldPlaystyleUi
    {
        private const string SaveButtonKey = "bettermods-playstyle-save";
        private const string DeleteButtonKey = "bettermods-playstyle-delete";
        private const string ScrollbarKey = "scrollbar";
        private const string WorldNameKey = "worldname";

        private const double ButtonGap = 8.0;
        private const double ButtonPadX = 8.0;
        private const double LabelToButtonsGap = 12.0;

        private static readonly AccessTools.FieldRef<GuiScreenSingleplayerNewWorld, WorldConfig> WcuRef =
            GameFields.Ref<GuiScreenSingleplayerNewWorld, WorldConfig>("wcu", "custom playstyles stay off");

        private static readonly AccessTools.FieldRef<GuiScreenSingleplayerNewWorld, int> SelectedIndexRef =
            GameFields.Ref<GuiScreenSingleplayerNewWorld, int>("selectedPlaystyleIndex", "custom playstyles stay off");

        private static readonly AccessTools.FieldRef<GuiScreenSingleplayerNewWorld, ElementBounds> ClippingBoundsRef =
            GameFields.Ref<GuiScreenSingleplayerNewWorld, ElementBounds>("clippingBounds", "no Save/Delete playstyle buttons");

        private static readonly AccessTools.FieldRef<GuiScreenSingleplayerNewWorld, ElementBounds> ListBoundsRef =
            GameFields.Ref<GuiScreenSingleplayerNewWorld, ElementBounds>("listBounds", "no Save/Delete playstyle buttons");

        private static readonly AccessTools.FieldRef<ScreenManager, List<ModContainer>> VerifiedModsRef =
            GameFields.Ref<ScreenManager, List<ModContainer>>("verifiedMods");

        /// <summary>The code the next <c>InitGui</c> should select (set by a Save). Consumed once.</summary>
        private static string pendingSelectCode;

        // ---------------------------------------------------------------- InitGui

        /// <summary>
        /// Before vanilla's <c>InitGui</c>: refresh the library, then make the cached screen's
        /// <c>selectedPlaystyleIndex</c> safe for the list <c>InitGui</c> is about to build.
        /// <para>
        /// Vanilla keeps that INDEX across rebuilds and does <c>wcu.selectPlayStyle(index)</c> and
        /// <c>cells[index]</c> with it (GuiScreenSingleplayerNewWorld.cs:87,132); with a list that can
        /// now shrink (a deleted preset, a vanished file, a base mod switched off) that is an index out
        /// of range on the main menu. So the selection is remembered by CODE and re-resolved against
        /// the rebuilt list; a custom preset that is gone falls back to its base playstyle (or the
        /// first entry) and takes its leftovers - customizations and world height - with it.
        /// </para>
        /// </summary>
        public static void BeforeInitGui(GuiScreenSingleplayerNewWorld screen)
        {
            try
            {
                CustomPlaystyles.Refresh();

                if (screen == null || WcuRef == null || SelectedIndexRef == null) return;

                WorldConfig wcu = WcuRef(screen);
                if (wcu == null) return;

                string pending = pendingSelectCode;
                pendingSelectCode = null;

                string previous = wcu.PlayStyles != null ? wcu.CurrentPlayStyle?.Code : null;

                // The same two steps InitGui opens with, so the list resolved against here is the list
                // it builds (the library is not re-read in between).
                List<ModContainer> mods = VerifiedModsRef != null && screen.ScreenManager != null ? VerifiedModsRef(screen.ScreenManager) : null;
                if (mods != null) wcu.mods = mods;
                wcu.LoadPlayStyles();

                List<PlayStyle> list = wcu.PlayStyles;
                if (list == null || list.Count == 0)
                {
                    SelectedIndexRef(screen) = 0;
                    return;
                }

                string wanted = pending ?? previous;
                int index = UsableIndexOf(list, wanted);

                if (index < 0 && PlaystyleFormat.IsCustomCode(wanted))
                {
                    index = UsableIndexOf(list, CustomPlaystyles.BaseOf(wanted));
                    ClearPresetLeftovers(wcu);
                }

                SelectedIndexRef(screen) = index >= 0 && index < list.Count ? index : 0;
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Error("[BetterMods] Could not prepare the playstyle list; resetting the selection.");
                BetterModsModSystem.Logger?.Error(e);

                try
                {
                    if (screen != null && SelectedIndexRef != null) SelectedIndexRef(screen) = 0;
                }
                catch (Exception)
                {
                    // Nothing more to do.
                }
            }
        }

        private static int UsableIndexOf(List<PlayStyle> list, string code)
        {
            if (code == null) return -1;

            int index = list.FindIndex(p => p?.Code == code);
            if (index < 0) return -1;

            CustomPlaystyles.BuiltPlaystyle custom = CustomPlaystyles.Find(code);
            return PlaystyleFormat.IsCustomCode(code) && (custom == null || !custom.Usable) ? -1 : index;
        }

        /// <summary>
        /// A custom preset puts its settings in the playstyle itself and its height in
        /// <c>MapsizeY</c>; vanilla keeps <c>WorldConfigsCustom</c> across clicks (and after a
        /// customize Apply it holds EVERY attribute). Leaving a preset therefore resets both, so the
        /// next entry means what it says.
        /// </summary>
        private static void ClearPresetLeftovers(WorldConfig wcu)
        {
            wcu.WorldConfigsCustom.Clear();
            wcu.MapsizeY = CustomPlaystyles.VanillaWorldHeight;
        }

        /// <summary>
        /// After vanilla's <c>InitGui</c>: the Save and Delete buttons, right-aligned on the "Select
        /// playstyle" label row - the bottom row (Back / Customize / Create) is already full at the
        /// minimum width, and nothing is drawn right of that label. Same reopen / add / re-parent /
        /// recompose sequence as the mods screen (<see cref="ModsScreenProfileUi"/>). Never throws.
        /// </summary>
        public static void AfterInitGui(GuiScreenSingleplayerNewWorld screen)
        {
            GuiComposer composer = screen?.ElementComposer;

            try
            {
                if (composer == null || ClippingBoundsRef == null || ListBoundsRef == null || WcuRef == null) return;
                if (CustomPlaystyles.Folder == null || !CustomPlaystyles.FeatureArmed()) return;

                ElementBounds clippingBounds = ClippingBoundsRef(screen);
                ElementBounds listBounds = ListBoundsRef(screen);
                ElementBounds insetBounds = clippingBounds?.ParentBounds;
                ElementBounds contentBounds = insetBounds?.ParentBounds;
                if (listBounds == null || insetBounds == null || contentBounds == null || contentBounds == composer.Bounds)
                {
                    BetterModsModSystem.Logger?.Warning("[BetterMods] New-world screen layout not recognised; no playstyle buttons.");
                    return;
                }

                double guiScale = RuntimeEnv.GUIScale;
                if (!(guiScale > 0.01)) guiScale = 1.0;

                // The label row: vanilla puts the inset 3 below a 30-high "Select playstyle" label.
                double rowY = insetBounds.fixedY - 3.0 - 30.0;
                double labelRight = CairoFont.WhiteSmallishText().GetTextExtents(Lang.Get("singleplayer-selectplaystyle")).XAdvance / guiScale + LabelToButtonsGap;
                double available = contentBounds.fixedWidth - labelRight;

                string saveLabel = "Save playstyle";
                string deleteLabel = "Delete playstyle";
                if (ButtonWidth(saveLabel, guiScale) + ButtonGap + ButtonWidth(deleteLabel, guiScale) > available)
                {
                    saveLabel = "Save";
                    deleteLabel = "Delete";
                }

                double deleteWidth = ButtonWidth(deleteLabel, guiScale);
                if (ButtonWidth(saveLabel, guiScale) + ButtonGap + deleteWidth > available)
                {
                    BetterModsModSystem.Logger?.Warning("[BetterMods] No room for the playstyle buttons at this window size.");
                    return;
                }

                // AutoBoxSize grows each button to its text; RightFixed keeps them against the content
                // box's right edge, i.e. the scrollbar's.
                ElementBounds deleteBounds = ElementBounds.FixedSize(0.0, 26.0)
                    .WithFixedPadding(ButtonPadX, 2.0)
                    .WithAlignment(EnumDialogArea.RightFixed);
                deleteBounds.fixedY = rowY;

                ElementBounds saveBounds = ElementBounds.FixedSize(0.0, 26.0)
                    .WithFixedPadding(ButtonPadX, 2.0)
                    .WithAlignment(EnumDialogArea.RightFixed)
                    .WithFixedAlignmentOffset(-(deleteWidth + ButtonGap), 0.0);
                saveBounds.fixedY = rowY;

                // Add* is a no-op on a composed composer, so reopen it first.
                composer.Composed = false;
                composer.AddSmallButton(saveLabel, () => OnSave(screen), saveBounds, EnumButtonStyle.Normal, SaveButtonKey);
                composer.AddSmallButton(deleteLabel, () => OnDelete(screen), deleteBounds, EnumButtonStyle.Normal, DeleteButtonKey);

                // EndChildElements() already ran, so the composer parented these to the dialog root,
                // which would drop their alignment. Re-home them on the content box.
                ReParent(composer, saveBounds, contentBounds);
                ReParent(composer, deleteBounds, contentBounds);

                composer.Bounds.MarkDirtyRecursive();
                composer.Bounds.CalcWorldBounds();
                composer.Compose(focusFirstElement: false);

                // Vanilla's InitGui tail, redone after the recompose.
                listBounds.CalcWorldBounds();
                clippingBounds.CalcWorldBounds();
                composer.GetScrollbar(ScrollbarKey)?.SetHeights((float)clippingBounds.fixedHeight, (float)listBounds.fixedHeight);

                UpdateButtons(screen);
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Error("[BetterMods] Could not add the playstyle buttons; the screen stays vanilla.");
                BetterModsModSystem.Logger?.Error(e);

                try
                {
                    if (composer != null && !composer.Composed) composer.Compose(focusFirstElement: false);
                }
                catch (Exception recomposeError)
                {
                    BetterModsModSystem.Logger?.Error(recomposeError);
                }
            }
        }

        private static double ButtonWidth(string text, double guiScale)
        {
            return CairoFont.SmallButtonText().GetTextExtents(text).Width / guiScale + 1.0 + 2.0 * ButtonPadX;
        }

        private static void ReParent(GuiComposer composer, ElementBounds bounds, ElementBounds newParent)
        {
            composer.Bounds.ChildBounds.Remove(bounds);
            bounds.ParentBounds = null;
            newParent.WithChild(bounds);
        }

        /// <summary>Delete only for the selected entry when it is a custom, usable, non-read-only preset.</summary>
        public static void UpdateButtons(GuiScreenSingleplayerNewWorld screen)
        {
            try
            {
                GuiComposer composer = screen?.ElementComposer;
                WorldConfig wcu = WcuRef != null && screen != null ? WcuRef(screen) : null;
                if (composer == null || wcu == null) return;

                PlayStyle current = wcu.PlayStyles != null ? wcu.CurrentPlayStyle : null;
                CustomPlaystyles.BuiltPlaystyle custom = CustomPlaystyles.Find(current?.Code);

                if (composer.GetButton(DeleteButtonKey) is GuiElementTextButton delete)
                {
                    delete.Enabled = custom != null && custom.Usable && !custom.File.ReadOnly;
                }

                if (composer.GetButton(SaveButtonKey) is GuiElementTextButton save)
                {
                    save.Enabled = current != null && (custom == null ? !PlaystyleFormat.IsCustomCode(current.Code) : custom.Usable);
                }
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not update the playstyle buttons: " + e.Message);
            }
        }

        // ---------------------------------------------------------------- the list

        /// <summary>
        /// Titles, descriptions and the top-right tag for custom entries on the new-world list.
        /// Vanilla titles every row with <c>Lang.Get("playstyle-" + LangCode)</c>, which for a custom
        /// code is just the key echoed back. Called from the <c>loadPlaystyleCells</c> postfix.
        /// </summary>
        public static void DressNewWorldCells(List<PlaystyleListEntry> cells)
        {
            if (cells == null) return;

            foreach (PlaystyleListEntry cell in cells)
            {
                try
                {
                    CustomPlaystyles.BuiltPlaystyle custom = CustomPlaystyles.Find(cell?.PlayStyle?.Code);
                    if (custom == null) continue;

                    PlaystyleFile file = custom.File;
                    List<string> lines = new List<string>();

                    cell.Title = file.Name;

                    if (!custom.Usable)
                    {
                        double[] dim = (double[])GuiStyle.DialogDefaultTextColor.Clone();
                        dim[3] = 0.45;
                        cell.TitleFont = CairoFont.WhiteSmallishText().WithColor(dim);
                        cell.RightTopText = "Unavailable";
                        lines.Add(custom.UnusableReason);
                    }
                    else
                    {
                        cell.RightTopText = file.ReadOnly
                            ? (string.IsNullOrEmpty(file.Author) ? "From a mod" : "By " + file.Author)
                            : "Custom";

                        if (!string.IsNullOrEmpty(file.Description)) lines.Add(file.Description);
                        lines.Add("Based on " + BaseTitle(custom) + ", world height " + custom.WorldHeight + ".");
                    }

                    // Skipped settings are NOT listed here - they go to the tooltip (AppendTooltipNotes)
                    // and the log, so a preset with a few unsupported settings looks like any other.
                    cell.DetailText = string.Join("\n", lines);
                }
                catch (Exception e)
                {
                    BetterModsModSystem.Logger?.Warning("[BetterMods] Could not label a custom playstyle row: " + e.Message);
                }
            }
        }

        /// <summary>
        /// The customize screen's playstyle dropdown: custom entries get their names, and unusable
        /// ones are left out (there is no way to refuse a dropdown pick after the fact). Only the cell
        /// list is filtered - the screen's <c>wcu.PlayStyles</c> keeps them, harmlessly.
        /// </summary>
        public static void DressCustomizeCells(List<PlaystyleListEntry> cells)
        {
            if (cells == null) return;

            try
            {
                cells.RemoveAll(cell =>
                {
                    CustomPlaystyles.BuiltPlaystyle custom = CustomPlaystyles.Find(cell?.PlayStyle?.Code);
                    return custom != null && !custom.Usable;
                });

                foreach (PlaystyleListEntry cell in cells)
                {
                    CustomPlaystyles.BuiltPlaystyle custom = CustomPlaystyles.Find(cell?.PlayStyle?.Code);
                    if (custom != null) cell.Title = custom.File.Name;
                }
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not label the custom playstyles in the dropdown: " + e.Message);
            }
        }

        /// <summary>
        /// A custom entry's hover tooltip: vanilla's <c>WorldConfig.ToRichText</c> text (the only thing
        /// that fills the new-world rows' tooltips, from <c>InitGui</c> and <c>OnClickCellLeft</c>) plus
        /// one dimmed line saying which settings were skipped. Called from the <c>ToRichText</c>
        /// postfix; leaves <paramref name="text"/> unchanged for anything but a custom entry with notes.
        /// </summary>
        public static void AppendTooltipNotes(PlayStyle playstyle, ref string text)
        {
            try
            {
                if (!PlaystyleFormat.IsCustomCode(playstyle?.Code)) return;

                CustomPlaystyles.BuiltPlaystyle custom = CustomPlaystyles.Find(playstyle.Code);
                if (custom?.Notes == null) return;

                string notes = Updates.ModUpdateRules.AsciiLine(custom.Notes, 600).Replace("<", "&lt;").Replace(">", "&gt;");
                string line = "<font opacity=\"0.6\">Skipped: " + notes + "</font>";
                text = string.IsNullOrEmpty(text) ? line : text.TrimEnd('\r', '\n') + "\n" + line;
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not add the playstyle notes to its tooltip: " + e.Message);
            }
        }

        private static string BaseTitle(CustomPlaystyles.BuiltPlaystyle custom)
        {
            string langCode = custom.Base?.LangCode;
            if (langCode == null) return custom.File.BaseCode;

            string title = Lang.Get("playstyle-" + langCode);
            return Updates.ModUpdateRules.AsciiLine(title, 60);
        }

        // ---------------------------------------------------------------- clicks

        /// <summary>
        /// Before vanilla's <c>OnClickCellLeft</c>. Refuses a click on an unusable entry
        /// (<c>SavegameCellEntry.Enabled</c> is not honoured by <c>GuiElementMainMenuCell</c>, so this
        /// is the only place to do it). Moving ONTO a custom preset clears <c>WorldConfigsCustom</c>
        /// (else a preset click after a customize Apply would change nothing) and applies the
        /// preset's height; moving from a preset onto a built-in clears the preset's leftovers.
        /// Re-clicking the current entry is vanilla's business. Returns false to swallow the click.
        /// </summary>
        public static bool BeforeCellClick(GuiScreenSingleplayerNewWorld screen, int cellIndex)
        {
            try
            {
                WorldConfig wcu = WcuRef != null && screen != null ? WcuRef(screen) : null;
                List<PlayStyle> list = wcu?.PlayStyles;
                if (list == null || cellIndex < 0 || cellIndex >= list.Count) return true;

                PlayStyle target = list[cellIndex];
                CustomPlaystyles.BuiltPlaystyle custom = CustomPlaystyles.Find(target?.Code);
                if (custom != null && !custom.Usable) return false;
                if (custom == null && PlaystyleFormat.IsCustomCode(target?.Code)) return false;

                PlayStyle current = wcu.CurrentPlayStyle;
                if (current != null && target != null && current.Code == target.Code) return true;

                if (custom != null)
                {
                    wcu.WorldConfigsCustom.Clear();
                    wcu.MapsizeY = custom.WorldHeight;
                }
                else if (PlaystyleFormat.IsCustomCode(current?.Code))
                {
                    ClearPresetLeftovers(wcu);
                }
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] Playstyle click handling failed; vanilla handles it: " + e.Message);
            }

            return true;
        }

        /// <summary>
        /// The customize screen's playstyle dropdown, before vanilla's <c>onPlayStyleChanged</c>
        /// (which clears <c>WorldConfigsCustom</c> itself and then sizes the height slider from
        /// <c>MapsizeY</c>): set the preset's height, or reset it when leaving a preset.
        /// </summary>
        public static void BeforeCustomizePlaystyleChanged(GuiScreenWorldCustomize screen, string code)
        {
            try
            {
                WorldConfig wcu = screen?.wcu;
                if (wcu == null || !wcu.IsNewWorld || wcu.PlayStyles == null) return;

                string current = wcu.CurrentPlayStyle?.Code;
                if (current == code) return;

                CustomPlaystyles.BuiltPlaystyle custom = CustomPlaystyles.Find(code);
                if (custom != null && custom.Usable) wcu.MapsizeY = custom.WorldHeight;
                else if (custom == null && PlaystyleFormat.IsCustomCode(current)) wcu.MapsizeY = CustomPlaystyles.VanillaWorldHeight;
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not apply the preset's world height: " + e.Message);
            }
        }

        // ---------------------------------------------------------------- save

        /// <summary>A button handler: nothing swaps screens inline (NOTES, "GUI").</summary>
        private static bool OnSave(GuiScreenSingleplayerNewWorld screen)
        {
            Post(() => BeginSave(screen));
            return true;
        }

        private static void BeginSave(GuiScreenSingleplayerNewWorld screen)
        {
            if (screen == null || !screen.IsOpened || WcuRef == null) return;

            ScreenManager manager = screen.ScreenManager;
            string worldName = WorldName(screen);

            CustomPlaystyles.SaveCapture capture = CustomPlaystyles.Capture(WcuRef(screen), out string captureError);
            if (capture == null)
            {
                ShowNotice(screen, worldName, "Cannot save playstyle", captureError);
                return;
            }

            string intro = "Saves the selected playstyle's settings, including anything changed under Customize, " +
                           "and the world height. The seed and world name are not saved.";

            manager.LoadScreen(new GuiScreenBetterModsTextPrompt(
                "Save playstyle",
                intro,
                "Name",
                capture.From?.File.Name ?? "",
                PlaystyleFormat.MaxNameLength,
                "Description (optional)",
                capture.From?.File.Description ?? "",
                PlaystyleFormat.MaxDescriptionLength,
                "Save",
                name => string.IsNullOrWhiteSpace(name) ? "Enter a name." : null,
                (confirmed, name, description) => Post(() =>
                {
                    if (!confirmed)
                    {
                        ReturnTo(screen, worldName);
                        return;
                    }

                    ContinueSave(screen, worldName, capture, name, description);
                }),
                manager,
                screen));
        }

        private static void ContinueSave(GuiScreenSingleplayerNewWorld screen, string worldName, CustomPlaystyles.SaveCapture capture, string name, string description)
        {
            CustomPlaystyles.SavePlan plan = CustomPlaystyles.PlanSave(name, out string planError);
            if (plan == null)
            {
                ShowNotice(screen, worldName, "Could not save playstyle", planError);
                return;
            }

            if (plan.Replaces == null)
            {
                FinishSave(screen, worldName, capture, plan, name, description);
                return;
            }

            string body = "A playstyle named <strong>" + Vtml(plan.Replaces.Name) + "</strong> is already saved as " +
                          Vtml(System.IO.Path.GetFileName(plan.Replaces.Path)) + ".\n\nReplace it with the current settings?";

            ScreenManager manager = screen.ScreenManager;
            manager.LoadScreen(new GuiScreenBetterModsConfirm("Replace playstyle?", body, "Replace", confirmed => Post(() =>
            {
                if (confirmed) FinishSave(screen, worldName, capture, plan, name, description);
                else ReturnTo(screen, worldName);
            }), manager, screen));
        }

        private static void FinishSave(GuiScreenSingleplayerNewWorld screen, string worldName, CustomPlaystyles.SaveCapture capture, CustomPlaystyles.SavePlan plan, string name, string description)
        {
            string code = CustomPlaystyles.Write(plan, name, description, capture, out string writeError);
            if (code == null && writeError != null)
            {
                ShowNotice(screen, worldName, "Could not save playstyle", writeError);
                return;
            }

            // Select the new preset. Its values equal the current ones, so WorldConfigsCustom is kept:
            // whatever the file could not express stays exactly as the player had it.
            pendingSelectCode = code;
            ReturnTo(screen, worldName);
        }

        // ---------------------------------------------------------------- delete

        private static bool OnDelete(GuiScreenSingleplayerNewWorld screen)
        {
            Post(() => BeginDelete(screen));
            return true;
        }

        private static void BeginDelete(GuiScreenSingleplayerNewWorld screen)
        {
            if (screen == null || !screen.IsOpened || WcuRef == null) return;

            WorldConfig wcu = WcuRef(screen);
            CustomPlaystyles.BuiltPlaystyle custom = CustomPlaystyles.Find(wcu?.CurrentPlayStyle?.Code);

            // Built-ins and worldconfig.json playstyles have no entry here; read-only files are a mod's.
            if (custom == null || custom.File.ReadOnly) return;

            ScreenManager manager = screen.ScreenManager;
            string worldName = WorldName(screen);
            PlaystyleFile file = custom.File;

            string body = "Delete the playstyle <strong>" + Vtml(file.Name) + "</strong>?\n\n" +
                          "This deletes " + Vtml(System.IO.Path.GetFileName(file.Path)) + " from the Playstyles folder and cannot be undone. " +
                          "Worlds already created with it are not affected.";

            manager.LoadScreen(new GuiScreenBetterModsConfirm("Delete playstyle", body, "Delete", confirmed => Post(() =>
            {
                if (confirmed)
                {
                    string error = CustomPlaystyles.Delete(file);
                    if (error != null)
                    {
                        ShowNotice(screen, worldName, "Could not delete playstyle", error);
                        return;
                    }
                }

                // InitGui's prefix finds the selected code gone and falls back to its base.
                ReturnTo(screen, worldName);
            }), manager, screen));
        }

        // ---------------------------------------------------------------- shared

        private static string WorldName(GuiScreenSingleplayerNewWorld screen)
        {
            try
            {
                return screen?.ElementComposer?.GetTextInput(WorldNameKey)?.GetText();
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Back to the new-world screen the way vanilla's <c>OnReturnFromCustomizer</c> does it
        /// (GuiScreenSingleplayerNewWorld.cs:251-253): <c>LoadScreen</c> rebuilds it through
        /// <c>InitGui</c>, which re-rolls the world name unless the player typed one, so the name
        /// captured before leaving is put back.
        /// </summary>
        private static void ReturnTo(GuiScreenSingleplayerNewWorld screen, string worldName)
        {
            screen.ScreenManager.LoadScreen(screen);

            if (worldName != null) screen.ElementComposer?.GetTextInput(WorldNameKey)?.SetValue(worldName);
        }

        private static void ShowNotice(GuiScreenSingleplayerNewWorld screen, string worldName, string title, string message)
        {
            ScreenManager manager = screen.ScreenManager;
            manager.LoadScreen(new GuiScreenBetterModsConfirm(title, Vtml(message ?? "Something went wrong."), "OK",
                _ => Post(() => ReturnTo(screen, worldName)), manager, screen));
        }

        private static string Vtml(string text)
        {
            return Updates.ModUpdateRules.AsciiLine(text, 200).Replace("<", "&lt;").Replace(">", "&gt;");
        }

        /// <summary>Main thread, next frame, wrapped - the queue invokes actions bare.</summary>
        private static void Post(Action action)
        {
            ScreenManager.EnqueueMainThreadTask(() =>
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    BetterModsModSystem.Logger?.Error("[BetterMods] A playstyle action failed.");
                    BetterModsModSystem.Logger?.Error(e);
                }
            });
        }
    }
}
