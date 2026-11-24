using System;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace BetterMods.Gui
{
    /// <summary>
    /// The parts <see cref="GuiElementVersionMenu"/> and <see cref="GuiElementRenamePanel"/> have in
    /// common.
    /// <para>
    /// The two popups are built the same way - a composer element added after <c>EndClip()</c> and
    /// last, whose bounds are cut out of the layout tree and positioned in raw screen pixels - but
    /// they cannot share a base class: one derives from <c>GuiElementListMenu</c> for its focus and
    /// element-table plumbing, the other straight from <c>GuiElement</c>. So the shared behaviour
    /// lives here instead, as statics that take whatever of the element they need.
    /// </para>
    /// <para>
    /// This is deliberately only the parts that must be IDENTICAL for the two to read as siblings:
    /// the frame, the detach, the anchoring rule and the text centring. Everything about what each
    /// popup contains stays in the element that owns it.
    /// </para>
    /// </summary>
    internal static class PopupChrome
    {
        /// <summary>
        /// Cuts bounds out of the composer's layout tree and re-homes them on an empty parent, so
        /// they are positioned in raw screen pixels and cannot grow a FitToChildren dialog.
        /// <c>GuiComposer.AddInteractiveElement</c> always parents what it is given, so this undoes
        /// that immediately afterwards.
        /// </summary>
        /// <param name="bounds">Bounds to re-home.</param>
        /// <param name="what">Names the popup in the log line if it fails.</param>
        internal static void Detach(ElementBounds bounds, string what)
        {
            try
            {
                bounds.ParentBounds?.ChildBounds.Remove(bounds);
                bounds.ParentBounds = ElementBounds.Empty;
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not detach the " + what + " bounds: " + e.Message);
            }
        }

        /// <summary>
        /// Places a popup of the given size against its anchor: below it by <paramref name="gap"/>,
        /// or above it when below would run off the bottom of the screen and there IS room above.
        /// Either way the result is finally clamped into the screen, so an anchor near an edge still
        /// gets a fully visible popup.
        /// <para>
        /// <paramref name="preferredX"/> is whatever horizontal anchoring the caller wants - the
        /// version picker hangs its RIGHT edge off the trigger, the rename panel puts its LEFT edge
        /// on the tab - and is subject to the same clamp.
        /// </para>
        /// <para>
        /// <paramref name="minX"/> raises the left limit above the screen margin. The main menu passes
        /// the sidebar's right edge (<see cref="MenuLayout.ClearLeftPx"/>) there: the sidebar is drawn
        /// after the screen at the same depth and is offered every mouse event first, so a popup that
        /// slid under it would be painted over and lose its clicks. The left limit wins over the right
        /// one - a popup too wide for the space beside the sidebar starts at the sidebar's edge and
        /// runs off the right of the screen rather than back under it.
        /// </para>
        /// </summary>
        internal static void Place(
            double preferredX, double anchorTop, double anchorBottom,
            double width, double height,
            double gap, double margin,
            double screenWidth, double screenHeight,
            out double x, out double y,
            double minX = 0.0)
        {
            double below = anchorBottom + gap;
            double above = anchorTop - gap - height;

            bool openUpward = below + height > screenHeight - margin && above >= margin;

            y = openUpward ? above : below;
            y = GameMath.Clamp(y, margin, Math.Max(margin, screenHeight - margin - height));

            double left = Math.Max(margin, minX);
            x = GameMath.Clamp(preferredX, left, Math.Max(left, screenWidth - margin - width));
        }

        /// <summary>
        /// Reads a popup's <c>LeftLimit</c> without letting it throw into the layout that asked.
        /// </summary>
        internal static double ReadLimit(Func<double> limit, string what)
        {
            if (limit == null) return 0.0;

            try
            {
                double value = limit();
                return double.IsNaN(value) || double.IsInfinity(value) ? 0.0 : value;
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not read the " + what + " left limit: " + e.Message);
                return 0.0;
            }
        }

        /// <summary>
        /// Bakes a popup's frame: a dark outline at its edge and the engine's own bevel just inside
        /// it - the same "flat dark, then emboss" order <c>GuiElementInset</c> (what <c>AddInset</c>
        /// builds) uses for every framed region in the game, so both popups read as siblings of each
        /// other and of the engine's own panels.
        /// <para>
        /// The corner radius is <c>GuiStyle.ElementBGRadius</c> and the bevel is
        /// <c>GuiElement.EmbossRoundRectangleElement</c> at its element defaults. The outline is the
        /// list menu's own edge stroke - black, which is what it already draws - taken from alpha 0.5
        /// to 0.8 and given the full 2px it asks for; vanilla puts that stroke on the surface edge, so
        /// half of it is clipped away and a popup over a mod row of nearly the same colour has no
        /// visible boundary at all. <c>GuiStyle.DialogBorderColor</c> is deliberately not used: at
        /// alpha 0.3 over <c>DialogStrongBgColor</c> it is the invisible hairline this is fixing.
        /// </para>
        /// <para>
        /// The interior is left transparent, so each popup's own body texture is still what separates
        /// it from whatever it overlaps.
        /// </para>
        /// </summary>
        /// <param name="owner">The element the frame belongs to - for the emboss and its API.</param>
        /// <param name="api">The element's <c>api</c>, which is protected and not reachable from here.</param>
        /// <param name="widthPx">Drawn width, in absolute screen pixels.</param>
        /// <param name="heightPx">Drawn height, in absolute screen pixels.</param>
        /// <param name="texture">The frame texture to load or update.</param>
        /// <param name="what">Names the popup in the log line if the bake fails.</param>
        internal static void ComposeFrame(
            GuiElement owner, ICoreClientAPI api,
            double widthPx, double heightPx,
            ref LoadedTexture texture, string what)
        {
            try
            {
                int width = (int)widthPx;
                int height = (int)heightPx;
                if (width < 4 || height < 4) return;

                ImageSurface surface = new ImageSurface(Format.Argb32, width, height);
                Context ctx = GuiElement.GenContext(surface);

                try
                {
                    GuiElement.RoundRectangle(ctx, 1.0, 1.0, width - 2.0, height - 2.0, GuiStyle.ElementBGRadius);
                    ctx.SetSourceRGBA(0.0, 0.0, 0.0, 0.8);
                    ctx.LineWidth = 2.0;
                    ctx.Stroke();

                    // Not inverse: the popup sits ON what it overlaps, it is not cut into it.
                    owner.EmbossRoundRectangleElement(ctx, 2.0, 2.0, width - 4.0, height - 4.0, inverse: false, depth: 2);

                    // GuiElement.generateTexture is protected, so this is its one-line body. Same
                    // call, same linear magnification default.
                    api.Gui.LoadOrUpdateCairoTexture(surface, true, ref texture);
                }
                finally
                {
                    ctx.Dispose();
                    surface.Dispose();
                }
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not compose the " + what + " frame: " + e.Message);
            }
        }

        /// <summary>
        /// How far below the top of a row of <paramref name="rowHeight"/> a line of
        /// <paramref name="font"/> has to start to be vertically centred in it.
        /// <para>
        /// Centred on ascent + descent, NOT on <c>FontExtents.Height</c>: Cairo folds the font's line
        /// gap into Height and that gap sits below the descent, so centring on it renders the line
        /// visibly low. <c>TextDrawUtil.DrawTextLine</c> puts the baseline at <c>offsetY + Ascent</c>,
        /// so offsetting by half the leftover of the real glyph band is what lands the glyphs in the
        /// middle.
        /// </para>
        /// </summary>
        internal static double CentringSlack(CairoFont font, double rowHeight)
        {
            FontExtents extents = font.GetFontExtents();
            return (rowHeight - (extents.Ascent + extents.Descent)) / 2.0;
        }
    }
}
