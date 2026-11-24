using System;
using System.Globalization;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

namespace BetterMods.Gui
{
    /// <summary>
    /// The version picker that drops out of a mod row's version text.
    /// <para>
    /// A cell-list row cannot host a <c>GuiElementDropDown</c>: rows are baked to a Cairo texture by
    /// <c>GuiElementModCell</c>, they are not in the composer's element table, they only ever see
    /// forwarded mouse events, and they live inside a <c>BeginClip</c> scissor that would cut the
    /// popup off. So the popup is not IN the row - it is a composer element of its own, added AFTER
    /// the clip, which is repositioned over the row that was clicked.
    /// </para>
    /// <para>
    /// It derives from <see cref="GuiElementListMenu"/> for its plumbing - focus, the composer's
    /// element table, <c>Values</c>/<c>Names</c>/<c>SelectedIndex</c> - but does its own layout,
    /// drawing and hit testing. Vanilla's <c>ComposeDynamicElements</c> is never run (see
    /// <see cref="ComposeElements"/>), which is what suppresses its scrollbar: a normal version list
    /// shows every entry at once, so there is nothing to scroll, and the 10px gutter vanilla reserves
    /// for the scrollbar in both its width and its mouse-down handler is simply not part of this
    /// geometry. A list too long to fit on screen becomes a wheel- and keyboard-driven window over
    /// the entries instead - see <see cref="Layout"/> - so those versions stay reachable without
    /// bringing the scrollbar back.
    /// </para>
    /// <para>
    /// Both surfaces - the main-menu <c>GuiScreenMods</c> and the in-game <c>GuiDialogModProfiles</c>
    /// - route mouse, wheel and key events straight into their <c>GuiComposer</c>, so one element
    /// works on both with no per-surface plumbing.
    /// </para>
    /// </summary>
    public class GuiElementVersionMenu : GuiElementListMenu
    {
        /// <summary>Interior inset, unscaled: clears the 2px outline and the 2px emboss of the frame.</summary>
        private const double UnscaledFramePad = 5.0;

        /// <summary>Horizontal padding either side of an entry's content, unscaled.</summary>
        private const double UnscaledEntryPadX = 10.0;

        /// <summary>
        /// Padding above the first entry and below the last, unscaled, on top of
        /// <see cref="UnscaledFramePad"/>. Applied to both ends, so the run of entries is centred in
        /// the frame however many of them there are.
        /// </summary>
        private const double UnscaledEntryPadY = 3.0;

        /// <summary>Width of the column the "this is the current version" marker lives in, unscaled.</summary>
        private const double UnscaledMarkerWidth = 14.0;

        /// <summary>
        /// Clear space between the marker column and the entry text, unscaled. Without it the check
        /// sits hard against the version and the two read as one glyph run.
        /// </summary>
        private const double UnscaledMarkerGap = 7.0;

        /// <summary>Narrowest the popup may get, unscaled, so a "--" entry is not a stub.</summary>
        private const double UnscaledMinWidth = 100.0;

        /// <summary>Gap between the trigger box and the popup, unscaled.</summary>
        private const double UnscaledAnchorGap = 3.0;

        /// <summary>How close to the screen edge the popup may be pushed, unscaled.</summary>
        private const double UnscaledScreenMargin = 4.0;

        /// <summary>What this element is called in the log lines <see cref="PopupChrome"/> writes.</summary>
        private const string PopupName = "version picker";

        /// <summary>
        /// Leftmost screen x the popup may be placed at, read each time it opens; null means the
        /// screen margin. The main-menu host points it at <see cref="MenuLayout.ClearLeftPx"/> so the
        /// picker never slides under the sidebar. See <see cref="PopupChrome.Place"/>.
        /// </summary>
        public Func<double> LeftLimit { get; set; }

        private Action<int> onPicked;
        private ElementBounds anchorBounds;
        private double anchorAbsY;

        /// <summary>
        /// The frame drawn around the open popup. Vanilla's list menu paints its own background and
        /// a half-clipped 1px hairline, which over a mod row of almost the same colour reads as
        /// floating text; this is the border that makes it a panel. See <see cref="ComposeFrame"/>.
        /// </summary>
        private LoadedTexture frameTexture;

        // The drawn rectangle, in absolute screen pixels. Everything - the body texture, the hover
        // bar, the frame, the hit test - is expressed against these four numbers, so drawn and
        // clickable geometry cannot drift apart.
        private double popupX;
        private double popupY;
        private double popupWidth;
        private double popupHeight;

        /// <summary>Height of one entry, in scaled pixels.</summary>
        private double lineHeight;

        /// <summary>Offset of the first entry from the popup's top edge, in scaled pixels.</summary>
        private double contentTop;

        /// <summary>Index of the topmost entry drawn. Non-zero only when the list overflows.</summary>
        private int firstVisible;

        /// <summary>How many entries are drawn at once - every entry, unless the screen is too short.</summary>
        private int visibleCount;

        /// <summary>
        /// One baked hover bar per DRAWN row, composed the first time that row is hovered and kept
        /// until the window scrolls. Sweeping the list used to rasterise and upload a texture on every
        /// hover change, so a cursor moving back and forth over two entries re-baked forever; this
        /// bounds the whole popup to at most one bake per row that fits on screen.
        /// </summary>
        private LoadedTexture[] hoverBars = new LoadedTexture[0];

        /// <summary>Hoisted out of the per-hover bake; neither depends on which row is hovered.</summary>
        private double[] hoverBarColor;
        private CairoFont hoverEntryFont;

        public GuiElementVersionMenu(ICoreClientAPI capi, CairoFont font)
            // One blank placeholder entry, not none: the base constructor sizes its internal arrays
            // from these, and an empty list would leave it with nothing to index.
            : base(capi,
                   new[] { "0" },
                   new[] { " " },
                   0,
                   null,
                   // Placeholder only: Layout() overwrites all four numbers with the drawn rectangle
                   // the moment the popup is opened for a row.
                   ElementBounds.Fixed(0.0, 0.0, UnscaledMinWidth, 30.0).WithEmptyParent(),
                   font,
                   false)
        {
            // The base constructor takes the callback but keeps it in a protected field, and ours
            // has to change per row, so it is routed through one stable handler instead.
            onSelectionChanged = OnEntryPicked;

            frameTexture = new LoadedTexture(capi);

            // Deliberately NOT PopupClickGate.Release(). Picking an entry runs on mouse-down and
            // queues a rebuild that constructs this element again long before the matching mouse-up
            // arrives; releasing here would hand that mouse-up to the row now under the cursor. See
            // PopupClickGate.
        }

        /// <summary>
        /// Adds a closed picker to a composer and takes its bounds back out of the layout tree.
        /// <para>
        /// Must be called after <c>EndClip()</c> so the popup is not scissored by the mod list, and
        /// last, so that every other element gets first refusal on a mouse event - the composer
        /// dispatches input in insertion order, not draw order.
        /// </para>
        /// </summary>
        public static GuiElementVersionMenu AddTo(GuiComposer composer, string key)
        {
            GuiElementVersionMenu menu = new GuiElementVersionMenu(composer.Api, CairoFont.WhiteSmallText());
            composer.AddInteractiveElement(menu, key);
            menu.DetachBounds();
            return menu;
        }

        /// <summary>
        /// Cuts the popup's bounds out of the composer's layout tree - see
        /// <see cref="PopupChrome.Detach"/>. Public because <c>GuiDialogModProfiles</c> composes this
        /// element inline and has to detach it before <c>Compose()</c>.
        /// </summary>
        public void DetachBounds()
        {
            PopupChrome.Detach(Bounds, PopupName);
        }

        /// <summary>
        /// Deliberately empty.
        /// <para>
        /// <c>GuiElementListMenu.ComposeElements</c> runs <c>ComposeDynamicElements</c>, which bakes
        /// vanilla's body, its hover bar AND its scrollbar, and pads the box out by a 10px scrollbar
        /// gutter. This element composes itself in <see cref="OpenFor"/> instead, so none of that
        /// ever exists: the inherited scrollbar is never composed, never sized, never drawn and
        /// never hit-tested, and no width is reserved for it.
        /// </para>
        /// </summary>
        public override void ComposeElements(Context ctx, ImageSurface surface)
        {
        }

        /// <summary>
        /// Opens the picker under <paramref name="cell"/>'s version trigger. Returns false - and
        /// stays closed - if the row has no trigger or the list is empty.
        /// </summary>
        public bool OpenFor(GuiElementModCell cell, string[] names, int selectedIndex, Action<int> picked)
        {
            try
            {
                if (names == null || names.Length == 0) return false;

                // The very rectangle the trigger was drawn as, so the popup hangs off the control
                // rather than off the glyphs inside it.
                if (!ModCellLayout.TryGetVersionZone(cell, out double triggerX, out double triggerY, out double triggerWidth, out double triggerHeight)) return false;

                // The list menu is a string list; the index is all we need back, so it is the value.
                string[] values = new string[names.Length];
                for (int i = 0; i < names.Length; i++) values[i] = i.ToString(CultureInfo.InvariantCulture);

                Values = values;
                Names = names;
                SetSelectedIndex(GameMath.Clamp(selectedIndex, 0, names.Length - 1));

                // No hover until the mouse or a key actually points at something. Starting it on the
                // selected entry is what made "current" and "pointed at" look like the same state.
                HoveredIndex = -1;

                onPicked = picked;
                anchorBounds = cell.Bounds;
                anchorAbsY = cell.Bounds.absY;

                firstVisible = 0;

                // Cloned AND re-alphaed, both deliberately: GuiElementListMenu.ComposeDynamicElements
                // takes a REFERENCE to GuiStyle.DialogHighlightColor and writes 0.5 into its alpha, so
                // the game's own value is 0.9 or 0.5 depending on whether a vanilla dropdown has been
                // opened this session. The bar has to be opaque either way - the entry's text is
                // redrawn on top of it.
                hoverBarColor = (double[])GuiStyle.DialogHighlightColor.Clone();
                hoverBarColor[3] = 1.0;
                hoverEntryFont = Font.Clone().WithColor(GuiStyle.DarkBrownColor);

                Layout(triggerX + triggerWidth, triggerY, triggerY + triggerHeight);
                ResetHoverBars();

                // A selection below the fold would otherwise open with no visible marker at all.
                EnsureVisible(SelectedIndex);

                ComposeBody();
                ComposeFrame();

                expanded = true;
                return true;
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Error("[BetterMods] Could not open the version picker.");
                BetterModsModSystem.Logger?.Error(e);
                CloseMenu();
                return false;
            }
        }

        /// <summary>Closes the picker and forgets whatever it was going to report.</summary>
        public void CloseMenu()
        {
            onPicked = null;
            expanded = false;
        }

        private void OnEntryPicked(string value, bool selected)
        {
            Action<int> handler = onPicked;
            onPicked = null;

            if (handler == null || !selected) return;
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index)) return;

            handler(index);
        }

        // ---------------------------------------------------------------- layout

        /// <summary>
        /// Sizes the popup to its contents and hangs it off the right edge of the version text.
        /// <para>
        /// Width is the widest measured entry plus the marker column, its gap, and symmetric padding,
        /// floored at <see cref="UnscaledMinWidth"/> - nothing is inherited from the anchor box and
        /// nothing is reserved for a scrollbar. Height is <c>entries * lineHeight</c> plus the same
        /// padding top and bottom, so a normal list shows every entry at once and there is nothing to
        /// scroll.
        /// </para>
        /// <para>
        /// Past roughly 35 entries at scale 1 that no longer fits on screen. The height is then capped
        /// to whichever side of the anchor has more room and the list becomes a WINDOW over the
        /// entries, driven by the wheel and by the arrow keys - not clamped to the screen edge with
        /// the tail drawn past it, which left those versions permanently unclickable. There is still
        /// no scrollbar; the overflow arrows drawn by <see cref="DrawOverflowCue"/> are the cue.
        /// </para>
        /// <para>
        /// Anchoring: the popup's RIGHT edge meets the trigger box's right edge, so it hangs off one
        /// edge like a real dropdown and stays clear of the on/off column (the trigger already ends
        /// 21 unscaled px short of it). Vertically it prefers to sit
        /// <see cref="UnscaledAnchorGap"/> below the text; if that would run it off the bottom of
        /// the screen and there IS room above, it opens upward instead.
        /// </para>
        /// </summary>
        private void Layout(double anchorRight, double anchorTop, double anchorBottom)
        {
            // The same scale factor the base class derives its line height from.
            double lineScale = Scale * RuntimeEnv.GUIScale;
            lineHeight = unscaledLineHeight * lineScale;

            double framePad = GuiElement.scaled(UnscaledFramePad);
            double padX = GuiElement.scaled(UnscaledEntryPadX);
            double padY = GuiElement.scaled(UnscaledEntryPadY);
            double markerColumn = GuiElement.scaled(UnscaledMarkerWidth + UnscaledMarkerGap);

            double widestText = 0.0;
            for (int i = 0; i < Names.Length; i++)
            {
                widestText = Math.Max(widestText, Font.GetTextExtents(Names[i] ?? "").Width);
            }

            popupWidth = Math.Ceiling(Math.Max(
                GuiElement.scaled(UnscaledMinWidth),
                2.0 * framePad + 2.0 * padX + markerColumn + widestText));
            contentTop = framePad + padY;

            double gap = GuiElement.scaled(UnscaledAnchorGap);
            double margin = GuiElement.scaled(UnscaledScreenMargin);
            double screenWidth = api.Render.FrameWidth;
            double screenHeight = api.Render.FrameHeight;

            double chrome = 2.0 * (framePad + padY);
            double roomBelow = screenHeight - margin - (anchorBottom + gap);
            double roomAbove = anchorTop - gap - margin;

            // At least one entry even on a screen that has room for none, so the popup is never a
            // bare frame; the final clamp is what keeps that one entry on screen.
            int fits = lineHeight > 0.5 ? (int)Math.Floor((Math.Max(roomBelow, roomAbove) - chrome) / lineHeight) : Names.Length;
            visibleCount = GameMath.Clamp(fits, 1, Names.Length);

            popupHeight = Math.Ceiling(chrome + visibleCount * lineHeight);
            firstVisible = GameMath.Clamp(firstVisible, 0, Names.Length - visibleCount);

            // Right edge on the trigger's right edge, so it hangs off one edge like a real dropdown.
            PopupChrome.Place(
                anchorRight - popupWidth, anchorTop, anchorBottom,
                popupWidth, popupHeight,
                gap, margin, screenWidth, screenHeight,
                out popupX, out popupY,
                PopupChrome.ReadLimit(LeftLimit, PopupName));

            // Bounds IS the drawn rectangle now - the base class treats it as the anchor box and
            // draws below it, but nothing of the base class's drawing survives here. Keeping the two
            // identical means the inherited geometry can never disagree with ours.
            double scale = RuntimeEnv.GUIScale;
            if (!(scale > 0.01)) scale = 1.0;

            Bounds.fixedX = popupX / scale;
            Bounds.fixedY = popupY / scale;
            Bounds.fixedWidth = popupWidth / scale;
            Bounds.fixedHeight = popupHeight / scale;
            Bounds.CalcWorldBounds();

            expandedBoxWidth = popupWidth;
            expandedBoxHeight = popupHeight;
            visibleBounds = Bounds;
            scrollOffY = 0.0;
        }

        // ---------------------------------------------------------------- drawing

        /// <summary>
        /// Bakes the whole popup: panel, the current entry's well and marker, and every entry's
        /// text.
        /// <para>
        /// Every entry is drawn in the SAME colour - the font's own - so "current" reads as a state
        /// rather than as a different kind of item. The current entry gets a recessed well
        /// (<c>rgba(0,0,0,0.25)</c>, the cue vanilla uses for the mod row's own switch box) plus a
        /// check in <c>GuiStyle.ActiveButtonTextColor</c>, the game's "this one is active" gold.
        /// </para>
        /// </summary>
        private void ComposeBody()
        {
            try
            {
                int width = (int)popupWidth;
                int height = (int)popupHeight;
                if (width < 4 || height < 4) return;

                ImageSurface surface = new ImageSurface(Format.Argb32, width, height);
                Context ctx = genContext(surface);

                try
                {
                    ctx.SetSourceRGBA(GuiStyle.DialogStrongBgColor);
                    GuiElement.RoundRectangle(ctx, 0.0, 0.0, width, height, GuiStyle.ElementBGRadius);
                    ctx.Fill();

                    double framePad = GuiElement.scaled(UnscaledFramePad);
                    double padX = GuiElement.scaled(UnscaledEntryPadX);
                    double markerWidth = GuiElement.scaled(UnscaledMarkerWidth);
                    double textX = framePad + padX + markerWidth + GuiElement.scaled(UnscaledMarkerGap);

                    int selected = SelectedIndex;
                    bool hasSelection = selected >= firstVisible && selected < firstVisible + visibleCount;
                    double selectedTop = contentTop + (selected - firstVisible) * lineHeight;

                    if (hasSelection)
                    {
                        GuiElement.RoundRectangle(ctx, framePad, selectedTop, width - 2.0 * framePad, lineHeight, GuiStyle.ElementBGRadius);
                        ctx.SetSourceRGBA(0.0, 0.0, 0.0, 0.25);
                        ctx.Fill();
                    }

                    DrawEntryText(ctx, Font, textX, contentTop, firstVisible, firstVisible + visibleCount);

                    // Last, so the marker's colour cannot leak into a text fill.
                    if (hasSelection)
                    {
                        DrawMarker(ctx, GuiStyle.ActiveButtonTextColor, framePad + padX, selectedTop, markerWidth, lineHeight);
                    }

                    if (firstVisible > 0) DrawOverflowCue(ctx, width, contentTop / 2.0, up: true);
                    if (firstVisible + visibleCount < Names.Length) DrawOverflowCue(ctx, width, height - contentTop / 2.0, up: false);

                    generateTexture(surface, ref dropDownTexture);
                }
                finally
                {
                    ctx.Dispose();
                    surface.Dispose();
                }
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not compose the version picker: " + e.Message);
            }
        }

        /// <summary>
        /// Bakes the bar for one hovered entry - highlight plus that entry's text redrawn on top of
        /// it - one line high, to be stamped over the body at the hovered row.
        /// <para>
        /// The bar is <c>GuiStyle.DialogHighlightColor</c> at its own full alpha, not the half-alpha
        /// wash vanilla lays over its text: at 50% over <c>DialogStrongBgColor</c> that lands in the
        /// same muddy brown as the mod row behind the popup, which is exactly what made "hovered"
        /// and "current" indistinguishable. Opaque means the text has to be redrawn dark, hence
        /// <c>GuiStyle.DarkBrownColor</c> - the game's own text colour for light-on-dark surfaces.
        /// The two states are then unmistakable: hover is a bright bar, current is a dark well with
        /// a gold check.
        /// </para>
        /// </summary>
        private void ComposeHover(int index, int row)
        {
            try
            {
                int width = (int)popupWidth;
                int height = (int)Math.Ceiling(lineHeight);
                if (width < 4 || height < 2 || index < 0 || index >= Names.Length) return;
                if (row < 0 || row >= hoverBars.Length) return;
                if (hoverBarColor == null || hoverEntryFont == null) return;

                ImageSurface surface = new ImageSurface(Format.Argb32, width, height);
                Context ctx = genContext(surface);

                try
                {
                    double framePad = GuiElement.scaled(UnscaledFramePad);
                    double padX = GuiElement.scaled(UnscaledEntryPadX);
                    double markerWidth = GuiElement.scaled(UnscaledMarkerWidth);

                    ctx.SetSourceRGBA(hoverBarColor);
                    GuiElement.RoundRectangle(ctx, framePad, 1.0, width - 2.0 * framePad, lineHeight - 2.0, GuiStyle.ElementBGRadius);
                    ctx.Fill();

                    // The bar is one line tall and composed at its own origin; the whole texture is
                    // offset onto the right row at render time.
                    DrawEntryText(ctx, hoverEntryFont, framePad + padX + markerWidth + GuiElement.scaled(UnscaledMarkerGap), 0.0, index, index + 1);
                    if (index == SelectedIndex) DrawMarker(ctx, GuiStyle.DarkBrownColor, framePad + padX, 0.0, markerWidth, lineHeight);

                    LoadedTexture bar = hoverBars[row];
                    generateTexture(surface, ref bar);
                    hoverBars[row] = bar;
                }
                finally
                {
                    ctx.Dispose();
                    surface.Dispose();
                }
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not compose the version picker hover bar: " + e.Message);
            }
        }

        /// <summary>Throws away every baked hover bar, for a window that no longer shows those rows.</summary>
        private void ResetHoverBars()
        {
            for (int i = 0; i < hoverBars.Length; i++) hoverBars[i]?.Dispose();

            hoverBars = new LoadedTexture[Math.Max(0, visibleCount)];
        }

        /// <summary>The hover bar for an entry, baked on first use. Null when it is not in the window.</summary>
        private LoadedTexture HoverBar(int index)
        {
            int row = index - firstVisible;
            if (row < 0 || row >= hoverBars.Length) return null;

            if (hoverBars[row] == null)
            {
                // Allocated BEFORE the bake, so a bake that failed is not retried - and re-logged -
                // on every frame the row stays hovered. An empty texture simply draws nothing.
                hoverBars[row] = new LoadedTexture(api);
                ComposeHover(index, row);
            }

            return hoverBars[row];
        }

        /// <summary>
        /// The "there is more this way" arrow, centred in the popup's own padding band. Drawn rather
        /// than a glyph, for the reason <see cref="DrawMarker"/> gives.
        /// </summary>
        private static void DrawOverflowCue(Context ctx, double width, double centreY, bool up)
        {
            double half = GuiElement.scaled(4.0);
            double height = GuiElement.scaled(3.0);
            double cx = width / 2.0;
            double top = centreY - height / 2.0;

            ctx.NewPath();
            if (up)
            {
                ctx.MoveTo(cx - half, top + height);
                ctx.LineTo(cx + half, top + height);
                ctx.LineTo(cx, top);
            }
            else
            {
                ctx.MoveTo(cx - half, top);
                ctx.LineTo(cx + half, top);
                ctx.LineTo(cx, top + height);
            }
            ctx.ClosePath();

            ctx.SetSourceRGBA(1.0, 1.0, 1.0, 0.5);
            ctx.Fill();
        }

        /// <summary>
        /// Draws entries <paramref name="from"/> (inclusive) to <paramref name="to"/> (exclusive),
        /// each vertically centred in its line, with entry <paramref name="from"/>'s line starting
        /// at <paramref name="firstRowTop"/> - so a one-entry surface can be composed at its own
        /// origin and the whole popup at <see cref="contentTop"/>. Vertical centring is
        /// <see cref="PopupChrome.CentringSlack"/>'s.
        /// </summary>
        private void DrawEntryText(Context ctx, CairoFont font, double textX, double firstRowTop, int from, int to)
        {
            font.SetupContext(ctx);

            double slack = PopupChrome.CentringSlack(font, lineHeight);

            for (int i = from; i < to; i++)
            {
                double top = firstRowTop + (i - from) * lineHeight;
                textUtil.DrawTextLine(ctx, font, Names[i] ?? "", textX, top + slack);
            }
        }

        /// <summary>
        /// Draws the "this is the current version" check, centred in the marker column.
        /// <para>
        /// A stroked path rather than U+2713: <c>CairoFont.SetupContext</c> uses Cairo's toy font
        /// API, which does no font fallback, so a glyph the system sans-serif happens not to ship
        /// would render as a box.
        /// </para>
        /// </summary>
        private static void DrawMarker(Context ctx, double[] color, double x, double y, double width, double height)
        {
            double size = Math.Min(width, height) * 0.62;
            double cx = x + width / 2.0;
            double cy = y + height / 2.0;

            ctx.NewPath();
            ctx.MoveTo(cx - 0.50 * size, cy + 0.05 * size);
            ctx.LineTo(cx - 0.15 * size, cy + 0.38 * size);
            ctx.LineTo(cx + 0.50 * size, cy - 0.38 * size);

            ctx.LineCap = LineCap.Round;
            ctx.LineJoin = LineJoin.Round;
            ctx.LineWidth = Math.Max(1.5, GuiElement.scaled(2.0));
            ctx.SetSourceRGBA(color);
            ctx.Stroke();
        }

        /// <summary>
        /// Bakes the popup's frame - <see cref="PopupChrome.ComposeFrame"/>, which is the same frame
        /// <see cref="GuiElementRenamePanel"/> draws. The interior is left transparent, so the body
        /// composed by <see cref="ComposeBody"/> is still what separates the popup from the rows.
        /// </summary>
        private void ComposeFrame()
        {
            PopupChrome.ComposeFrame(this, api, popupWidth, popupHeight, ref frameTexture, PopupName);
        }

        /// <summary>
        /// The popup is anchored to a row inside a scrolling list, so it closes the moment that row
        /// moves. Checking at draw time catches every way the list can scroll - wheel, scrollbar
        /// drag, a rebuild - without any of them having to know the picker exists.
        /// <para>
        /// Nothing of the base class's rendering is reused: it would draw its own body under our
        /// frame and its scrollbar over it.
        /// </para>
        /// </summary>
        public override void RenderInteractiveElements(float deltaTime)
        {
            if (!IsOpened) return;

            if (AnchorMoved())
            {
                CloseMenu();
                return;
            }

            try
            {
                // The popup is added after EndClip but the mod list's scissor may still be live at
                // this point in the draw order, and the popup deliberately overhangs it.
                bool scissored = api.Render.ScissorStack.Count > 0;
                if (scissored) api.Render.GlScissorFlag(enable: false);

                try
                {
                    int x = (int)popupX;
                    int y = (int)popupY;

                    if (dropDownTexture != null && dropDownTexture.TextureId != 0)
                    {
                        api.Render.Render2DTexturePremultipliedAlpha(dropDownTexture.TextureId, x, y, (int)popupWidth, (int)popupHeight, 310f);
                    }

                    LoadedTexture bar = HoveredIndex >= 0 && HoveredIndex < Names.Length ? HoverBar(HoveredIndex) : null;
                    if (bar != null && bar.TextureId != 0)
                    {
                        api.Render.Render2DTexturePremultipliedAlpha(
                            bar.TextureId,
                            x,
                            (int)(popupY + contentTop + (HoveredIndex - firstVisible) * lineHeight),
                            bar.Width,
                            bar.Height,
                            311f);
                    }

                    // Over both, so the bevel is never half-covered by a hover bar at the top or
                    // bottom entry.
                    if (frameTexture != null && frameTexture.TextureId != 0)
                    {
                        api.Render.Render2DTexturePremultipliedAlpha(frameTexture.TextureId, x, y, frameTexture.Width, frameTexture.Height, 315f);
                    }
                }
                finally
                {
                    if (scissored) api.Render.GlScissorFlag(enable: true);
                }
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not draw the version picker: " + e.Message);
            }
        }

        // ---------------------------------------------------------------- hit testing

        /// <summary>
        /// The drawn rectangle, exactly. Vanilla's version spans <c>absY .. absY + fullContentHeight</c>
        /// measured from the top of the ANCHOR box, which is neither where this popup starts nor how
        /// tall it is - and the composer uses this to decide who owns the cursor and the wheel.
        /// </summary>
        public override bool IsPositionInside(int posX, int posY)
        {
            if (!IsOpened) return false;

            return posX >= popupX && posX <= popupX + popupWidth
                && posY >= popupY && posY <= popupY + popupHeight;
        }

        /// <summary>Index of the entry under a screen position, or -1. Same rectangle as the draw.</summary>
        private int EntryAt(int posX, int posY)
        {
            if (!IsOpened || lineHeight < 1.0) return -1;
            if (posX < popupX || posX > popupX + popupWidth) return -1;

            double local = posY - popupY - contentTop;
            if (local < 0.0) return -1;

            int row = (int)(local / lineHeight);
            if (row < 0 || row >= visibleCount) return -1;

            int index = firstVisible + row;
            return index < Names.Length ? index : -1;
        }

        /// <summary>True when the entries do not all fit and the popup is a window over them.</summary>
        private bool CanScroll => visibleCount > 0 && visibleCount < Names.Length;

        /// <summary>
        /// Moves the window. Everything drawn depends on it, so the body is re-baked and the hover
        /// bars - which are per drawn ROW, not per entry - are thrown away.
        /// </summary>
        private void SetFirstVisible(int value)
        {
            int max = Math.Max(0, Names.Length - visibleCount);
            value = GameMath.Clamp(value, 0, max);
            if (value == firstVisible) return;

            firstVisible = value;

            // Not while opening: OpenFor bakes both itself, once, after this has settled.
            if (!expanded) return;

            ResetHoverBars();
            ComposeBody();
        }

        /// <summary>Scrolls the window by the smallest amount that brings an entry into it.</summary>
        private void EnsureVisible(int index)
        {
            if (index < 0 || index >= Names.Length) return;

            if (index < firstVisible) SetFirstVisible(index);
            else if (index >= firstVisible + visibleCount) SetFirstVisible(index - visibleCount + 1);
        }

        private bool AnchorMoved()
        {
            try
            {
                if (anchorBounds?.ParentBounds == null) return true;
                return Math.Abs(anchorBounds.absY - anchorAbsY) > 0.5;
            }
            catch (Exception)
            {
                return true;
            }
        }

        private void SetHoveredIndex(int index)
        {
            HoveredIndex = index;
            if (index < 0) return;

            // Keyboard navigation can point past the window; the mouse never can.
            EnsureVisible(index);

            // Bakes the bar the first time this row is hovered and never again while the window
            // holds still, so a cursor sweeping the list costs at most one bake per drawn row.
            HoverBar(index);
        }

        // ---------------------------------------------------------------- input

        public override void OnMouseDown(ICoreClientAPI api, MouseEvent args)
        {
            // Nothing of the base class's handler applies: it measures from the anchor box and
            // hands the leftmost 10px of the popup to a scrollbar that does not exist here.
            if (!IsOpened) return;

            // Whether the click picked an entry or just dismissed the popup, it was meant for the
            // popup and must not reach the row underneath it.
            PopupClickGate.Consume();

            if (!IsPositionInside(args.X, args.Y))
            {
                CloseMenu();
                api.Gui.PlaySound("menubutton");
                return;
            }

            args.Handled = true;

            int index = EntryAt(args.X, args.Y);
            if (index < 0) return;

            Pick(api, index);
        }

        public override void OnMouseUp(ICoreClientAPI api, MouseEvent args)
        {
            // Runs after every row handler in this composer - see PopupClickGate. The base handler is
            // skipped on purpose: it only forwards to the suppressed scrollbar.
            PopupClickGate.Release();
        }

        public override void OnMouseMove(ICoreClientAPI api, MouseEvent args)
        {
            if (!IsOpened) return;

            int index = EntryAt(api.Input.MouseX, api.Input.MouseY);
            SetHoveredIndex(index);

            if (index >= 0) args.Handled = true;
        }

        public override void OnMouseWheel(ICoreClientAPI api, MouseWheelEventArgs args)
        {
            // Swallowed either way while the cursor is over the popup: letting the wheel through
            // would scroll the mod list underneath and take the anchor row away, which closes the
            // popup mid-gesture. When the list does not all fit, the wheel moves the window instead -
            // that is the only way to reach the entries past the fold.
            if (!IsOpened || !IsPositionInside(api.Input.MouseX, api.Input.MouseY)) return;

            args.SetHandled();

            // One row per notch. On 1.22 deltaPrecise is the (int)-truncated wheel delta (1.22 has
            // no DeltaYInt), so a zero delta - a sub-notch trackpad event - steps nothing.
            if (CanScroll && args.deltaPrecise != 0f) SetFirstVisible(firstVisible - Math.Sign(args.deltaPrecise));
        }

        /// <summary>
        /// Vanilla's list menu treats Enter as "confirm" and ignores Escape, which would then bubble
        /// up and close the whole dialog; its arrow keys drive the scrollbar this element does not
        /// have. All of it is reimplemented here against the popup's own geometry.
        /// </summary>
        public override void OnKeyDown(ICoreClientAPI api, KeyEvent args)
        {
            if (!IsOpened) return;

            switch (args.KeyCode)
            {
                case (int)GlKeys.Escape:
                    CloseMenu();
                    args.Handled = true;
                    return;

                case (int)GlKeys.Up:
                    SetHoveredIndex(Step(-1));
                    args.Handled = true;
                    return;

                case (int)GlKeys.Down:
                    SetHoveredIndex(Step(1));
                    args.Handled = true;
                    return;

                case (int)GlKeys.Home:
                    SetHoveredIndex(0);
                    args.Handled = true;
                    return;

                case (int)GlKeys.End:
                    SetHoveredIndex(Names.Length - 1);
                    args.Handled = true;
                    return;

                case (int)GlKeys.Enter:
                case (int)GlKeys.KeypadEnter:
                    Pick(api, HoveredIndex >= 0 ? HoveredIndex : SelectedIndex);
                    args.Handled = true;
                    return;
            }
        }

        /// <summary>
        /// First-letter jump, like vanilla's - minus its <c>EnsureItemVisible</c> call, which drives
        /// the suppressed scrollbar.
        /// </summary>
        public override void OnKeyPress(ICoreClientAPI api, KeyEvent args)
        {
            if (!IsOpened || Names.Length == 0) return;

            char typed = char.ToLowerInvariant((char)args.KeyCode);
            int start = HoveredIndex >= 0 ? HoveredIndex : SelectedIndex;

            for (int i = 0; i < Names.Length; i++)
            {
                int index = GameMath.Mod(start + 1 + i, Names.Length);
                string name = Names[index];
                if (string.IsNullOrEmpty(name)) continue;

                if (char.ToLowerInvariant(name[0]) == typed)
                {
                    SetHoveredIndex(index);
                    args.Handled = true;
                    return;
                }
            }
        }

        private int Step(int delta)
        {
            int from = HoveredIndex >= 0 ? HoveredIndex : SelectedIndex;
            return GameMath.Mod(from + delta, Names.Length);
        }

        /// <summary>Commits <paramref name="index"/> and closes. Never re-enters the callback.</summary>
        private void Pick(ICoreClientAPI api, int index)
        {
            if (Values == null || index < 0 || index >= Values.Length)
            {
                CloseMenu();
                return;
            }

            SetSelectedIndex(index);

            // Not CloseMenu(): that would drop the callback before it has been invoked.
            expanded = false;

            api.Gui.PlaySound("toggleswitch");
            OnEntryPicked(Values[index], selected: true);
        }

        public override void OnFocusLost()
        {
            base.OnFocusLost();
            onPicked = null;
        }

        public override void Dispose()
        {
            base.Dispose();

            frameTexture?.Dispose();
            for (int i = 0; i < hoverBars.Length; i++) hoverBars[i]?.Dispose();
        }
    }
}
