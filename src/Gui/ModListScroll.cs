using System;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace BetterMods.Gui
{
    /// <summary>
    /// Carries a mod list's scroll position across a rebuild.
    /// <para>
    /// Both surfaces throw their whole composer away to refresh the list - the version label and the
    /// on/off state are baked into each cell's Cairo texture, so nothing short of a rebuild shows a
    /// change - and a fresh composer starts at the top. These two calls put the player back where
    /// they were.
    /// </para>
    /// <para>
    /// The scrollbar works entirely in UNSCALED units: <c>SetHeights</c> is fed
    /// <c>clip.fixedHeight</c> / <c>list.fixedHeight</c> on both surfaces, and its
    /// <c>onNewScrollbarValue</c> writes the cell list's <c>fixedY</c>. <c>CurrentYPosition</c> is
    /// therefore the content offset in those same units, which is what makes it meaningful to carry
    /// from one composer to the next.
    /// </para>
    /// </summary>
    public static class ModListScroll
    {
        /// <summary>
        /// The content offset the list is currently showing, or 0 when there is nothing to read it
        /// from. Must be called BEFORE the composer is disposed.
        /// </summary>
        public static float Capture(GuiComposer composer, string scrollbarKey)
        {
            try
            {
                GuiElementScrollbar scrollbar = composer?.GetScrollbar(scrollbarKey);
                if (scrollbar == null) return 0f;

                float value = scrollbar.CurrentYPosition;
                return float.IsNaN(value) || value <= 0f ? 0f : value;
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not read the mod list scroll position: " + e.Message);
                return 0f;
            }
        }

        /// <summary>
        /// Puts the rebuilt list back to <paramref name="value"/>, clamped to what the new list can
        /// actually scroll to - collapsing a version group changes the row count, and rows have
        /// variable height, so the list can easily be shorter than it was.
        /// <para>
        /// Must be called AFTER the caller's own <c>SetHeights</c>: the conversion between the
        /// scrollbar handle and the content offset is derived from those two heights, so restoring
        /// against a stale total would land somewhere else entirely. The two bounds passed here are
        /// deliberately the same two the caller measured <c>SetHeights</c> from.
        /// </para>
        /// </summary>
        public static void Restore(GuiComposer composer, string scrollbarKey, ElementBounds clipBounds, ElementBounds listBounds, float value)
        {
            try
            {
                if (!(value > 0f)) return;

                GuiElementScrollbar scrollbar = composer?.GetScrollbar(scrollbarKey);
                if (scrollbar == null || clipBounds == null || listBounds == null) return;

                float max = (float)Math.Max(0.0, listBounds.fixedHeight - clipBounds.fixedHeight);
                if (!(max > 0f)) return;

                // The setter divides by the conversion factor the heights above just produced, so it
                // resolves to a handle position; TriggerChanged is what actually moves the cell list.
                scrollbar.CurrentYPosition = GameMath.Clamp(value, 0f, max);
                scrollbar.TriggerChanged();
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not restore the mod list scroll position: " + e.Message);
            }
        }
    }
}
