using System;
using System.Collections.Generic;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace BetterMods.Gui
{
    /// <summary>
    /// A horizontal strip of profile tabs, plus a trailing "new profile" tab.
    /// <para>
    /// This exists instead of the vanilla <c>GuiElementHorizontalTabs</c> because that element
    /// ignores <c>MouseEvent.Button</c> in <c>OnMouseDownOnElement</c> and keeps its hit-test state
    /// (<c>tabWidths</c>/<c>tabOffsets</c>/<c>currentScrollOffset</c>) private, so a subclass cannot
    /// tell a left click from a right click on a specific tab. We need that distinction for
    /// right-click-to-rename, so the strip is rendered and hit-tested here.
    /// </para>
    /// <para>
    /// Only the VISIBLE window of the strip is ever rasterised, and only two highlight textures exist
    /// at a time - the active tab's and the hovered one's. An earlier version baked one texture as
    /// wide as every tab put together plus one texture per tab, which at a few dozen profiles ran
    /// past <c>GL_MAX_TEXTURE_SIZE</c> (the upload then fails silently and the strip is blank) and
    /// held one live GL texture per profile. Everything here is therefore bounded by the width of the
    /// element and by how many tabs fit in it, not by how many profiles exist.
    /// </para>
    /// </summary>
    public class GuiElementProfileTabs : GuiElementTextBase
    {
        /// <summary>Gap between two tabs, unscaled.</summary>
        public const double UnscaledTabSpacing = 4.0;

        /// <summary>Horizontal padding inside a tab, unscaled.</summary>
        public const double UnscaledTabPadding = 9.0;

        /// <summary>
        /// Where each surface's strip was last scrolled to, keyed by that surface.
        /// <para>
        /// The element is thrown away and rebuilt on every tab click, mod toggle and version pick, so
        /// instance state would put the strip back at offset 0 each time - and since the "+" tab is
        /// always last, creating a profile would mean scrolling to the far right again every time.
        /// A static store survives the rebuild; <see cref="EnsureActiveVisible"/> then corrects it
        /// when the tab that matters has moved out of view.
        /// </para>
        /// </summary>
        private static readonly Dictionary<string, double> ScrollOffsets =
            new Dictionary<string, double>(StringComparer.Ordinal);

        private readonly Action<int, EnumMouseButton> onTabAction;
        private readonly string[] tabNames;
        private readonly int[] tabWidths;

        /// <summary>Left edge of each tab, in scaled pixels from the start of the strip.</summary>
        private readonly double[] tabOffsets;

        private readonly string scrollKey;
        private readonly CairoFont selectedFont;

        /// <summary>The visible window of the unselected strip, <c>Bounds.InnerWidth</c> wide.</summary>
        private LoadedTexture stripTexture;

        /// <summary>Highlight for the active tab and for the hovered one; nothing else is ever baked.</summary>
        private LoadedTexture activeTexture;
        private LoadedTexture hoverTexture;

        private int composedActiveIndex = -1;
        private int composedHoverIndex = -1;
        private double composedScrollOffset = double.NaN;

        private double totalWidth;
        private double scrollOffset;
        private float fontHeight;

        /// <summary>Index of the tab drawn as selected. -1 for none.</summary>
        public int ActiveIndex;

        // Tabs are mouse-driven only; letting them join the tab-key focus cycle would steal focus
        // from the mod list without giving the player anything useful to do with it.
        public override bool Focusable => false;

        public GuiElementProfileTabs(ICoreClientAPI capi, ElementBounds bounds, string[] names, int activeIndex, Action<int, EnumMouseButton> onTabAction, string scrollKey = "default")
            : base(capi, "", CairoFont.WhiteSmallText().Clone(), bounds)
        {
            tabNames = names ?? new string[0];
            ActiveIndex = activeIndex;
            this.onTabAction = onTabAction;
            this.scrollKey = scrollKey ?? "default";

            selectedFont = CairoFont.WhiteSmallText().Clone();

            tabWidths = new int[tabNames.Length];
            tabOffsets = new double[tabNames.Length];

            stripTexture = new LoadedTexture(capi);
            activeTexture = new LoadedTexture(capi);
            hoverTexture = new LoadedTexture(capi);

            ScrollOffsets.TryGetValue(this.scrollKey, out scrollOffset);
        }

        private double MaxScrollOffset => Math.Max(0.0, totalWidth - Bounds.InnerWidth);

        public override void ComposeTextElements(Context ctxStatic, ImageSurface surfaceStatic)
        {
            MeasureTabs();
            EnsureActiveVisible();
            ComposeStrip();
        }

        private void MeasureTabs()
        {
            ImageSurface surface = new ImageSurface(Format.Argb32, 1, 1);
            Context ctx = new Context(surface);
            Font.SetupContext(ctx);

            fontHeight = (float)Font.GetFontExtents().Height;
            double spacing = scaled(UnscaledTabSpacing);
            double padding = scaled(UnscaledTabPadding);

            totalWidth = 0.0;
            for (int i = 0; i < tabNames.Length; i++)
            {
                tabOffsets[i] = totalWidth;
                tabWidths[i] = (int)(ctx.TextExtents(tabNames[i]).Width + 2.0 * padding + 1.0);
                totalWidth += (double)tabWidths[i] + spacing;
            }

            ctx.Dispose();
            surface.Dispose();

            SetScrollOffset(scrollOffset);
        }

        /// <summary>
        /// Scrolls the active tab into view, by the smallest amount that does it. Nothing moves when
        /// it is already visible, so the offset carried over from before a rebuild is respected.
        /// </summary>
        private void EnsureActiveVisible()
        {
            if (ActiveIndex < 0 || ActiveIndex >= tabWidths.Length) return;

            double width = Bounds.InnerWidth;
            if (width <= 1.0) return;

            double left = tabOffsets[ActiveIndex];
            double right = left + tabWidths[ActiveIndex];

            if (left < scrollOffset) SetScrollOffset(left);
            else if (right > scrollOffset + width) SetScrollOffset(right - width);
        }

        private void SetScrollOffset(double value)
        {
            scrollOffset = Math.Clamp(value, 0.0, MaxScrollOffset);
            ScrollOffsets[scrollKey] = scrollOffset;
        }

        /// <summary>
        /// Bakes the tabs that are currently in view into one texture exactly as wide as the element.
        /// Re-run whenever the strip is scrolled, which is the only thing that changes what is in it.
        /// </summary>
        private void ComposeStrip()
        {
            // Recorded up front, not on success: RenderInteractiveElements re-runs this whenever the
            // two disagree, so a failure that left it stale would re-bake and re-log every frame.
            composedScrollOffset = scrollOffset;

            try
            {
                int width = Math.Max(1, (int)Bounds.InnerWidth + 1);
                int height = Math.Max(1, (int)Bounds.InnerHeight + 1);

                ImageSurface surface = new ImageSurface(Format.Argb32, width, height);
                Context ctx = genContext(surface);

                try
                {
                    double padding = scaled(UnscaledTabPadding);

                    // Unselected tabs are drawn dimmed; the active and hovered ones are overlaid at
                    // render time.
                    Font.Color[3] = 0.5;
                    for (int i = FirstVisibleTab(); i < tabNames.Length; i++)
                    {
                        double x = tabOffsets[i] - scrollOffset;
                        if (x > width) break;

                        TabPath(ctx, x, tabWidths[i], height);
                        double[] bg = GuiStyle.DialogDefaultBgColor;
                        ctx.SetSourceRGBA(bg[0], bg[1], bg[2], bg[3] * 0.75);
                        ctx.FillPreserve();
                        ShadePath(ctx);

                        Font.SetupContext(ctx);
                        DrawTextLineAt(ctx, tabNames[i], x + padding, ((float)height - fontHeight) / 2f);
                    }

                    generateTexture(surface, ref stripTexture);
                }
                finally
                {
                    // In the finally so a Cairo failure mid-strip cannot leave the font permanently
                    // dimmed for the highlight passes.
                    Font.Color[3] = 1.0;

                    ctx.Dispose();
                    surface.Dispose();
                }
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not compose the profile tab strip: " + e.Message);
            }
        }

        /// <summary>Index of the first tab whose right edge is at or past the left of the window.</summary>
        private int FirstVisibleTab()
        {
            int index = TabIndexBefore(scrollOffset);
            return index < 0 ? 0 : index;
        }

        /// <summary>
        /// Bakes one tab's selected look into <paramref name="texture"/>. Only ever called for the
        /// active tab and the hovered one, and only when that index has actually changed.
        /// </summary>
        private void ComposeHighlight(int index, ref LoadedTexture texture, ref int composedIndex)
        {
            composedIndex = index;
            if (index < 0 || index >= tabWidths.Length) return;

            try
            {
                double padding = scaled(UnscaledTabPadding);
                int height = Math.Max(1, (int)Bounds.InnerHeight + 1);

                ImageSurface surface = new ImageSurface(Format.Argb32, Math.Max(1, tabWidths[index]), height);
                Context ctx = genContext(surface);

                try
                {
                    TabPath(ctx, 0.0, tabWidths[index], height);
                    double[] bg = GuiStyle.DialogStrongBgColor;
                    ctx.SetSourceRGBA(bg[0], bg[1], bg[2], bg[3]);
                    ctx.FillPreserve();

                    double[] hl = GuiStyle.DialogHighlightColor;
                    ctx.SetSourceRGBA(hl[0], hl[1], hl[2], hl[3]);
                    ctx.LineWidth = 2.0;
                    ctx.Stroke();

                    selectedFont.SetupContext(ctx);
                    DrawTextLineAt(ctx, tabNames[index], padding, ((float)height - fontHeight) / 2f);

                    generateTexture(surface, ref texture);
                }
                finally
                {
                    ctx.Dispose();
                    surface.Dispose();
                }
            }
            catch (Exception e)
            {
                composedIndex = -1;
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not compose a profile tab highlight: " + e.Message);
            }
        }

        /// <summary>Rounded on top, square at the bottom, so the tab reads as attached to the list below it.</summary>
        private static void TabPath(Context ctx, double x, int width, double height)
        {
            double r = scaled(3.0);
            ctx.NewPath();
            ctx.MoveTo(x, height);
            ctx.LineTo(x, r);
            ctx.Arc(x + r, r, r, Math.PI, 1.5 * Math.PI);
            ctx.Arc(x + (double)width - r, r, r, -0.5 * Math.PI, 0.0);
            ctx.LineTo(x + (double)width, height);
            ctx.ClosePath();
        }

        public override void RenderInteractiveElements(float deltaTime)
        {
            if (scrollOffset != composedScrollOffset) ComposeStrip();

            int hovered = TabIndexAt(api.Input.MouseX, api.Input.MouseY);
            if (hovered != composedHoverIndex) ComposeHighlight(hovered, ref hoverTexture, ref composedHoverIndex);
            if (ActiveIndex != composedActiveIndex) ComposeHighlight(ActiveIndex, ref activeTexture, ref composedActiveIndex);

            api.Render.PushScissor(Bounds, stacking: true);

            api.Render.Render2DTexture(stripTexture.TextureId, (int)Bounds.renderX, (int)Bounds.renderY, (int)Bounds.InnerWidth + 1, (int)Bounds.InnerHeight + 1);

            DrawHighlight(activeTexture, composedActiveIndex);
            if (composedHoverIndex != composedActiveIndex) DrawHighlight(hoverTexture, composedHoverIndex);

            api.Render.PopScissor();
        }

        private void DrawHighlight(LoadedTexture texture, int index)
        {
            if (index < 0 || index >= tabWidths.Length) return;
            if (texture == null || texture.TextureId == 0) return;

            api.Render.Render2DTexturePremultipliedAlpha(
                texture.TextureId,
                (int)(Bounds.renderX - scrollOffset + tabOffsets[index]),
                (int)Bounds.renderY,
                tabWidths[index],
                (int)Bounds.InnerHeight + 1);
        }

        /// <summary>Index of the tab under the given absolute screen position, or -1.</summary>
        private int TabIndexAt(int absPointX, int absPointY)
        {
            if (!Bounds.PointInside(absPointX, absPointY)) return -1;

            double relX = (double)absPointX - Bounds.absX + scrollOffset;
            int index = TabIndexBefore(relX);
            if (index < 0) return -1;

            // The gap between two tabs belongs to neither of them.
            return relX < tabOffsets[index] + tabWidths[index] ? index : -1;
        }

        /// <summary>
        /// Index of the last tab starting at or before <paramref name="offset"/>, or -1 when the
        /// offset is left of the first tab. Binary search, so the per-frame hover test does not walk
        /// every profile.
        /// </summary>
        private int TabIndexBefore(double offset)
        {
            if (tabOffsets.Length == 0 || offset < 0.0) return -1;

            int low = 0;
            int high = tabOffsets.Length - 1;

            while (low < high)
            {
                int mid = (low + high + 1) / 2;
                if (tabOffsets[mid] <= offset) low = mid;
                else high = mid - 1;
            }

            return tabOffsets[low] <= offset ? low : -1;
        }

        /// <summary>
        /// The rectangle one tab is drawn as, in absolute screen pixels - the same numbers
        /// <see cref="RenderInteractiveElements"/> stamps its highlight at, so anything anchored to
        /// it lines up with what the player sees. False for an index that is not a tab.
        /// <para>
        /// The rectangle is NOT clipped to the strip: a tab scrolled half out of view still reports
        /// where it really is, and it is up to the caller to keep whatever it anchors on screen.
        /// </para>
        /// </summary>
        public bool TryGetTabRect(int index, out double x, out double y, out double width, out double height)
        {
            x = 0.0;
            y = 0.0;
            width = 0.0;
            height = 0.0;

            try
            {
                if (index < 0 || index >= tabWidths.Length) return false;
                if (Bounds?.ParentBounds == null) return false;

                x = Bounds.absX - scrollOffset + tabOffsets[index];
                y = Bounds.absY;
                width = tabWidths[index];
                height = Bounds.InnerHeight;

                return width > 1.0 && height > 1.0;
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not locate profile tab " + index + ": " + e.Message);
                return false;
            }
        }

        public override void OnMouseDownOnElement(ICoreClientAPI api, MouseEvent args)
        {
            int index = TabIndexAt(args.X, args.Y);
            if (index < 0) return;

            // Mark handled and read the button before invoking: the handler rebuilds the whole
            // screen, which disposes this element.
            args.Handled = true;
            EnumMouseButton button = args.Button;

            api.Gui.PlaySound("menubutton_wood");
            onTabAction?.Invoke(index, button);
        }

        public override void OnMouseWheel(ICoreClientAPI api, MouseWheelEventArgs args)
        {
            if (MaxScrollOffset <= 0.0) return;
            if (!Bounds.PointInside(api.Input.MouseX, api.Input.MouseY)) return;

            args.SetHandled();

            // deltaPrecise is the wheel delta vanilla's own continuous scrollers read on 1.22
            // (GuiElementScrollbar.OnMouseWheel). 1.22 has no DeltaY.
            SetScrollOffset(scrollOffset - (double)args.deltaPrecise * scaled(20.0));
        }

        public override void Dispose()
        {
            base.Dispose();

            stripTexture?.Dispose();
            activeTexture?.Dispose();
            hoverTexture?.Dispose();
        }
    }
}
