using System;
using Vintagestory.API.Client;

namespace BetterMods.Gui
{
    /// <summary>
    /// Where a main-menu screen may put things so that the left-hand sidebar never covers them.
    /// <para>
    /// The sidebar (<c>GuiCompositeMainMenuLeft</c>) is a separate composer that
    /// <c>GuiScreen.RenderToDefaultFramebuffer</c> draws AFTER the screen's own composer at the same
    /// depth, and that <c>GuiScreen.OnMouseDown/Up/Move</c> offer every mouse event to FIRST, whenever
    /// the screen's <c>ShowMainMenu</c> is set. Anything of ours that overlaps it is therefore painted
    /// over and loses its clicks. Raising our depth is not a way out: <c>GuiElementTextInput</c> draws
    /// its text at the default z, so a raised background would hide it (NOTES, "GUI draw depth").
    /// Staying out from under it is the only fix.
    /// </para>
    /// <para>
    /// <c>ScreenManager.guiMainmenuLeft</c> is internal, so its width is derived here from the very
    /// public constants its <c>Compose</c> sizes it from - logo + two logo paddings + the wood plank,
    /// 303 unscaled - rather than read back. That also makes it immune to the order in which the
    /// sidebar and a screen recompose after a GUI-scale change.
    /// </para>
    /// </summary>
    internal static class MenuLayout
    {
        /// <summary>
        /// Clearance kept between the sidebar and anything of ours, unscaled. The sidebar's
        /// bottom-left login line is 300 wide at offset 13 and so pokes 10 past the sidebar's own
        /// edge; 15 is also what the sidebar itself leaves before the background logo.
        /// </summary>
        internal const double UnscaledSidebarGap = 15.0;

        /// <summary>Clearance kept between a dialog and the window's right edge, unscaled.</summary>
        internal const double UnscaledWindowMargin = 15.0;

        /// <summary>The sidebar's width in screen pixels at the current GUI scale.</summary>
        internal static double SidebarWidthPx()
        {
            return GuiElement.scaled(
                ElementStdBounds.mainMenuUnscaledLogoSize
                + 2 * ElementStdBounds.mainMenuUnscaledLogoHorPadding
                + ElementStdBounds.mainMenuUnscaledWoodPlankWidth);
        }

        /// <summary>
        /// The leftmost screen x anything of ours may occupy on <paramref name="screen"/>: just right
        /// of the sidebar plus <see cref="UnscaledSidebarGap"/> while the sidebar is shown, else 0.
        /// Read live, so it follows <c>ShowMainMenu</c> and the GUI scale.
        /// </summary>
        internal static double ClearLeftPx(GuiScreen screen)
        {
            try
            {
                if (screen == null || !screen.ShowMainMenu) return 0.0;
                return SidebarWidthPx() + GuiElement.scaled(UnscaledSidebarGap);
            }
            catch (Exception)
            {
                return 0.0;
            }
        }

        /// <summary>
        /// The widest a dialog may be, in screen pixels, and still sit clear of the sidebar (when it
        /// is shown) and of the window's edges.
        /// </summary>
        internal static double AvailableWidthPx(GuiScreen screen, double windowWidth)
        {
            double left = screen != null && screen.ShowMainMenu
                ? ClearLeftPx(screen)
                : GuiElement.scaled(UnscaledWindowMargin);

            return windowWidth - left - GuiElement.scaled(UnscaledWindowMargin);
        }

        /// <summary>
        /// The left edge that centres <paramref name="width"/> in the space the screen leaves free.
        /// <para>
        /// With the sidebar shown this is vanilla's own rule (<c>GuiScreen.Cmp_OnRecomposed</c>:
        /// <c>sidebar + (W - sidebar - width) / 2</c>), but floored at the sidebar's edge plus the
        /// gap, so a dialog too wide to centre there sits flush beside the sidebar and runs off the
        /// right edge instead of sliding underneath it. Without the sidebar it is centred on the full
        /// window, floored at 0.
        /// </para>
        /// </summary>
        internal static double CentredLeftPx(GuiScreen screen, double windowWidth, double width)
        {
            if (screen != null && screen.ShowMainMenu)
            {
                double sidebar = SidebarWidthPx();
                return Math.Max(ClearLeftPx(screen), sidebar + (windowWidth - sidebar - width) / 2.0);
            }

            return Math.Max(0.0, (windowWidth - width) / 2.0);
        }

        /// <summary>
        /// Re-applies <see cref="CentredLeftPx"/> to a screen composer's root bounds. Meant to be
        /// subscribed to <c>GuiComposer.OnComposed</c> AFTER <c>dialogBase</c>'s own handler, so it
        /// has the last word on every (re)compose - the engine recomposes on a resize or a GUI-scale
        /// change without going back through the screen's own layout code.
        /// <para>Never throws: it runs inside <c>GuiComposer.Compose</c>.</para>
        /// </summary>
        internal static void Centre(GuiScreen screen, GuiComposer composer)
        {
            try
            {
                ElementBounds root = composer?.Bounds;
                if (screen?.ScreenManager == null || root == null) return;

                double windowWidth = screen.ScreenManager.GamePlatform.FramebufferSize.Width;
                root.absOffsetX = CentredLeftPx(screen, windowWidth, root.OuterWidth);
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not centre a menu dialog: " + e.Message);
            }
        }
    }
}
