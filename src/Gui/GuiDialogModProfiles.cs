using System;
using System.Collections.Generic;
using System.Text;
using BetterMods.Profiles;
using BetterMods.Startup;
using BetterMods.Updates;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.Client.NoObf;
using Vintagestory.Common;

namespace BetterMods.Gui
{
    /// <summary>
    /// The in-game profile manager: profile tabs on top, the installed mod list with an on/off
    /// switch per mod below, Apply Profile and Close at the bottom.
    /// <para>
    /// This exists because the main-menu patch only takes effect once mod code has run, i.e. after
    /// the player has loaded a world at least once in this session - on a cold launch the menu is
    /// plain vanilla. The dialog is always available in-game and offers the same functionality.
    /// </para>
    /// <para>
    /// Viewing and applying are separate, exactly like the menu version: toggling a mod only edits
    /// the VIEWED profile's file entry, and only Apply Profile writes <c>ClientSettings</c>. In-game
    /// that write cannot affect the running world - it decides what loads next time.
    /// </para>
    /// </summary>
    public class GuiDialogModProfiles : GuiDialog
    {
        public const string HotkeyCode = "bettermodsprofiles";

        private const string ComposerName = "bettermods-profiles";
        private const string ListKey = "bettermods-modlist";
        private const string ScrollbarKey = "bettermods-scrollbar";
        private const string NoticeKey = "bettermods-notice";

        // The tab strip, the version picker and the rename panel are the same three elements the
        // main-menu screen composes, so both surfaces address them by the one set of keys - and the
        // tab row is the same height on both. ModsScreenProfileUi owns them.
        private const string TabsKey = ModsScreenProfileUi.TabsElementKey;
        private const string VersionMenuKey = ModsScreenProfileUi.VersionMenuKey;
        private const string RenamePanelKey = ModsScreenProfileUi.RenamePanelKey;

        private const double TabRowHeight = ModsScreenProfileUi.TabRowHeight;

        /// <summary>Width the dialog wants for its mod list, unscaled, before the window is consulted.</summary>
        private const double PreferredListWidth = 700.0;

        /// <summary>Narrowest the mod list may be squeezed to, unscaled.</summary>
        private const double MinListWidth = 320.0;

        /// <summary>Which strip the tab element remembers its scroll position under.</summary>
        private const string TabScrollKey = "ingame";

        private const string DefaultNotice = "Toggles edit the selected profile. Apply Profile takes effect the next time you load a world.";

        private readonly List<ModContainer> installedMods = new List<ModContainer>();

        /// <summary>The groups behind the currently composed rows, one per row, keyed by mod id.</summary>
        private readonly Dictionary<string, ModVersionGroup> groupsByModId =
            new Dictionary<string, ModVersionGroup>(StringComparer.OrdinalIgnoreCase);

        private ElementBounds listBounds;
        private ElementBounds clipBounds;

        private IAsset warningIcon;
        private bool warningIconResolved;

        private string viewedProfileId;

        /// <summary>What the last "Fix dependencies" did, for the notice line; null when there is nothing to say.</summary>
        private string fixSummary;
        private bool fixSummaryIsError;

        /// <summary>The player pressed Add Shortcut since this dialog opened: its outcome owns the notice line.</summary>
        private bool showShortcutResult;

        public override string ToggleKeyCombinationCode => HotkeyCode;

        public override bool PrefersUngrabbedMouse => true;

        /// <summary>
        /// Above every ordinary dialog, below the escape menu.
        /// <para>
        /// <c>GuiManager</c> keeps the opened dialogs sorted by <c>DrawOrder</c> and renders them low
        /// to high; focusing a dialog only moves it to the front of ITS OWN draw-order group
        /// (<c>GuiManager.RequestFocus</c>). At the default 0.1 this dialog therefore always rendered
        /// behind the inventories, the handbook, barrels, firepits, waypoint editors and every other
        /// 0.2 dialog, focused or not. Verified on 1.23 (API + live VSSurvivalMod/VSEssentials IL):
        /// the highest ordinary value is 0.2, the world map is 0.11, the escape menu is 0.89, the
        /// held-item cursor (<c>HudMouseTools</c>) 0.9, the auction dialogs 1.0, and
        /// <c>GuiDialogConfirm</c> 2.0 - so the core-mod confirmation this dialog opens still lands on
        /// top of it. <c>RegisterDialog</c> also sorts by draw order within an input-order group, so
        /// this dialog is offered the mouse before the 0.2 dialogs it now draws over.
        /// </para>
        /// </summary>
        public override double DrawOrder => 0.25;

        /// <summary>
        /// Depth budget for this dialog, up from the default 150.
        /// <para>
        /// <c>GuiManager.OnRenderFrameGUI</c> translates z by each dialog's <c>ZSize</c> after drawing
        /// it, so the next dialog up starts that far in front. The version picker draws at z 310/315
        /// inside this dialog; with 150, a dialog stacked above this one (its background at
        /// 150 + 50) would sit BEHIND the open picker and the picker would show through it. 400
        /// clears 315 with room for a stacked dialog's own low-z draws.
        /// </para>
        /// </summary>
        public override float ZSize => 400f;

        public GuiDialogModProfiles(ICoreClientAPI capi)
            : base(capi)
        {
        }

        /// <summary>
        /// Id of the profile currently being viewed/edited. Self-heals to the active profile when it
        /// is unset or names a profile that no longer exists.
        /// </summary>
        private string ViewedProfileId
        {
            get
            {
                if (viewedProfileId != null && ProfileStore.FindProfile(viewedProfileId) != null) return viewedProfileId;

                viewedProfileId = ProfileStore.ActiveProfile?.Id;
                return viewedProfileId;
            }
        }

        public override void OnGuiOpened()
        {
            // A mod installed or enabled from in-game writes ClientSettings directly
            // (ModDbUtil.MaybeEnableMod), and the four points that fold such a change into the active
            // profile all sit on the menu / world-start path - none of them covers a dialog opened
            // afterwards. Without this the rows would render from the profile and show it as off.
            try
            {
                ProfileStore.ReconcileActiveFromClientSettings();
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Error("[BetterMods] Could not reconcile the active profile on opening the dialog.");
                BetterModsModSystem.Logger?.Error(e);
            }

            // A genuine open is never mid-click, so it is the safe place to clear a gate whose
            // mouse-up went somewhere else entirely. See PopupClickGate.
            PopupClickGate.Release();

            // Markers only: installing happens on the main-menu screen. The check itself is the
            // session's one check - already answered if the menu screen was opened before this world,
            // started here otherwise (a cold launch without the startup hook has a vanilla menu).
            try
            {
                ModUpdateService.Changed -= OnUpdatesChanged;
                ModUpdateService.Changed += OnUpdatesChanged;
                ModUpdateService.EnsureChecked();
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not start the mod update check: " + e.Message);
            }

            // Opening always lands on the active profile, matching the main-menu screen.
            viewedProfileId = ProfileStore.ActiveProfile?.Id;
            fixSummary = null;
            showShortcutResult = false;

            RefreshInstalledMods();

            // Every open starts centred. The title bar's "Movable" mode stores a position under the
            // composer name and re-applies it on every compose (GuiElementDialogTitleBar
            // .SetUpMovableState), which would otherwise pin the dialog wherever it was last dragged -
            // part off-screen after a resize or a GUI-scale change. Cleared on OPEN only: a recompose
            // while open (tab switch, version pick) keeps a position dragged this session.
            try
            {
                capi.Gui.SetDialogPosition(ComposerName, null);
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not reset the profiles dialog position: " + e.Message);
            }

            // A fresh open, so no scroll to carry over - the dialog is not disposed when it closes,
            // and its old composer would otherwise still be holding the last session's position.
            Compose(preserveScroll: false);

            base.OnGuiOpened();
        }

        public override void OnGuiClosed()
        {
            ModUpdateService.Changed -= OnUpdatesChanged;
            base.OnGuiClosed();
        }

        /// <summary>
        /// The update check answered. Rows bake their markers into the cell textures, so the dialog is
        /// recomposed - unless a popup is open, which a recompose would close; the markers then show on
        /// the next recompose instead. Raised on the main thread, never from inside a mouse handler.
        /// </summary>
        private void OnUpdatesChanged()
        {
            if (!IsOpened()) return;

            GuiComposer composer = SingleComposer;
            if (composer?.GetElement(VersionMenuKey) is GuiElementVersionMenu menu && menu.IsOpened) return;
            if (composer?.GetElement(RenamePanelKey) is GuiElementRenamePanel panel && panel.IsOpen) return;

            RecomposeDeferred();
        }

        // ---------------------------------------------------------------- composition

        private void RefreshInstalledMods()
        {
            installedMods.Clear();

            try
            {
                installedMods.AddRange(InstalledModScanner.Scan(capi));
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Error("[BetterMods] Could not build the installed mod list.");
                BetterModsModSystem.Logger?.Error(e);
            }
        }

        /// <summary>
        /// (Re)builds the whole dialog. Every refresh trigger - tab switch, new profile, rename,
        /// version pick - goes through here, because the version label and the on/off state are
        /// baked into each cell's texture. <paramref name="preserveScroll"/> is what keeps the row
        /// the player just clicked where it was instead of snapping the list back to the top.
        /// </summary>
        private void Compose(bool preserveScroll = true)
        {
            try
            {
                // Read before the old composer is disposed further down.
                float scroll = preserveScroll ? ModListScroll.Capture(SingleComposer, ScrollbarKey) : 0f;

                List<ModProfile> profiles = ProfileStore.Profiles;
                string viewedId = ViewedProfileId;

                string[] names = new string[profiles.Count + 1];
                int activeIndex = -1;
                for (int i = 0; i < profiles.Count; i++)
                {
                    names[i] = profiles[i].Name;
                    if (profiles[i].Id == viewedId) activeIndex = i;
                }
                names[profiles.Count] = "+";

                // RuntimeEnv.GUIScale is what GuiElement.scaled multiplies by (on 1.22 it equals
                // ClientSettings.GUIScale; 1.23 folds the display's DPI scale into it).
                double guiScale = RuntimeEnv.GUIScale;
                if (!(guiScale > 0.01)) guiScale = 1.0;

                double listHeight = Math.Clamp((double)capi.Render.FrameHeight / guiScale - 260.0, 200.0, 520.0);

                // The height was already clamped against the window; the width was not, so at GUI
                // scale 2 a fixed 700 unscaled is 1400 real pixels and hangs off both sides of a
                // 1280-wide window, taking Apply Profile and Close off the edges with it.
                double listWidth = Math.Clamp(
                    (double)capi.Render.FrameWidth / guiScale - 2.0 * GuiStyle.ElementToDialogPadding - 40.0,
                    MinListWidth,
                    PreferredListWidth);

                ElementBounds tabBounds = ElementBounds.Fixed(0.0, 30.0, listWidth, TabRowHeight);
                ElementBounds insetBounds = ElementBounds.Fixed(0.0, 0.0, listWidth, listHeight).FixedUnder(tabBounds, 4.0);
                clipBounds = insetBounds.ForkContainingChild(3.0, 3.0, 3.0, 3.0);
                listBounds = clipBounds.ForkContainingChild(0.0, 0.0, 0.0, -3.0).WithFixedPadding(5.0);

                ElementBounds noticeBounds = ElementBounds.Fixed(0.0, 0.0, listWidth, 22.0).FixedUnder(insetBounds, 8.0);
                ElementBounds buttonBounds = ElementBounds.FixedSize(60.0, 30.0).WithFixedPadding(10.0, 2.0).FixedUnder(noticeBounds, 6.0);

                ElementBounds bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
                bgBounds.BothSizing = ElementSizing.FitToChildren;

                GuiElementProfileTabs tabs = new GuiElementProfileTabs(capi, tabBounds, names, activeIndex, OnTabAction, TabScrollKey);
                GuiElementVersionMenu versionMenu = new GuiElementVersionMenu(capi, CairoFont.WhiteSmallText());

                // Built before the composer chain rather than inline in AddCellList, because the
                // notice below is derived from the groups this call publishes.
                List<ModCellEntry> cells = BuildCells();

                // Built here rather than inline so nothing is constructed when the card is off.
                GuiElementModCardTooltip tooltip = ModCellLayout.CardLayoutAvailable ? CreateTooltip() : null;

                // Seven things the one notice line can say, most severe first. Read-only means
                // nothing the player does reaches the disk at all; the outcome of a "Fix dependencies"
                // the player just pressed is the direct answer to a click (it carries the restart
                // sentence when the fix left a resident mod at another version - the same
                // LoadedModVersions test, not a second one); a restart notice means the profile is fine
                // but the game cannot act on part of it until it is restarted; the update hint points
                // at the main-menu screen, the only place updates are installed from. Add Shortcut's
                // outcome is a direct answer to a click, like the fix summary; the startup hint is the
                // least important thing there is to say, so it only ever replaces the default line
                // (BuildCells, just above, refreshed the shortcut state it reads).
                bool readOnly = ProfileStore.PersistenceSuspended;
                string shortcut = !showShortcutResult ? null
                    : StartupOffer.LastError != null ? "Add Shortcut failed: " + StartupOffer.LastError
                    : StartupOffer.LastSucceeded ? "Created \"" + DesktopShortcut.ShortcutName + "\" on your desktop. Start the game from it to have profiles on the main menu."
                    : null;
                string restart = readOnly ? null : RestartNotice();
                string fix = readOnly || fixSummary == null
                    ? null
                    : fixSummary + (restart != null ? LoadedModVersions.AppliedSuffix : "");
                string updates = readOnly || restart != null || fix != null ? null : ModUpdateUi.InGameHint(groupsByModId.Values);

                // Only where the card - and so the button and its tooltip the hint points at - is drawn.
                string hint = ModCellLayout.CardLayoutAvailable && StartupOffer.ShowHint ? StartupOffer.Hint : null;

                string notice = readOnly ? ModsScreenProfileUi.ReadOnlyNotice : shortcut ?? fix ?? restart ?? updates ?? hint ?? DefaultNotice;
                CairoFont noticeFont =
                    readOnly ? CairoFont.WhiteDetailText().WithColor(GuiStyle.ErrorTextColor)
                    : shortcut != null ? CairoFont.WhiteDetailText().WithColor(StartupOffer.LastError != null ? GuiStyle.ErrorTextColor : GuiStyle.SuccessTextColor)
                    : fix != null ? CairoFont.WhiteDetailText().WithColor(fixSummaryIsError || restart != null ? GuiStyle.WarningTextColor : GuiStyle.SuccessTextColor)
                    : restart != null ? CairoFont.WhiteDetailText().WithColor(GuiStyle.WarningTextColor)
                    : updates != null ? CairoFont.WhiteDetailText().WithColor(GuiStyle.SuccessTextColor)
                    : CairoFont.WhiteDetailText();

                SingleComposer?.Dispose();
                GuiComposer composer = capi.Gui.CreateCompo(ComposerName, ElementStdBounds.AutosizedMainDialog)
                    .AddShadedDialogBG(bgBounds)
                    .AddDialogTitleBar("Mod Profiles", OnTitleBarClose)
                    .BeginChildElements(bgBounds)
                        .AddInteractiveElement(tabs, TabsKey)
                        .AddInset(insetBounds)
                        .AddVerticalScrollbar(OnNewScrollbarValue, ElementStdBounds.VerticalScrollbar(insetBounds), ScrollbarKey)
                        .BeginClip(clipBounds)
                        .AddCellList(listBounds, CreateModCell, cells, ListKey)
                        .EndClip()
                        .AddIf(tooltip != null)
                            // The cards' hover tooltip; clipped by its own test against clipBounds,
                            // like vanilla's singleplayer tooltips. See GuiElementModCardTooltip.
                            .AddInteractiveElement(tooltip, GuiElementModCardTooltip.ElementKey)
                        .EndIf()
                        .AddDynamicText(notice, noticeFont, noticeBounds, NoticeKey)
                        .AddSmallButton("Apply Profile", OnApplyProfile, buttonBounds.FlatCopy().WithAlignment(EnumDialogArea.LeftFixed))
                        .AddSmallButton("Close", OnCloseButton, buttonBounds.FlatCopy().WithAlignment(EnumDialogArea.RightFixed))
                        // Added last and after EndClip: the picker draws over the rows, must not be
                        // scissored by the list, and must be the last element offered a mouse event.
                        .AddInteractiveElement(versionMenu, VersionMenuKey)
                    .EndChildElements();

                // Before Compose, so the FitToChildren dialog background never sizes itself around
                // a popup that positions itself in raw screen pixels.
                versionMenu.DetachBounds();

                // After everything else and after EndClip, same reasoning as the version picker: it
                // draws over the rows and takes the last look at every mouse event. AddTo detaches
                // its own bounds and those of the text field it hosts.
                GuiElementRenamePanel.AddTo(composer, RenamePanelKey);

                SingleComposer = composer.Compose();

                // Same tail as the vanilla mods screen: the cell list decides its own height, so the
                // scrollbar can only be sized once the bounds have been calculated.
                listBounds.CalcWorldBounds();
                clipBounds.CalcWorldBounds();
                SingleComposer.GetScrollbar(ScrollbarKey).SetHeights((float)clipBounds.fixedHeight, (float)listBounds.fixedHeight);

                // After SetHeights, never before: the restore is clamped against the heights it
                // just published, and the list may well be shorter than the one we measured.
                ModListScroll.Restore(SingleComposer, ScrollbarKey, clipBounds, listBounds, scroll);
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Error("[BetterMods] Could not compose the mod profiles dialog.");
                BetterModsModSystem.Logger?.Error(e);
            }
        }

        /// <summary>
        /// One cell per installed MOD ID - every installed version of the same mod shares a row -
        /// switched on when the VIEWED profile does not disable all of them.
        /// Mirrors <c>GuiScreenMods.LoadModCells</c> minus the blocklist round trip: in-game the
        /// blocklist may not have been fetched, and <c>ModContainer.Error</c> already carries the
        /// verdict for everything the game itself loaded.
        /// </summary>
        private List<ModCellEntry> BuildCells()
        {
            groupsByModId.Clear();
            List<ModCellEntry> cells = new List<ModCellEntry>();

            // Every group is published before any card is dressed: a card's dependency state reads
            // the whole set.
            List<ModVersionGroup> groups = new List<ModVersionGroup>();
            foreach (ModVersionGroup group in ModGrouping.Build(installedMods, ViewedProfileId))
            {
                if (group.Selected?.Info == null) continue;

                groupsByModId[group.ModId] = group;
                groups.Add(group);
            }

            ModCardActions actions = new ModCardActions
            {
                OpenPage = OpenModPage,
                FixDependencies = FixDependencies,
                AddShortcut = AddShortcut,
                CanDownload = false,
                Dependencies = DependencyFixService.BuildView(groups)
            };

            foreach (ModVersionGroup group in groups)
            {
                ModContainer mod = group.Selected;
                ModInfo info = mod.Info;

                CairoFont detailFont = CairoFont.WhiteDetailText().WithFontSize((float)GuiStyle.SmallFontSize);

                ModCellEntry entry = new ModCellEntry
                {
                    Title = info.Name + " (" + info.Type + ")",
                    // Elided, because the trigger box is sized around this string and an opaque box
                    // as wide as an exotic version string is a slab over the whole row.
                    RightTopText = group.HasVersionChoice
                        ? ModCellLayout.ElideVersionLabel(group.VersionLabel, detailFont)
                        : group.VersionLabel,
                    RightTopOffY = 3f,
                    // The card shows only a load error on its face; everything else is in the
                    // tooltip ModCardContent builds. A vanilla row (no card on this build) keeps it all.
                    DetailText = ModCellLayout.CardLayoutAvailable ? LoadErrorLine(mod) : DetailTextFor(mod, info),
                    Enabled = group.Enabled,
                    Mod = mod,
                    DetailTextFont = detailFont
                };

                // Earns the row a drawn dropdown trigger around its version; a single-version row
                // keeps the plain label vanilla would have drawn.
                if (group.HasVersionChoice) ModCellLayout.MarkVersionPicker(entry);

                // Same card as the menu screen, minus the Update buttons: nothing is installed in game,
                // and "Fix dependencies" only switches on what is already installed.
                ModCardContent.Apply(entry, group, actions);

                cells.Add(entry);
            }

            return cells;
        }

        private static string DetailTextFor(ModContainer mod, ModInfo info)
        {
            StringBuilder text = new StringBuilder();

            if (info.Authors.Count > 0) text.AppendLine(string.Join(", ", info.Authors));
            if (!string.IsNullOrEmpty(info.Description)) text.AppendLine(info.Description);

            string error = LoadErrorLine(mod);
            if (error.Length > 0) text.AppendLine(error);

            return text.ToString().Trim();
        }

        private static string LoadErrorLine(ModContainer mod)
        {
            return mod?.Error.HasValue == true ? "This mod could not be loaded (" + mod.Error.Value + ")." : "";
        }

        /// <summary>
        /// The cards' hover tooltip. Hidden while either popup is open, and while any ordinary dialog
        /// drawn above this one (a confirmation, the escape menu) is open: the tooltip draws at a z
        /// well past this dialog's depth budget, so it would otherwise show through them.
        /// </summary>
        private GuiElementModCardTooltip CreateTooltip()
        {
            return new GuiElementModCardTooltip(
                capi, ElementBounds.Fixed(0.0, 0.0, 1.0, 1.0), clipBounds,
                () => SingleComposer?.GetCellList<ModCellEntry>(ListKey),
                TooltipSuppressed);
        }

        private bool TooltipSuppressed()
        {
            if (!IsOpened()) return true;

            GuiComposer composer = SingleComposer;
            if (composer?.GetElement(VersionMenuKey) is GuiElementVersionMenu menu && menu.IsOpened) return true;
            if (composer?.GetElement(RenamePanelKey) is GuiElementRenamePanel panel && panel.IsOpen) return true;

            List<GuiDialog> opened = capi.Gui.OpenedGuis;
            if (opened == null) return false;

            foreach (GuiDialog dialog in opened)
            {
                if (dialog == null || dialog == this || dialog.DialogType != EnumDialogType.Dialog) continue;
                if (dialog.DrawOrder > DrawOrder) return true;
            }

            return false;
        }

        /// <summary>
        /// A card's "Open mod page" button: the address is resolved (<see cref="ModPageLinks"/>), then,
        /// by <see cref="ModPageLinks.Classify"/>: a trusted host opens in the browser straight away; an
        /// untrusted one gets <see cref="GuiDialogOpenModPage"/> (Cancel / Always trust &lt;host&gt; /
        /// Open); anything else goes through the game's own <c>Gui.OpenLink</c> - vanilla's
        /// <c>GuiDialogConfirm</c>, as before. The callback is posted, so nothing here runs inside a
        /// mouse handler. Dropped if the dialog was closed by the time the address arrives.
        /// </summary>
        private void OpenModPage(string modId)
        {
            ModPageLinks.Open(modId, url =>
            {
                if (!IsOpened()) return;

                switch (ModPageLinks.Classify(url, out string host))
                {
                    case ModPageTrust.Trusted:
                        if (!ModPageLinks.OpenTrusted(url)) BetterModsModSystem.Logger?.Warning("[BetterMods] The mod page was not opened: " + url);
                        return;

                    case ModPageTrust.Untrusted:
                        new GuiDialogOpenModPage(capi, url, host).TryOpen();
                        return;

                    default:
                        capi.Gui.OpenLink(url);
                        return;
                }
            });
        }

        /// <summary>
        /// A card's "Fix dependencies", in game: switches on the newest installed versions that
        /// satisfy the mod's dependencies, transitively, in the VIEWED profile - the profile file only,
        /// like every toggle here. Anything that would need a download is reported as such (the main
        /// menu is the only place mods are installed from). Runs inline from the row's mouse-up; the
        /// recompose that shows the result is deferred.
        /// </summary>
        private void FixDependencies(ModVersionGroup group)
        {
            if (group == null) return;

            DependencyFixService.Start(group.ModId, ViewedProfileId, new DependencyFixSurface
            {
                Groups = profileId => ModGrouping.Build(InstalledModScanner.Scan(capi), profileId),
                CanDownload = false,
                Report = (text, isError) =>
                {
                    fixSummary = ModUpdateRules.AsciiLine(text, 300);
                    fixSummaryIsError = isError;
                    RecomposeDeferred();
                }
            });

            capi.Gui.PlaySound("menubutton");
        }

        /// <summary>The group behind a composed row, or null if the row is not one of ours.</summary>
        private ModVersionGroup GroupFor(ModContainer mod)
        {
            string modId = InstalledModScanner.ModIdOf(mod);
            if (string.IsNullOrEmpty(modId)) return null;

            return groupsByModId.TryGetValue(modId, out ModVersionGroup group) ? group : null;
        }

        private IGuiElementCell CreateModCell(ModCellEntry cell, ElementBounds bounds)
        {
            GuiElementModCell element = new GuiElementModCell(capi, cell, bounds, WarningIcon())
            {
                On = cell.Enabled
            };

            // Vanilla leaves blocked mods unswitchable; a profile must not be able to re-enable one.
            if (cell.Mod?.Error != ModError.Blocked)
            {
                element.OnMouseDownOnCellRight = OnToggleCell;
            }

            // The left half is inert in vanilla (an empty stub on 1.22, not wired at all on 1.23), so it is
            // free real estate - toggling from anywhere on the row is friendlier with a mouse-look
            // cursor. It is also where the version area lives, which a blocked mod still needs, so
            // this half is wired up unconditionally and the handler re-checks the block.
            element.OnMouseDownOnCellLeft = OnToggleCell;

            return element;
        }

        /// <summary>The vanilla version-mismatch icon. Resolved once; null is tolerated by the cell.</summary>
        private IAsset WarningIcon()
        {
            if (warningIconResolved) return warningIcon;
            warningIconResolved = true;

            try
            {
                warningIcon = capi.Assets.Get(new AssetLocation("textures/icons/warning.svg"));
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not load the warning icon: " + e.Message);
                warningIcon = null;
            }

            return warningIcon;
        }

        // ---------------------------------------------------------------- handlers

        private void OnNewScrollbarValue(float value)
        {
            ElementBounds bounds = SingleComposer.GetCellList<ModCellEntry>(ListKey).Bounds;
            bounds.fixedY = 0f - value;
            bounds.CalcWorldBounds();
        }

        private void OnToggleCell(int cellIndex)
        {
            try
            {
                // The click that a popup swallowed landed on this row by accident.
                if (PopupClickGate.ClickConsumed) return;

                GuiElementCellList<ModCellEntry> list = SingleComposer.GetCellList<ModCellEntry>(ListKey);
                if (cellIndex < 0 || cellIndex >= list.elementCells.Count) return;

                GuiElementModCell cell = list.elementCells[cellIndex] as GuiElementModCell;
                ModVersionGroup group = GroupFor(cell?.cell?.Mod);
                if (group == null) return;

                // The card's button row comes first, and consumes the click whether or not the
                // button did anything (an "x available" label is information, not a control) - the
                // whole left half toggles here, so a click aimed at a button must not also switch
                // the mod.
                if (ModCard.TryClick(cell, capi.Input.MouseX, capi.Input.MouseY)) return;

                // Only consulted for mods that actually have something to choose between, so a
                // normal row still toggles from anywhere.
                if (group.HasVersionChoice && ModCellLayout.IsInVersionZone(cell, capi.Input.MouseX, capi.Input.MouseY))
                {
                    OpenVersionMenu(cell, group);
                    return;
                }

                if (cell.cell.Mod?.Error == ModError.Blocked) return;

                // Vanilla puts switching a core mod off behind a confirmation on the menu screen
                // (GuiScreenMods.OnClickCellRight, GuiScreenMods.cs:196-208); this dialog had no
                // guard at all, so 'game' or 'survival' went off silently and instantly.
                //
                // The guard is stated against the PROFILE, not against the live mod status: the row
                // is drawn from the viewed profile, so the question is "is this profile about to
                // disable a core mod". Switching one back on is never a warning.
                ModInfo info = cell.cell.Mod?.Info;
                if (cell.On && info != null && info.CoreMod)
                {
                    ConfirmCoreModOff(group, info);
                    return;
                }

                cell.On = !cell.On;
                cell.cell.Enabled = cell.On;

                // Only the profile file changes. ClientSettings and the live ModContainer.Status are
                // deliberately untouched - nothing is being enabled or disabled right now.
                ModGrouping.SetEnabled(ViewedProfileId, group, cell.On);
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Error("[BetterMods] Toggling a mod failed.");
                BetterModsModSystem.Logger?.Error(e);
            }
        }

        /// <summary>
        /// Asks before a profile switches a core mod off, which is the in-game counterpart of
        /// vanilla's <c>GuiScreenMods.ShowConfirmationDialog</c>.
        /// <para>
        /// <c>GuiScreenConfirmAction</c> is internal and menu-only; <c>GuiDialogConfirm</c> is the
        /// public in-game equivalent, and it is opened off the main-thread queue exactly as
        /// <c>GuiAPI.OpenLink</c> does - a dialog must not be brought up from inside a mouse handler.
        /// The vanilla lang key is reused so the wording is the one the player already knows and is
        /// translated.
        /// </para>
        /// <para>
        /// The confirmation writes the PROFILE and recomposes, rather than reaching back for the cell
        /// it came from: by the time the player answers, the dialog may have been rebuilt and that
        /// element disposed. The group's keys are still the right thing to write either way.
        /// </para>
        /// </summary>
        private void ConfirmCoreModOff(ModVersionGroup group, ModInfo info)
        {
            string profileId = ViewedProfileId;

            capi.Event.EnqueueMainThreadTask(() =>
            {
                try
                {
                    new GuiDialogConfirm(capi, Lang.Get("coremod-warning", info.Name), confirmed =>
                    {
                        if (!confirmed) return;

                        ModGrouping.SetEnabled(profileId, group, false);
                        RecomposeDeferred();
                    }).TryOpen();
                }
                catch (Exception e)
                {
                    BetterModsModSystem.Logger?.Error("[BetterMods] Could not open the core-mod confirmation; the mod was left switched on.");
                    BetterModsModSystem.Logger?.Error(e);
                }
            }, "bettermods-coremod-confirm");
        }

        /// <summary>
        /// The "you have to restart" line for the rows currently composed, or null. Derived from the
        /// groups <see cref="BuildCells"/> published, so it is recomputed on every rebuild - which is
        /// what puts it on screen the instant a version is picked, since picking one recomposes.
        /// </summary>
        private string RestartNotice()
        {
            return LoadedModVersions.Describe(LoadedModVersions.ModIdsNeedingRestart(groupsByModId.Values));
        }

        /// <summary>
        /// Drops the version picker out of a row's version text. Picking an entry points the viewed
        /// profile at that version; the version is baked into the cell texture, so the dialog has to
        /// be recomposed to show it.
        /// </summary>
        private void OpenVersionMenu(GuiElementModCell cell, ModVersionGroup group)
        {
            GuiComposer composer = SingleComposer;
            GuiElementVersionMenu menu = composer?.GetElement(VersionMenuKey) as GuiElementVersionMenu;
            if (menu == null) return;

            string profileId = ViewedProfileId;

            bool opened = menu.OpenFor(cell, group.VersionNames(), group.SelectedIndex, index =>
            {
                ModGrouping.SelectVersion(profileId, group, index);
                RecomposeDeferred();
            });
            if (!opened) return;

            // Focus is what makes the composer close the popup again when the next click lands
            // somewhere that handled it, and it is what routes key events at it.
            composer.FocusElement(menu.TabIndex);
            capi.Gui.PlaySound("menubutton");
        }

        private void OnTabAction(int index, EnumMouseButton button)
        {
            try
            {
                List<ModProfile> profiles = ProfileStore.Profiles;

                if (index >= profiles.Count)
                {
                    // The trailing "+" tab: clone whatever is being viewed and switch to it.
                    if (button != EnumMouseButton.Left) return;

                    ModProfile created = ProfileStore.CreateProfile(null, ViewedProfileId);
                    if (created != null) viewedProfileId = created.Id;
                    RecomposeDeferred();
                    return;
                }

                ModProfile profile = profiles[index];

                // Right-click opens rename/delete; middle and the rest are deliberately inert.
                if (button == EnumMouseButton.Right)
                {
                    OpenRenamePanel(index, profile);
                    return;
                }

                if (button != EnumMouseButton.Left) return;

                viewedProfileId = profile.Id;
                fixSummary = null;
                RecomposeDeferred();
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Error("[BetterMods] Profile tab click failed.");
                BetterModsModSystem.Logger?.Error(e);
            }
        }

        /// <summary>
        /// Drops the rename/delete panel out of the tab that was right-clicked. Opening it changes
        /// nothing, so the dialog is only recomposed once the panel reports an actual edit.
        /// </summary>
        private void OpenRenamePanel(int index, ModProfile profile)
        {
            GuiComposer composer = SingleComposer;
            if (composer == null || profile == null) return;

            GuiElementProfileTabs tabs = composer.GetElement(TabsKey) as GuiElementProfileTabs;
            GuiElementRenamePanel panel = composer.GetElement(RenamePanelKey) as GuiElementRenamePanel;
            if (tabs == null || panel == null) return;

            // The tab strip is added before the version picker, so the picker never saw the click
            // that opened this panel and would otherwise stay hanging over the list.
            (composer.GetElement(VersionMenuKey) as GuiElementVersionMenu)?.CloseMenu();

            panel.OpenFor(tabs, index, profile.Id, profile.Name, RecomposeDeferred);
        }

        private bool OnApplyProfile()
        {
            try
            {
                ModProfile profile = ProfileStore.FindProfile(ViewedProfileId);
                ProfileStore.ApplyProfile(ViewedProfileId);

                // A suspended store still applies - ClientSettings is a different file - but the
                // profile the player is editing is not being kept, and that is the more useful thing
                // to have on screen than a confirmation. A restart requirement survives applying, so
                // the confirmation carries it rather than replacing it.
                bool needsRestart = LoadedModVersions.ModIdsNeedingRestart(groupsByModId.Values).Count > 0;

                SingleComposer?.GetDynamicText(NoticeKey)?.SetNewText(
                    ProfileStore.PersistenceSuspended
                        ? ModsScreenProfileUi.ReadOnlyNotice
                        : "Applied \"" + (profile?.Name ?? "") + "\"." +
                          (needsRestart
                              ? LoadedModVersions.AppliedSuffix
                              : " It takes effect the next time you load a world."));
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Error("[BetterMods] Applying profile '" + viewedProfileId + "' failed.");
                BetterModsModSystem.Logger?.Error(e);
            }

            return true;
        }

        /// <summary>
        /// BetterMods' own card's Add Shortcut. Runs from the row's click delegate, inside the
        /// composer's walk, so the recompose that turns the button disabled (and puts the outcome on
        /// the notice line) is deferred.
        /// </summary>
        private void AddShortcut()
        {
            StartupOffer.AddShortcut();
            showShortcutResult = true;
            capi.Gui.PlaySound("menubutton");
            RecomposeDeferred();
        }

        private bool OnCloseButton()
        {
            TryClose();
            return true;
        }

        private void OnTitleBarClose()
        {
            TryClose();
        }

        /// <summary>
        /// Recomposes on the next frame rather than inline. Both callers run from inside
        /// <c>GuiComposer.OnMouseDown</c>, which keeps iterating its element dictionary after the
        /// handler returns - disposing and rebuilding the composer underneath that loop would
        /// invalidate the enumerator.
        /// </summary>
        private void RecomposeDeferred()
        {
            capi.Event.EnqueueMainThreadTask(() => Compose(), "bettermods-recompose");
        }
    }
}
