using System;
using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;

namespace BetterMods.Gui
{
    /// <summary>
    /// The mod cards' hover tooltip: one <see cref="GuiElementHoverText"/> per surface that shows the
    /// <c>HoverText</c> of whichever card is under the mouse.
    /// <para>
    /// Vanilla's singleplayer screen does per-row tooltips by adding one hover-text element per row,
    /// on a child of that row's bounds (so it moves with the list when it scrolls - cell bounds are
    /// parented to the list bounds, and every <c>abs*</c> position is derived live from the parent),
    /// with <c>InsideClipBounds</c> set to the list's clip so a row scrolled out of view cannot
    /// trigger it (<c>GuiScreenSingleplayer.InitGui</c>, 1.23). That does not transplant: those
    /// elements have to be added before the composer is composed, and the mods screen re-creates
    /// its cells AFTER composing (<c>OnScreenLoaded</c> runs <c>InitGui</c>, then
    /// <c>ReloadCells(LoadModCells())</c>), which would leave every per-row element pointing at a
    /// disposed cell.
    /// </para>
    /// <para>
    /// So this keeps the same two ingredients - the cell's own live bounds for scrolling, the list's
    /// clip bounds for clipping - and looks the hovered cell up each frame instead of owning one
    /// element per row. The cell list element survives <c>ReloadCells</c>, so the lookup always sees
    /// the current cells. Shown only over a card's body: over its buttons, its version trigger or
    /// its on/off column (<see cref="ModCard.IsOverControl"/>) it stays out of the way, and the
    /// surface can suppress it entirely while one of its popups is open. The text is recomposed only
    /// when the hovered card changes.
    /// </para>
    /// </summary>
    public sealed class GuiElementModCardTooltip : GuiElementHoverText
    {
        public const string ElementKey = "bettermods-cardtooltip";

        /// <summary>Widest the tooltip wraps at, unscaled (vanilla's singleplayer tooltips use 320).</summary>
        private const int UnscaledMaxWidth = 380;

        private readonly ElementBounds clipBounds;
        private readonly Func<GuiElementCellList<ModCellEntry>> cellList;
        private readonly Func<bool> suppressed;

        private string shownText;
        private bool failed;

        /// <param name="bounds">Any bounds the composer can hold; the panel is placed at the mouse.</param>
        /// <param name="clipBounds">The cell list's clip - only a hover inside it counts.</param>
        /// <param name="cellList">The surface's mod cell list, looked up when needed.</param>
        /// <param name="suppressed">True while the tooltip must not show (a popup is open, the surface is covered).</param>
        public GuiElementModCardTooltip(
            ICoreClientAPI capi, ElementBounds bounds, ElementBounds clipBounds,
            Func<GuiElementCellList<ModCellEntry>> cellList, Func<bool> suppressed)
            : base(capi, "", CairoFont.WhiteDetailText(), UnscaledMaxWidth, bounds)
        {
            this.clipBounds = clipBounds;
            this.cellList = cellList;
            this.suppressed = suppressed;

            // Shown and hidden from RenderInteractiveElements below, not by vanilla's own test
            // against this element's bounds.
            SetAutoDisplay(false);
            SetVisible(true);
        }

        public override void RenderInteractiveElements(float deltaTime)
        {
            if (failed) return;

            try
            {
                string text = HoveredText();
                if (string.IsNullOrEmpty(text)) return;

                if (!string.Equals(text, shownText, StringComparison.Ordinal))
                {
                    shownText = text;
                    SetNewText(text);
                }

                base.RenderInteractiveElements(deltaTime);
            }
            catch (Exception e)
            {
                // Once, not per frame: a broken tooltip is not worth a log flood or a crash.
                failed = true;
                BetterModsModSystem.Logger?.Error("[BetterMods] The mod card tooltip failed and is switched off for this screen.");
                BetterModsModSystem.Logger?.Error(e);
            }
        }

        private string HoveredText()
        {
            if (suppressed != null && suppressed()) return null;

            int mouseX = api.Input.MouseX;
            int mouseY = api.Input.MouseY;
            if (clipBounds == null || !clipBounds.PointInside(mouseX, mouseY)) return null;

            GuiElementCellList<ModCellEntry> list = cellList?.Invoke();
            if (list?.elementCells == null) return null;

            foreach (IGuiElementCell cell in list.elementCells)
            {
                if (!(cell is GuiElementModCell element) || element.Bounds == null) continue;
                if (element.Bounds.PositionInside(mouseX, mouseY) == null) continue;

                return ModCard.IsOverControl(element, mouseX, mouseY) ? null : element.cell?.HoverText;
            }

            return null;
        }
    }
}
