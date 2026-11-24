using System;
using System.Collections.Generic;
using BetterMods.Profiles;
using BetterMods.Updates;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;
using Vintagestory.Common;

namespace BetterMods.Gui
{
    /// <summary>
    /// Grafts the profile controls onto the vanilla mods screen and owns the "which profile am I
    /// looking at" state.
    /// <para>
    /// Viewing and applying are deliberately separate: selecting a tab only changes which profile
    /// the cell toggles edit. Only <see cref="OnApplyProfile"/> writes <c>ClientSettings</c>.
    /// </para>
    /// <para>
    /// The screen is decorated by a postfix on the (private) <c>InitGui</c> rather than by replacing
    /// it: we shift the vanilla inset down to free a row, add our two elements, and re-compose. That
    /// keeps vanilla's layout authoritative and leaves room for other mods to patch the same method.
    /// </para>
    /// </summary>
    public static class ModsScreenProfileUi
    {
        /// <summary>Height of the tab row, unscaled.</summary>
        public const double TabRowHeight = 30.0;

        /// <summary>Shortest the tab row may be squeezed to on a short list, unscaled.</summary>
        public const double MinTabRowHeight = 20.0;

        /// <summary>Gap between the tab row and the mod list inset, unscaled.</summary>
        public const double TabRowGap = 4.0;

        /// <summary>How far the mod list moves down to make room for a full-height tab row.</summary>
        public const double RowShift = TabRowHeight + TabRowGap;

        /// <summary>
        /// What the player is told when the profile config cannot be written. ASCII only - Cairo's
        /// toy font API does no glyph fallback.
        /// </summary>
        public const string ReadOnlyNotice = "Not saving - config unreadable";

        /// <summary>The tab-row line after a failed Add Shortcut. Short: the slot is 220 wide; the reason is in the card's tooltip.</summary>
        private const string ShortcutFailedNotice = "Add Shortcut failed";

        /// <summary>Room reserved for the tab-row notice, unscaled.</summary>
        private const double NoticeWidth = 220.0;

        // The first three name the same three elements on BOTH surfaces: GuiDialogModProfiles
        // composes its own copies and looks them up by these very keys, so they live here rather
        // than being spelled out twice. The notice key differs - each surface has its own line.
        public const string TabsElementKey = "bettermods-profiletabs";
        public const string VersionMenuKey = "bettermods-versionmenu";
        public const string RenamePanelKey = "bettermods-renamepanel";

        public const string ApplyButtonKey = "bettermods-applyprofile";
        public const string NoticeKey = "bettermods-readonlynotice";
        public const string UpdateButtonKey = "bettermods-updatebutton";
        public const string UpdateStatusKey = "bettermods-updatestatus";

        /// <summary>Clear space between the heading, the update status and the update button, unscaled.</summary>
        private const double HeaderGap = 10.0;

        /// <summary>Narrowest the update status line is worth drawing at, unscaled; below this only the button stays.</summary>
        private const double MinStatusWidth = 60.0;

        /// <summary>
        /// The mods screen the update listener refreshes. The screen is cached by the game
        /// (<c>LoadAndCacheScreen</c>), so this is normally the one instance for the whole session;
        /// <c>IsOpened</c> is checked before it is touched.
        /// </summary>
        private static GuiScreenMods currentScreen;

        private static bool updateListenerInstalled;
        private static bool updateRefreshQueued;

        /// <summary>Which strip the tab element remembers its scroll position under.</summary>
        private const string TabScrollKey = "mainmenu";

        private const string ScrollbarKey = "scrollbar";

        /// <summary>Vanilla's key for the mod cell list (<c>GuiScreenMods.InitGui</c>).</summary>
        private const string CellListKey = "modstable";

        /// <summary>
        /// Narrowest the mod list is taken in to when fitting the dialog beside the sidebar, unscaled.
        /// A row needs the 88 icon + 15 gap on the left and the version slot + on/off column on the
        /// right; below about this the title has no width left to wrap in.
        /// </summary>
        private const double MinInsetWidth = 300.0;

        // Resolved defensively: a renamed field in a future game build must degrade to the vanilla
        // screen, not blow up the main menu.
        private const string BoundsMissing = "the mods screen stays vanilla";

        private static readonly AccessTools.FieldRef<GuiScreenMods, ElementBounds> ClippingBoundsRef =
            GameFields.Ref<GuiScreenMods, ElementBounds>("clippingBounds", BoundsMissing);

        private static readonly AccessTools.FieldRef<GuiScreenMods, ElementBounds> ListBoundsRef =
            GameFields.Ref<GuiScreenMods, ElementBounds>("listBounds", BoundsMissing);

        private static string viewedProfileId;
        private static bool suppressViewResetOnce;

        private static float pendingScroll;
        private static bool hasPendingScroll;

        /// <summary>
        /// The groups behind the rows the mods screen is currently showing, keyed by mod id, as
        /// published by the <c>LoadModCells</c> postfix. Rows and groups are looked up by mod id
        /// rather than by index so a reordered or re-filtered list can never mismatch them.
        /// </summary>
        private static readonly Dictionary<string, ModVersionGroup> GroupsByModId =
            new Dictionary<string, ModVersionGroup>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Id of the profile currently being viewed/edited. Self-heals to the active profile when
        /// it is unset or names a profile that no longer exists.
        /// </summary>
        public static string ViewedProfileId
        {
            get
            {
                if (viewedProfileId != null && ProfileStore.FindProfile(viewedProfileId) != null) return viewedProfileId;

                viewedProfileId = ProfileStore.ActiveProfile?.Id;
                return viewedProfileId;
            }
        }

        public static void SetViewedProfile(string id)
        {
            viewedProfileId = id;
        }

        /// <summary>Puts the view back on the active profile, for when the screen is (re)opened.</summary>
        public static void ResetViewToActive()
        {
            viewedProfileId = ProfileStore.ActiveProfile?.Id;
        }

        /// <summary>
        /// Marks the next <c>OnScreenLoaded</c> as a return trip rather than a fresh open, so it
        /// keeps the current tab AND the current scroll position. Used when we bounce through
        /// another screen (the vanilla core-mod confirmation) or reload the list in place. A genuine
        /// (re)open of the screen deliberately starts at the active profile, at the top of the list.
        /// </summary>
        public static void SuppressNextViewReset()
        {
            suppressViewResetOnce = true;
        }

        public static bool ConsumeViewResetSuppression()
        {
            bool suppressed = suppressViewResetOnce;
            suppressViewResetOnce = false;
            return suppressed;
        }

        // ---------------------------------------------------------------- scroll position

        /// <summary>
        /// Remembers where the mod list is scrolled to, before <c>InitGui</c> throws the composer
        /// away. Every in-place rebuild - tab switch, new profile, version pick, mod toggle that
        /// went through the core-mod confirmation, Reload Mods, Apply Profile, coming back from the
        /// rename screen - funnels through <c>OnScreenLoaded</c>, so capturing there covers them all.
        /// </summary>
        public static void CaptureScroll(GuiScreenMods screen)
        {
            pendingScroll = ModListScroll.Capture(screen?.ElementComposer, ScrollbarKey);
            hasPendingScroll = true;
        }

        /// <summary>Forgets the captured position, for a rebuild that SHOULD land back at the top.</summary>
        public static void DiscardCapturedScroll()
        {
            pendingScroll = 0f;
            hasPendingScroll = false;
        }

        /// <summary>
        /// Puts the rebuilt list back where it was. Runs after vanilla's own <c>SetHeights</c> tail
        /// in <c>OnScreenLoaded</c>, which is the only point at which the new heights are known.
        /// </summary>
        public static void RestoreScroll(GuiScreenMods screen)
        {
            try
            {
                if (!hasPendingScroll) return;

                float value = pendingScroll;
                DiscardCapturedScroll();

                if (screen == null || ClippingBoundsRef == null || ListBoundsRef == null) return;

                ModListScroll.Restore(screen.ElementComposer, ScrollbarKey, ClippingBoundsRef(screen), ListBoundsRef(screen), value);
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not restore the mod list scroll position: " + e.Message);
            }
        }

        // ---------------------------------------------------------------- version groups

        /// <summary>The unmet dependencies of the rows being built, from the groups <see cref="SetGroups"/> last published.</summary>
        private static DependencyView dependencyView;

        /// <summary>Replaces the group map after the cell list has been rebuilt.</summary>
        public static void SetGroups(IEnumerable<ModVersionGroup> groups)
        {
            GroupsByModId.Clear();
            dependencyView = null;
            if (groups == null) return;

            foreach (ModVersionGroup group in groups)
            {
                if (string.IsNullOrEmpty(group?.ModId)) continue;
                GroupsByModId[group.ModId] = group;
            }

            // Once per rebuild, before any card is dressed: every row reads the same snapshot.
            dependencyView = DependencyFixService.BuildView(GroupsByModId.Values);
        }

        /// <summary>The group a row belongs to, or null when the mod could not be grouped.</summary>
        public static ModVersionGroup GroupFor(ModContainer mod)
        {
            string modId = InstalledModScanner.ModIdOf(mod);
            if (string.IsNullOrEmpty(modId)) return null;

            return GroupsByModId.TryGetValue(modId, out ModVersionGroup group) ? group : null;
        }

        /// <summary>
        /// Drops the version picker out of a row's version text. Picking an entry points the viewed
        /// profile at that version and rebuilds the screen — the version is drawn into the cell
        /// texture, so nothing short of a rebuild shows the change.
        /// </summary>
        public static void OpenVersionMenu(GuiScreenMods screen, GuiElementModCell cell, ModVersionGroup group)
        {
            GuiComposer composer = screen?.ElementComposer;
            GuiElementVersionMenu menu = composer?.GetElement(VersionMenuKey) as GuiElementVersionMenu;
            if (menu == null || cell == null || group == null) return;

            string profileId = ViewedProfileId;

            bool opened = menu.OpenFor(cell, group.VersionNames(), group.SelectedIndex, index =>
            {
                ModGrouping.SelectVersion(profileId, group, index);
                MirrorToClientSettingsIfActive(group);
                RebuildDeferred(screen);
            });
            if (!opened) return;

            // Focus is what makes the composer close the popup again when the next click lands
            // somewhere that handled it, and it is what routes key events at it.
            composer.FocusElement(menu.TabIndex);
            composer.Api?.Gui.PlaySound("menubutton");
        }

        /// <summary>
        /// Pushes a group's state into <c>ClientSettings</c> and the live <c>ModContainer.Status</c>
        /// when the profile on screen is the ACTIVE one, which is what vanilla's own toggle handler
        /// does (GuiScreenMods.cs:210-228). Editing any other profile only writes the profile file:
        /// nothing is being enabled or disabled right now.
        /// </summary>
        public static void MirrorToClientSettingsIfActive(ModVersionGroup group)
        {
            MirrorToClientSettingsIfActive(ViewedProfileId, group);
        }

        /// <summary>
        /// <see cref="MirrorToClientSettingsIfActive(ModVersionGroup)"/> for an edit made to
        /// <paramref name="profileId"/> - which, for work that finishes later (a dependency fix waiting
        /// on downloads), need not be the profile on screen any more.
        /// </summary>
        public static void MirrorToClientSettingsIfActive(string profileId, ModVersionGroup group)
        {
            if (group == null) return;
            if (profileId == null || profileId != ProfileStore.ActiveProfile?.Id) return;

            ProfileStore.PushGroupToClientSettings(group.Keys, group.Enabled ? group.SelectedKey : null);

            ModContainer selected = group.Selected;
            foreach (ModContainer version in group.Versions)
            {
                // Vanilla only ever moves a mod between these two; anything else is an error state.
                if (version.Status != ModStatus.Enabled && version.Status != ModStatus.Disabled) continue;

                version.Status = group.Enabled && version == selected ? ModStatus.Enabled : ModStatus.Disabled;
            }
        }

        // ---------------------------------------------------------------- screen decoration

        /// <summary>
        /// Adds the tab row and the Apply Profile button to a freshly composed mods screen.
        /// Never throws: on any failure the player is left with the untouched vanilla screen.
        /// </summary>
        public static void Decorate(GuiScreenMods screen)
        {
            try
            {
                GuiComposer composer = screen?.ElementComposer;
                if (composer == null) return;
                if (ClippingBoundsRef == null || ListBoundsRef == null) return;

                ElementBounds clippingBounds = ClippingBoundsRef(screen);
                ElementBounds listBounds = ListBoundsRef(screen);
                GuiElementScrollbar scrollbar = composer.GetScrollbar(ScrollbarKey);
                if (clippingBounds == null || listBounds == null || scrollbar == null)
                {
                    BetterModsModSystem.Logger?.Warning("[BetterMods] Mods screen layout not recognised, leaving it as vanilla.");
                    return;
                }

                // clippingBounds is forked from the inset bounds, which is itself a child of the
                // content bounds that dialogBase() opened - that is where our elements belong.
                ElementBounds insetBounds = clippingBounds.ParentBounds;
                ElementBounds contentBounds = insetBounds?.ParentBounds;
                if (insetBounds == null || contentBounds == null) return;

                // Before anything is laid out against the inset's width: the tab strip and the
                // notice below are sized from it.
                FitBesideSidebar(screen, composer, contentBounds, insetBounds, clippingBounds, listBounds, scrollbar);

                // The session's one update check starts with the first look at this screen; a no-op
                // on every rebuild after that. Its answer arrives through OnUpdatesChanged.
                currentScreen = screen;
                InstallUpdateListener();
                ModUpdateService.EnsureChecked();

                // Free a row above the list. The inset keeps its bottom edge, so the vanilla button
                // row - which was positioned relative to it at compose time - does not move.
                double shift = RowShiftFor(insetBounds.fixedHeight);
                double tabRowY = insetBounds.fixedY + TabRowGap;
                insetBounds.fixedY += shift;
                insetBounds.fixedHeight -= shift;
                clippingBounds.fixedHeight -= shift;
                scrollbar.Bounds.fixedOffsetY += shift;
                scrollbar.Bounds.fixedHeight -= shift;

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

                // One notice slot, two things worth saying. Read-only wins: nothing the player does
                // here reaches the disk at all, which outranks one mod needing a restart. The slot
                // takes its room out of the tab row, which scrolls, rather than out of the button
                // band under the list - the Apply Profile button is centred there and vanilla's own
                // three buttons fill the rest.
                // A failed Add Shortcut (BetterMods' own card) sits between the two: it answers a
                // click, and the card's tooltip carries the reason. A success needs no line - the
                // button turning disabled says it.
                bool readOnly = ProfileStore.PersistenceSuspended;
                string shortcutFailed = Startup.StartupOffer.LastError != null ? ShortcutFailedNotice : null;
                string notice = readOnly ? ReadOnlyNotice : shortcutFailed ?? RestartNotice();
                double[] noticeColor = readOnly || shortcutFailed != null ? GuiStyle.ErrorTextColor : GuiStyle.WarningTextColor;

                double tabRowHeight = shift - TabRowGap;
                double noticeWidth = notice != null ? Math.Min(NoticeWidth, insetBounds.fixedWidth * 0.5) : 0.0;
                double stripWidth = insetBounds.fixedWidth - (notice != null ? noticeWidth + 8.0 : 0.0);

                ElementBounds tabBounds = ElementBounds.Fixed(EnumDialogArea.LeftTop, insetBounds.fixedX, tabRowY, stripWidth, tabRowHeight);
                ElementBounds applyBounds = ElementBounds.FixedSize(60.0, 30.0)
                    .WithFixedPadding(10.0, 2.0)
                    .WithAlignment(EnumDialogArea.CenterFixed)
                    .FixedUnder(insetBounds, 10.0);

                GuiElementProfileTabs tabs = new GuiElementProfileTabs(
                    composer.Api, tabBounds, names, activeIndex,
                    (index, button) => OnTabAction(screen, index, button), TabScrollKey);

                // Add* is a no-op on an already composed composer, so reopen it first.
                composer.Composed = false;
                composer.AddInteractiveElement(tabs, TabsElementKey);
                composer.AddSmallButton("Apply Profile", () => OnApplyProfile(screen), applyBounds, EnumButtonStyle.Normal, ApplyButtonKey);

                ElementBounds noticeBounds = null;
                if (notice != null)
                {
                    noticeBounds = ElementBounds.Fixed(
                        EnumDialogArea.LeftTop,
                        insetBounds.fixedX + stripWidth + 8.0,
                        tabRowY + Math.Max(0.0, (tabRowHeight - 18.0) / 2.0),
                        noticeWidth,
                        18.0);

                    composer.AddStaticText(
                        notice,
                        CairoFont.WhiteDetailText().WithColor(noticeColor),
                        noticeBounds,
                        NoticeKey);
                }

                // Before the two popups, which must stay the last elements offered a mouse event.
                List<ElementBounds> headerBounds = AddUpdateHeader(screen, composer, contentBounds);

                // The cards' hover tooltip. Outside the list's clip (it is clipped by its own test
                // instead, like vanilla's singleplayer tooltips) and before the popups, which it never
                // competes with: it takes no mouse events and stays hidden while either is open.
                ElementBounds tooltipBounds = AddCardTooltip(screen, composer, clippingBounds);

                // Last, and outside the list's clip: both popups have to draw over the rows and must
                // be the last elements offered a mouse event. Their bounds detach themselves from the
                // layout tree, so the two ReParent calls below do not apply to them.
                // Read when each popup opens, not now, so it follows the GUI scale it is opened at.
                Func<double> clearOfSidebar = () => MenuLayout.ClearLeftPx(screen);
                GuiElementVersionMenu.AddTo(composer, VersionMenuKey).LeftLimit = clearOfSidebar;
                GuiElementRenamePanel.AddTo(composer, RenamePanelKey).LeftLimit = clearOfSidebar;

                // After dialogBase's own handler, so ours has the last word on every recompose -
                // including the ones the engine does by itself after a resize or GUI-scale change.
                // A fresh composer each InitGui, so this never stacks up.
                composer.OnComposed += () => MenuLayout.Centre(screen, composer);

                // InitGui() already closed the content bounds, so the composer parented our bounds
                // to the dialog root. Re-home them: the dialog root sizes itself from its direct
                // children with alignment disabled, which would drop our alignment and offsets.
                ReParent(composer, tabBounds, contentBounds);
                ReParent(composer, applyBounds, contentBounds);
                if (noticeBounds != null) ReParent(composer, noticeBounds, contentBounds);
                if (tooltipBounds != null) ReParent(composer, tooltipBounds, contentBounds);
                foreach (ElementBounds bounds in headerBounds) ReParent(composer, bounds, contentBounds);

                // Same sequence the engine uses when a window resize invalidates a dialog
                // (GuiComposer.PostRender): dirty the whole tree, recalculate it, then recompose.
                // Without the dirty pass Compose() would keep the already-initialised bounds and
                // the list would stay where it was.
                composer.Bounds.MarkDirtyRecursive();
                composer.Bounds.CalcWorldBounds();
                composer.Compose(focusFirstElement: false);

                // Same tail as vanilla InitGui: the list moved, so the scrollbar needs new heights.
                listBounds.CalcWorldBounds();
                clippingBounds.CalcWorldBounds();
                scrollbar.SetHeights((float)clippingBounds.fixedHeight, (float)listBounds.fixedHeight);
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Error("[BetterMods] Could not decorate the mods screen; falling back to vanilla.");
                BetterModsModSystem.Logger?.Error(e);

                // We may have died between reopening the composer and re-composing it. Leaving it
                // half open would leave the screen unrendered, so put it back together.
                try
                {
                    GuiComposer composer = screen?.ElementComposer;
                    if (composer != null && !composer.Composed) composer.Compose(focusFirstElement: false);
                }
                catch (Exception recomposeError)
                {
                    BetterModsModSystem.Logger?.Error(recomposeError);
                }
            }
        }

        /// <summary>
        /// <see cref="LoadedModVersions.ShortNotice"/> when the viewed profile points a mod at a
        /// version other than the one whose assembly this process is already holding, else null.
        /// <para>
        /// Read off the groups the <c>LoadModCells</c> postfix just published, so it is recomputed on
        /// every rebuild - which is also what puts it on screen the moment a version is picked, since
        /// picking one rebuilds the screen. It is a STATE, not an event: it stays up for as long as
        /// the profile is in that shape, and a cold launch that has never loaded a world never shows
        /// it, because nothing is resident to conflict with.
        /// </para>
        /// </summary>
        private static string RestartNotice()
        {
            return LoadedModVersions.ModIdsNeedingRestart(GroupsByModId.Values).Count > 0
                ? LoadedModVersions.ShortNotice
                : null;
        }

        /// <summary>
        /// How much height the tab row may take out of the mod list.
        /// <para>
        /// A flat <see cref="RowShift"/> assumes the inset is tall, and vanilla's is
        /// <c>Max(300, windowHeight) / GUIScale - 190</c> unscaled - 110 at the engine's floor, which
        /// a fixed 34 would cut to 76, shorter than the ~94 minimum row
        /// (<see cref="ModCellLayout.MinUnscaledRowHeight"/>) the icon change raised it to. So the
        /// strip gives way first, down to <see cref="MinTabRowHeight"/>; below that there is no
        /// arrangement that leaves both usable and the strip is what stops shrinking, because a list
        /// the player can still scroll beats a tab row they cannot read.
        /// </para>
        /// </summary>
        private static double RowShiftFor(double insetHeight)
        {
            double minList = ModCellLayout.MinUnscaledRowHeight() + 2.0 * ModCellLayout.UnscaledCellPadding;

            return Math.Clamp(insetHeight - minList, MinTabRowHeight + TabRowGap, RowShift);
        }

        /// <summary>
        /// Narrows the vanilla dialog until it fits in the space right of the sidebar.
        /// <para>
        /// Vanilla sizes the list to <c>Max(400, W * 0.5)</c> screen pixels and the dialog 80 unscaled
        /// wider than that, then centres it in the space beside the 303-unscaled sidebar. Half the
        /// window plus 80 * scale does not fit beside 303 * scale once the window is narrower than
        /// roughly 766 * scale - a small window, a high GUI scale, and since 1.23 a HiDPI display,
        /// whose DPI scale is folded into <c>RuntimeEnv.GUIScale</c>. The overhang is then centred
        /// UNDER the sidebar, which is drawn over it and takes its clicks first.
        /// </para>
        /// <para>
        /// So every width that hangs off the content box is taken in by the same amount: the dialog
        /// background, the content box, the inset, the clip, the cell list, every cell already
        /// created, and the scrollbar's offset (<c>ElementStdBounds.VerticalScrollbar</c> baked the
        /// inset's right edge into it). The bottom buttons are aligned against the content box and
        /// follow it. It never widens anything, and the list never goes below
        /// <see cref="MinInsetWidth"/>: at that point the dialog stays that wide and
        /// <see cref="MenuLayout.CentredLeftPx"/> sits it flush against the sidebar, overhanging the
        /// right edge of the window rather than the sidebar.
        /// </para>
        /// <para>Never throws; on failure the vanilla width is left alone.</para>
        /// </summary>
        private static void FitBesideSidebar(
            GuiScreenMods screen, GuiComposer composer,
            ElementBounds contentBounds, ElementBounds insetBounds,
            ElementBounds clippingBounds, ElementBounds listBounds,
            GuiElementScrollbar scrollbar)
        {
            try
            {
                double guiScale = RuntimeEnv.GUIScale;
                if (!(guiScale > 0.01)) return;

                // dialogBase forks the content box's bounding parent as the dialog background; that
                // parent, not the content box, is what the root sizes itself around.
                ElementBounds backgroundBounds = contentBounds.ParentBounds;
                if (backgroundBounds == null || backgroundBounds == composer.Bounds) return;
                if (backgroundBounds.horizontalSizing != ElementSizing.Fixed || contentBounds.horizontalSizing != ElementSizing.Fixed) return;

                double windowWidth = screen.ScreenManager.GamePlatform.FramebufferSize.Width;
                double available = MenuLayout.AvailableWidthPx(screen, windowWidth);

                double excess = (composer.Bounds.OuterWidth - available) / guiScale;
                if (!(excess > 0.0)) return;

                double shrink = Math.Min(excess, insetBounds.fixedWidth - MinInsetWidth);
                if (!(shrink > 0.0)) return;

                // Fetched before anything is mutated, so a surprise here leaves the widths whole.
                GuiElementCellList<ModCellEntry> cellList = composer.GetCellList<ModCellEntry>(CellListKey);

                backgroundBounds.fixedWidth -= shrink;
                contentBounds.fixedWidth -= shrink;
                insetBounds.fixedWidth -= shrink;
                clippingBounds.fixedWidth -= shrink;
                listBounds.fixedWidth -= shrink;
                scrollbar.Bounds.fixedOffsetX -= shrink;

                // The cells were created by vanilla's own Compose, each with a fixed width copied
                // from the list at that moment (GuiElementCellList.AddCell). None has been drawn yet,
                // so their textures are baked at the new width; and a later ReloadCells (what
                // OnScreenLoaded does next) reads the list's width again by itself.
                if (cellList != null)
                {
                    foreach (IGuiElementCell cell in cellList.elementCells)
                    {
                        if (cell?.Bounds != null) cell.Bounds.fixedWidth -= shrink;
                    }
                }
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not fit the mods screen beside the sidebar: " + e.Message);
            }
        }

        private static void ReParent(GuiComposer composer, ElementBounds bounds, ElementBounds newParent)
        {
            composer.Bounds.ChildBounds.Remove(bounds);
            bounds.ParentBounds = null;
            newParent.WithChild(bounds);
        }

        // ---------------------------------------------------------------- mod updates

        /// <summary>
        /// The update status line and its one button, on the "Installed mods" heading line.
        /// <para>
        /// That line is the only free space on the screen: the bottom row is vanilla's three buttons
        /// plus the centred Apply Profile, already crowded at narrow widths, and the tab row's right end
        /// is the read-only/restart notice slot. Vanilla draws nothing right of the heading, so the
        /// button is right-aligned to the content box (it follows <see cref="FitBesideSidebar"/>
        /// exactly as the bottom buttons do) and the status fills the gap between it and the heading,
        /// right-aligned and elided - dropped altogether when the gap is too small to read.
        /// </para>
        /// <para>Returns the bounds it added, for the caller to re-parent. Never throws.</para>
        /// </summary>
        private static List<ElementBounds> AddUpdateHeader(GuiScreenMods screen, GuiComposer composer, ElementBounds contentBounds)
        {
            List<ElementBounds> added = new List<ElementBounds>();

            try
            {
                double guiScale = RuntimeEnv.GUIScale;
                if (!(guiScale > 0.01)) guiScale = 1.0;

                string status = ModUpdateUi.HeaderStatus(GroupsByModId.Values, out double[] color, out string buttonLabel, out bool installs);

                double headingRight = CairoFont.WhiteSmallishText().GetTextExtents(Lang.Get("Installed mods")).XAdvance / guiScale + HeaderGap;
                double statusRight = contentBounds.fixedWidth;

                if (buttonLabel != null)
                {
                    // AutoBoxSize grows a button to its text's width / scale + 1; padding on top.
                    double buttonWidth = CairoFont.SmallButtonText().GetTextExtents(buttonLabel).Width / guiScale + 1.0 + 2.0 * 8.0;

                    ElementBounds buttonBounds = ElementBounds.FixedSize(0.0, 26.0)
                        .WithFixedPadding(8.0, 2.0)
                        .WithAlignment(EnumDialogArea.RightFixed);
                    buttonBounds.fixedY = 0.0;

                    GuiScreenMods target = screen;
                    composer.AddSmallButton(buttonLabel, () => OnUpdateButton(target, installs), buttonBounds, EnumButtonStyle.Normal, UpdateButtonKey);
                    added.Add(buttonBounds);

                    statusRight -= buttonWidth + HeaderGap;
                }

                double statusWidth = statusRight - headingRight;
                if (!string.IsNullOrEmpty(status) && statusWidth >= MinStatusWidth)
                {
                    CairoFont font = CairoFont.WhiteDetailText().WithOrientation(EnumTextOrientation.Right);
                    if (color != null) font.WithColor(color);

                    string line = ModCellLayout.Elide(ModUpdateRules.AsciiLine(status, 200), font, GuiElement.scaled(statusWidth));

                    ElementBounds statusBounds = ElementBounds.Fixed(EnumDialogArea.LeftTop, headingRight, 7.0, statusWidth, 18.0);
                    composer.AddStaticText(line, font, statusBounds, UpdateStatusKey);
                    added.Add(statusBounds);
                }
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not show the mod update status: " + e.Message);
            }

            return added;
        }

        /// <summary>
        /// The header button: install every offered update for the profile on screen, or check again.
        /// A button handler, so the rebuild that shows the new state is deferred like every other one
        /// (the service raises its change on the next frame).
        /// </summary>
        private static bool OnUpdateButton(GuiScreenMods screen, bool install)
        {
            try
            {
                if (install) StartUpdates(screen, new List<ModVersionGroup>(GroupsByModId.Values));
                else ModUpdateService.Recheck();
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Error("[BetterMods] The mod update button failed.");
                BetterModsModSystem.Logger?.Error(e);
            }

            return true;
        }

        // ---------------------------------------------------------------- mod cards

        /// <summary>
        /// Dresses one row for this screen: tooltip, update marker, "Open mod page" and - this is the
        /// one surface that installs - the "Update to x" button. Called from the <c>LoadModCells</c>
        /// postfix, before the cell exists. Never throws.
        /// </summary>
        public static void DressCard(GuiScreenMods screen, ModCellEntry cell, ModVersionGroup group)
        {
            ModCardContent.Apply(cell, group, new ModCardActions
            {
                Install = target => StartRowUpdate(screen, target),
                InstallOlderRelease = target => ConfirmOlderRelease(screen, target),
                OpenPage = modId => OpenModPage(screen, modId),
                FixDependencies = target => FixDependencies(screen, target),
                AddShortcut = () => AddShortcut(screen),
                CanDownload = true,
                Dependencies = dependencyView
            });
        }

        /// <summary>
        /// A card's warning-coloured "Update to x (1.22)": a release tagged only for an older game
        /// version. Never installed without asking - the question is a screen of its own, pushed on the
        /// next frame (this runs from the row's mouse-up, inside the composer's walk). The profile is
        /// captured now, at the click, like every other install.
        /// </summary>
        private static void ConfirmOlderRelease(GuiScreenMods screen, ModVersionGroup group)
        {
            UpdateOffer offer = ModUpdateService.OfferFor(group);
            if (offer == null || !offer.IsOlderGameVersionOffer || offer.Installing) return;

            string modId = group.ModId;
            string profileId = ViewedProfileId;
            string target = ModUpdateRules.AsciiLine(offer.TargetVersion, 40);
            string older = ModUpdateRules.AsciiLine(offer.OlderGameVersion, 30);
            string running = ModUpdateRules.AsciiLine(ModUpdateService.GameVersionChecked, 30);
            string name = ModUpdateRules.AsciiLine(group.Selected?.Info?.Name ?? modId, 60);

            string body =
                "<strong>" + Vtml(name) + " " + Vtml(target) + "</strong> is tagged for game version <strong>" + Vtml(older) +
                "</strong>, not for " + Vtml(running) + ". It has not been marked as tested on this version and may not work.\n\n" +
                (offer.Retracted ? "The installed release was retracted on the mod DB" +
                                   (string.IsNullOrEmpty(offer.RetractionReason) ? "." : ": " + Vtml(offer.RetractionReason)) + "\n\n" : "") +
                "Only the profile you are viewing switches to it; every other profile keeps the version it has. " +
                "The installed version stays on disk.\n\nInstall it anyway?";

            ShowConfirm(screen, "Install a release made for " + older + "?", body, "Install", confirmed =>
            {
                if (!confirmed) return;

                // Re-read after the return trip: OnScreenLoaded republished the groups.
                if (!GroupsByModId.TryGetValue(modId, out ModVersionGroup fresh)) fresh = group;
                if (ModUpdateService.StartInstall(new List<ModVersionGroup> { fresh }, profileId, includeOlderGameVersion: true) > 0)
                {
                    screen?.ElementComposer?.Api?.Gui.PlaySound("menubutton");
                }
            });
        }

        /// <summary>
        /// BetterMods' own card's "Add Shortcut". Runs from the row's click delegate, inside the
        /// composer's walk, so the rebuild that turns the button disabled (or puts a failure in the
        /// tab-row notice slot) is deferred.
        /// </summary>
        private static void AddShortcut(GuiScreenMods screen)
        {
            Startup.StartupOffer.AddShortcut();
            screen?.ElementComposer?.Api?.Gui.PlaySound("menubutton");
            RebuildDeferred(screen);
        }

        /// <summary>
        /// A card's "Fix dependencies": the installed part runs now (profile writes, mirrored into
        /// <c>ClientSettings</c> when the viewed profile is the active one - what this screen's own
        /// toggle does), downloads follow in the background. The rebuild that shows the result is
        /// deferred like every other one (the outcome is reported through the update status line,
        /// which raises its change on the next frame).
        /// </summary>
        private static void FixDependencies(GuiScreenMods screen, ModVersionGroup group)
        {
            if (group == null) return;

            string profileId = ViewedProfileId;
            bool started = DependencyFixService.Start(group.ModId, profileId, new DependencyFixSurface
            {
                Groups = MenuGroupsFor,
                AfterProfileEdit = MirrorToClientSettingsIfActive,
                CanDownload = true,
                Confirm = (title, body, label, answer) => ShowConfirm(currentScreen, title, body, label, answer),
                Report = ModUpdateService.ReportSummary
            });

            if (started) screen?.ElementComposer?.Api?.Gui.PlaySound("menubutton");
        }

        /// <summary>Every installed mod, grouped for <paramref name="profileId"/> - what this screen's rows are built from.</summary>
        private static List<ModVersionGroup> MenuGroupsFor(string profileId)
        {
            List<ModContainer> mods = new List<ModContainer>();
            foreach (ModContainer mod in InstalledModScanner.MenuMods(ClientProgram.screenManager, out bool _))
            {
                if (mod?.Info != null) mods.Add(mod);
            }

            return ModGrouping.Build(mods, profileId);
        }

        /// <summary>
        /// Asks a yes/no question over the mods screen, on the next frame, and comes back to it with
        /// tab and scroll kept (a re-entry, like the external-link confirmation). The answer runs after
        /// the mods screen is back. Answers false - without asking - when the mods screen is not the
        /// one open any more. Never throws.
        /// </summary>
        private static void ShowConfirm(GuiScreenMods screen, string title, string body, string confirmLabel, Action<bool> answer)
        {
            ScreenManager.EnqueueMainThreadTask(() =>
            {
                try
                {
                    if (screen == null || !screen.IsOpened)
                    {
                        answer?.Invoke(false);
                        return;
                    }

                    ScreenManager manager = screen.ScreenManager;
                    manager.LoadScreen(new GuiScreenBetterModsConfirm(title, body, confirmLabel, confirmed =>
                    {
                        try
                        {
                            SuppressNextViewReset();
                            manager.LoadScreen(screen);
                        }
                        catch (Exception e)
                        {
                            BetterModsModSystem.Logger?.Error("[BetterMods] Could not return to the mods screen.");
                            BetterModsModSystem.Logger?.Error(e);
                        }

                        answer?.Invoke(confirmed);
                    }, manager, screen));
                }
                catch (Exception e)
                {
                    BetterModsModSystem.Logger?.Error("[BetterMods] Could not show a confirmation.");
                    BetterModsModSystem.Logger?.Error(e);

                    try
                    {
                        answer?.Invoke(false);
                    }
                    catch (Exception inner)
                    {
                        BetterModsModSystem.Logger?.Error(inner);
                    }
                }
            });
        }

        private static string Vtml(string text)
        {
            return string.IsNullOrEmpty(text) ? "" : text.Replace("<", "&lt;").Replace(">", "&gt;");
        }

        /// <summary>
        /// A card's "Update to x" button. Runs from the row's left-click delegate, i.e. inside the
        /// composer's element walk - nothing here rebuilds inline (the service raises its change on
        /// the next frame).
        /// </summary>
        private static void StartRowUpdate(GuiScreenMods screen, ModVersionGroup group)
        {
            if (group == null) return;
            StartUpdates(screen, new List<ModVersionGroup> { group });
        }

        /// <summary>
        /// A card's "Open mod page" button. The page address is resolved on the next frame or two
        /// (<see cref="ModPageLinks"/>), then, by <see cref="ModPageLinks.Classify"/>:
        /// <list type="bullet">
        /// <item><b>Trusted</b> host - opens in the browser straight away; the screen stays put.</item>
        /// <item><b>Untrusted</b> - BetterMods' own question (Cancel / Always trust &lt;host&gt; / Open),
        /// then back to this screen.</item>
        /// <item><b>Untrustable</b> - the game's own <c>Gui.OpenLink</c>, i.e. vanilla's confirmation
        /// screen, exactly as before.</item>
        /// </list>
        /// Both questions return with <c>LoadScreen(this)</c> - a re-entry, so it must keep the tab and
        /// the scroll position like every other bounce through another screen. Dropped if the player
        /// has left this screen by the time the address arrives.
        /// </summary>
        private static void OpenModPage(GuiScreenMods screen, string modId)
        {
            ModPageLinks.Open(modId, url =>
            {
                if (screen == null || !screen.IsOpened) return;

                switch (ModPageLinks.Classify(url, out string host))
                {
                    case ModPageTrust.Trusted:
                        if (!ModPageLinks.OpenTrusted(url)) BetterModsModSystem.Logger?.Warning("[BetterMods] The mod page was not opened: " + url);
                        return;

                    case ModPageTrust.Untrusted:
                        AskToOpenModPage(screen, url, host);
                        return;

                    default:
                        SuppressNextViewReset();
                        screen.ScreenManager.api.Gui.OpenLink(url);
                        return;
                }
            });
        }

        /// <summary>
        /// The first-click question for a mod page on a host the player has not trusted yet. Already
        /// on the main thread, outside any mouse handler (<see cref="ModPageLinks.Open"/> posts its
        /// callback), so the screen swap is safe here.
        /// </summary>
        private static void AskToOpenModPage(GuiScreenMods screen, string url, string host)
        {
            ScreenManager manager = screen.ScreenManager;
            string body = "Open this mod page in your browser?\n\n" + Vtml(url) +
                          "\n\n<font opacity=\"0.6\">Always trust " + Vtml(host) + " opens mod pages on " + Vtml(host) +
                          " without asking from now on. Remove it from TrustedLinkHosts in bettermods.json to undo.</font>";

            manager.LoadScreen(new GuiScreenBetterModsConfirm("Open mod page", body, "Open", "Always trust " + host, choice =>
            {
                try
                {
                    SuppressNextViewReset();
                    manager.LoadScreen(screen);
                }
                catch (Exception e)
                {
                    BetterModsModSystem.Logger?.Error("[BetterMods] Could not return to the mods screen.");
                    BetterModsModSystem.Logger?.Error(e);
                }

                bool opened = choice switch
                {
                    BetterModsConfirmChoice.Confirm => ModPageLinks.OpenOnce(url),
                    BetterModsConfirmChoice.Extra => ModPageLinks.TrustAndOpen(url, host),
                    _ => true
                };

                if (!opened) BetterModsModSystem.Logger?.Warning("[BetterMods] The mod page was not opened: " + url);
            }, manager, screen));
        }

        /// <summary>
        /// Adds the cards' hover tooltip and returns its bounds for the caller to re-parent, or null
        /// when the card is not drawn on this build (vanilla rows keep their own detail text). Never
        /// throws.
        /// </summary>
        private static ElementBounds AddCardTooltip(GuiScreenMods screen, GuiComposer composer, ElementBounds clippingBounds)
        {
            try
            {
                if (!ModCellLayout.CardLayoutAvailable) return null;

                ElementBounds bounds = ElementBounds.Fixed(0.0, 0.0, 1.0, 1.0);
                GuiElementModCardTooltip tooltip = new GuiElementModCardTooltip(
                    composer.Api, bounds, clippingBounds,
                    () => composer.GetCellList<ModCellEntry>(CellListKey),
                    () => !screen.IsOpened || PopupOpen(screen));

                composer.AddInteractiveElement(tooltip, GuiElementModCardTooltip.ElementKey);
                return bounds;
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not add the mod card tooltip: " + e.Message);
                return null;
            }
        }

        /// <summary>
        /// Starts installing, for the VIEWED profile: when each file lands, that profile - and no other
        /// - switches to it. Captured now, at the click, so switching tabs during a download cannot
        /// redirect the result to a profile the player was not looking at when they asked.
        /// </summary>
        private static void StartUpdates(GuiScreenMods screen, List<ModVersionGroup> groups)
        {
            if (ModUpdateService.StartInstall(groups, ViewedProfileId) > 0)
            {
                screen?.ElementComposer?.Api?.Gui.PlaySound("menubutton");
            }
        }

        private static void InstallUpdateListener()
        {
            if (updateListenerInstalled) return;
            updateListenerInstalled = true;
            ModUpdateService.Changed += OnUpdatesChanged;
        }

        /// <summary>
        /// The update state moved - a check answered, an install progressed or finished. The status and
        /// the badges are baked into the composer and the cell textures, so the screen is rebuilt,
        /// keeping tab and scroll position. Coalesced to one rebuild per frame, and held back while a
        /// popup is open: a rebuild would close the version picker, or throw away a half-typed profile
        /// name.
        /// </summary>
        private static void OnUpdatesChanged()
        {
            GuiScreenMods screen = currentScreen;
            if (screen == null || !screen.IsOpened || updateRefreshQueued) return;

            updateRefreshQueued = true;
            ScreenManager.EnqueueMainThreadTask(() => RefreshForUpdates(screen));
        }

        private static void RefreshForUpdates(GuiScreenMods screen)
        {
            try
            {
                updateRefreshQueued = false;
                if (screen == null || !screen.IsOpened) return;

                if (PopupOpen(screen))
                {
                    updateRefreshQueued = true;
                    ScreenManager.EnqueueCallBack(() => RefreshForUpdates(screen), 300, "bettermods-update-refresh");
                    return;
                }

                Rebuild(screen, reloadMods: false);
            }
            catch (Exception e)
            {
                updateRefreshQueued = false;
                BetterModsModSystem.Logger?.Error("[BetterMods] Refreshing the mods screen for mod updates failed.");
                BetterModsModSystem.Logger?.Error(e);
            }
        }

        private static bool PopupOpen(GuiScreenMods screen)
        {
            GuiComposer composer = screen?.ElementComposer;
            if (composer == null) return false;

            if (composer.GetElement(VersionMenuKey) is GuiElementVersionMenu menu && menu.IsOpened) return true;
            return composer.GetElement(RenamePanelKey) is GuiElementRenamePanel panel && panel.IsOpen;
        }

        // ---------------------------------------------------------------- handlers

        /// <summary>
        /// A profile tab was clicked. Every branch that rebuilds does so DEFERRED: this runs from
        /// inside <c>GuiComposer.OnMouseDown</c>, which keeps walking <c>interactiveElements</c>
        /// after a handler returns, so rebuilding inline would hand a mouse-down to every element
        /// after the tab strip - all of them already disposed. The in-game dialog's equivalent has
        /// always deferred; this one used not to.
        /// </summary>
        private static void OnTabAction(GuiScreenMods screen, int index, EnumMouseButton button)
        {
            try
            {
                List<ModProfile> profiles = ProfileStore.Profiles;

                if (index >= profiles.Count)
                {
                    // The trailing "+" tab: clone whatever is being viewed and switch to it.
                    if (button != EnumMouseButton.Left) return;

                    ModProfile created = ProfileStore.CreateProfile(null, ViewedProfileId);
                    if (created != null) SetViewedProfile(created.Id);
                    RebuildDeferred(screen);
                    return;
                }

                ModProfile profile = profiles[index];

                // Right-click opens rename/delete; middle and the rest are deliberately inert.
                if (button == EnumMouseButton.Right)
                {
                    OpenRenamePanel(screen, index, profile);
                    return;
                }

                if (button != EnumMouseButton.Left) return;

                SetViewedProfile(profile.Id);
                RebuildDeferred(screen);
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Error("[BetterMods] Profile tab click failed.");
                BetterModsModSystem.Logger?.Error(e);
            }
        }

        /// <summary>
        /// Drops the rename/delete panel out of the tab that was right-clicked. Nothing is rebuilt
        /// until the panel actually reports a change - opening it is not an edit.
        /// </summary>
        private static void OpenRenamePanel(GuiScreenMods screen, int index, ModProfile profile)
        {
            GuiComposer composer = screen?.ElementComposer;
            if (composer == null || profile == null) return;

            GuiElementProfileTabs tabs = composer.GetElement(TabsElementKey) as GuiElementProfileTabs;
            GuiElementRenamePanel panel = composer.GetElement(RenamePanelKey) as GuiElementRenamePanel;
            if (tabs == null || panel == null) return;

            // The tab strip is added before the version picker, so the picker never saw the click
            // that opened this panel and would otherwise stay hanging over the list.
            (composer.GetElement(VersionMenuKey) as GuiElementVersionMenu)?.CloseMenu();

            panel.OpenFor(tabs, index, profile.Id, profile.Name, () => RebuildDeferred(screen));
        }

        private static bool OnApplyProfile(GuiScreenMods screen)
        {
            try
            {
                ProfileStore.ApplyProfile(ViewedProfileId);

                // Deferred like every other rebuild, and for the same reason: this is a button
                // handler, so it runs from inside GuiComposer's element-table walk (vanilla buttons
                // fire from OnMouseUpOnElement, which iterates exactly as OnMouseDown does).
                // reloadMods:true is what the vanilla "Reload Mods" button does - the mod list has to
                // be re-verified against the ClientSettings that ApplyProfile just wrote.
                RebuildDeferred(screen, reloadMods: true);
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Error("[BetterMods] Applying profile '" + viewedProfileId + "' failed.");
                BetterModsModSystem.Logger?.Error(e);
            }

            return true;
        }

        /// <summary>
        /// Rebuilds the screen for the profile now being viewed. OnScreenLoaded re-runs InitGui and
        /// reloads the cells, which is exactly the refresh we need - it just must not be mistaken
        /// for a fresh open, or it would snap the view back to the active profile.
        /// <para>
        /// Private on purpose: every caller is a click handler, and a click handler must go through
        /// <see cref="RebuildDeferred"/>.
        /// </para>
        /// <para>
        /// <paramref name="reloadMods"/> adds the <c>ScreenManager.loadMods()</c> that vanilla's
        /// "Reload Mods" button does. Only Apply Profile needs it: it is the one rebuild that follows
        /// a <c>ClientSettings</c> write, so it is the one where the containers' status and error
        /// state are stale.
        /// </para>
        /// </summary>
        private static void Rebuild(GuiScreenMods screen, bool reloadMods)
        {
            SuppressNextViewReset();
            if (reloadMods) screen.ScreenManager.loadMods();
            screen.OnScreenLoaded();
        }

        /// <summary>
        /// Rebuilds on the next frame rather than inline. Every caller runs from inside the
        /// composer's element-table walk - the version picker reporting a selection, the rename panel
        /// reporting an edit, a tab click, the Apply Profile button - and that loop keeps iterating
        /// after a handler returns; disposing and rebuilding that composer underneath it would hand
        /// mouse events to disposed elements.
        /// </summary>
        private static void RebuildDeferred(GuiScreenMods screen, bool reloadMods = false)
        {
            ICoreClientAPI api = screen?.ElementComposer?.Api;
            if (api == null)
            {
                Rebuild(screen, reloadMods);
                return;
            }

            api.Event.EnqueueMainThreadTask(() =>
            {
                try
                {
                    // The player may have left the screen in the meantime.
                    if (!screen.IsOpened) return;
                    Rebuild(screen, reloadMods);
                }
                catch (Exception e)
                {
                    BetterModsModSystem.Logger?.Error("[BetterMods] Rebuilding the mods screen failed.");
                    BetterModsModSystem.Logger?.Error(e);
                }
            }, "bettermods-rebuild-modsscreen");
        }
    }
}
