using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

namespace BetterMods.Gui
{
    /// <summary>
    /// How hard a card button holds on to its place when the row is too narrow for every button. The
    /// lowest priority drops first; <see cref="Essential"/> is never dropped - it is elided to fit.
    /// </summary>
    public enum ModCardButtonPriority
    {
        /// <summary>Nice to have (the mod page link). The first to go on a narrow card.</summary>
        Optional,

        /// <summary>An action worth keeping (Fix dependencies). Goes only after every optional one.</summary>
        Normal,

        /// <summary>The update action or its label. Always kept; its label is cut down if it must be.</summary>
        Essential
    }

    /// <summary>How a card button is drawn, and whether it is a control at all.</summary>
    public enum ModCardButtonStyle
    {
        /// <summary>A small raised button. Clickable when it has an action.</summary>
        Button,

        /// <summary>Green text (an update exists, one is being installed). Never clickable.</summary>
        Info,

        /// <summary>Warning-coloured text (the installed release was retracted). Never clickable.</summary>
        Warning,

        /// <summary>
        /// A small raised button with a warning-coloured label - an action worth a second look (a
        /// release made for an older game version). Clickable when it has an action.
        /// </summary>
        WarningButton,

        /// <summary>
        /// A button that cannot be pressed right now: drawn flat with a faint outline and a dimmed
        /// label instead of raised. Never clickable - a click on it is still consumed (like every
        /// button-row hit, see <see cref="ModCard.TryClick"/>), so it never toggles the mod and does
        /// nothing. The tooltip says why it is disabled.
        /// </summary>
        DisabledButton
    }

    /// <summary>
    /// One entry in a card's button row. The row is baked into the cell texture, so a card button is a
    /// drawn rectangle plus a hit zone, not a composed element (NOTES: a mod row only receives
    /// forwarded mouse events). Labels are ASCII - Cairo's toy font API does no glyph fallback.
    /// </summary>
    public sealed class ModCardButton
    {
        public ModCardButton(string label, ModCardButtonPriority priority, ModCardButtonStyle style, Action onClick = null)
        {
            Label = label;
            Priority = priority;
            Style = style;
            OnClick = onClick;
        }

        public string Label { get; }

        public ModCardButtonPriority Priority { get; }

        public ModCardButtonStyle Style { get; }

        /// <summary>
        /// Runs from the row's left-click delegate, i.e. on mouse-UP and from inside the composer's
        /// element walk - it must not rebuild the screen inline (defer it), and it must not throw
        /// (it is wrapped anyway).
        /// </summary>
        public Action OnClick { get; }

        public bool Clickable => IsFramed && Style != ModCardButtonStyle.DisabledButton && OnClick != null;

        /// <summary>Drawn as a button (raised, or flat for a disabled one) rather than bare text: padded, label centred.</summary>
        public bool IsFramed => Style == ModCardButtonStyle.Button || Style == ModCardButtonStyle.WarningButton || Style == ModCardButtonStyle.DisabledButton;
    }

    /// <summary>
    /// The per-row state of a mod card that is not on <c>ModCellEntry</c> - the update marker and the
    /// button row - and the button row's layout, drawing and hit testing.
    /// <para>
    /// <b>Extension point.</b> A surface describes a row's buttons with <see cref="AddButton"/> before
    /// the cell is composed; nothing else needs to know. <see cref="Layout"/> right-aligns the whole
    /// row against the version slot's right edge, in the order the buttons were added (so the last
    /// one added - the update action - keeps the corner). A row too narrow for everything drops
    /// buttons lowest <see cref="ModCardButtonPriority"/> first, never overlaps them, and never drops
    /// an <see cref="ModCardButtonPriority.Essential"/> one - that is elided to fit instead. The same
    /// layout feeds the draw, <see cref="TryClick"/> and <see cref="IsOverControl"/>, so what is
    /// drawn is what is hit.
    /// </para>
    /// <para>
    /// The card also carries an optional warning-coloured <b>version note</b> (<c>"(Retracted)"</c>),
    /// drawn on the top line right after the version; <see cref="ModCellLayout"/> owns its geometry.
    /// </para>
    /// <para>
    /// Every button sits left of the row's click split (<c>InnerWidth - scaled(40)</c>), so a click on
    /// one always arrives through <c>OnMouseDownOnCellLeft</c> and never reaches the on/off switch.
    /// Both surfaces route that delegate through <see cref="TryClick"/> before anything else, and a
    /// click on any button - clickable or a plain label - is consumed there, so it can never also
    /// toggle the mod.
    /// </para>
    /// </summary>
    public static class ModCard
    {
        /// <summary>Horizontal padding inside a button, unscaled.</summary>
        private const double UnscaledButtonPadX = 8.0;

        /// <summary>Padding above and below a button's text, unscaled.</summary>
        private const double UnscaledButtonPadY = 3.0;

        /// <summary>Clear space between two buttons, unscaled.</summary>
        private const double UnscaledButtonGap = 6.0;

        /// <summary>Widest a button's label may be drawn before it is elided, unscaled.</summary>
        private const double UnscaledMaxLabelWidth = 160.0;

        private sealed class State
        {
            public bool UpdateMarker;
            public string VersionNote;
            public readonly List<ModCardButton> Buttons = new List<ModCardButton>();
        }

        /// <summary>
        /// Weak, like the picker marks: <c>ModCellEntry</c> is the game's type and the entries die
        /// with the composer that holds them, so nothing here ever needs clearing.
        /// </summary>
        private static readonly ConditionalWeakTable<ModCellEntry, State> States = new ConditionalWeakTable<ModCellEntry, State>();

        /// <summary>Shows (or hides) the "update available" marker left of the row's version.</summary>
        public static void SetUpdateMarker(ModCellEntry cell, bool on)
        {
            if (cell == null) return;

            try
            {
                States.GetValue(cell, _ => new State()).UpdateMarker = on;
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not mark a mod card's update state: " + e.Message);
            }
        }

        public static bool HasUpdateMarker(ModCellEntry cell)
        {
            return ModCellLayout.CardLayoutAvailable && cell != null && States.TryGetValue(cell, out State state) && state.UpdateMarker;
        }

        /// <summary>
        /// Sets (or, with null, clears) the warning-coloured note drawn right of the row's version on
        /// the top line - <c>"(Retracted)"</c>. ASCII. Call before the cell is composed.
        /// </summary>
        public static void SetVersionNote(ModCellEntry cell, string note)
        {
            if (cell == null) return;

            try
            {
                States.GetValue(cell, _ => new State()).VersionNote = string.IsNullOrWhiteSpace(note) ? null : note;
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not set a mod card's version note: " + e.Message);
            }
        }

        /// <summary>The row's version note, or null. Null whenever the card is unavailable.</summary>
        public static string VersionNote(ModCellEntry cell)
        {
            return ModCellLayout.CardLayoutAvailable && cell != null && States.TryGetValue(cell, out State state) ? state.VersionNote : null;
        }

        /// <summary>Appends a button to the row. Call before the cell is composed.</summary>
        public static void AddButton(ModCellEntry cell, ModCardButton button)
        {
            if (cell == null || button == null || string.IsNullOrEmpty(button.Label)) return;

            try
            {
                States.GetValue(cell, _ => new State()).Buttons.Add(button);
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not add a button to a mod card: " + e.Message);
            }
        }

        // ---------------------------------------------------------------- layout

        private struct PlacedButton
        {
            public ModCardButton Button;
            public string Text;
            public double X;
            public double Width;
        }

        private static CairoFont ButtonFont() => CairoFont.WhiteDetailText();

        /// <summary>
        /// Top and height of the button row, surface-local. The row's bottom is the icon's bottom
        /// (<c>Compose</c> draws the icon at <c>(int)absPaddingY + 5</c>), so the row reads as the
        /// card's foot whatever is drawn above it. False when the card is too short for a row clear
        /// of the top line - not the case for the fixed card height, but a backstop.
        /// </summary>
        private static bool TryGetRowBand(GuiElementModCell element, out double top, out double height)
        {
            ElementBounds bounds = element.Bounds;

            height = ModCellLayout.InkHeight(ButtonFont()) + 2.0 * GuiElement.scaled(UnscaledButtonPadY);
            // Clamped to the same raw 5px bottom margin the card height is derived with
            // (ModCellLayout.MinUnscaledRowHeight), so it equals the icon's bottom on the fixed card.
            double bottom = (int)bounds.absPaddingY + 5 + ModCellLayout.LockedIconSize(0.0);
            bottom = Math.Min(bottom, bounds.OuterHeight - 5.0);
            top = bottom - height;

            return top >= ModCellLayout.TopLineBottom(bounds);
        }

        private static List<PlacedButton> Layout(GuiElementModCell element, out double rowTop, out double rowHeight)
        {
            rowTop = -1.0;
            rowHeight = 0.0;
            List<PlacedButton> placed = new List<PlacedButton>();

            if (!ModCellLayout.CardLayoutAvailable || element?.Bounds == null || element.cell == null) return placed;
            if (!TryGetRowBand(element, out rowTop, out rowHeight)) return placed;
            if (!States.TryGetValue(element.cell, out State state) || state.Buttons.Count == 0) return placed;

            CairoFont font = ButtonFont();
            double pad = GuiElement.scaled(UnscaledButtonPadX);
            double gap = GuiElement.scaled(UnscaledButtonGap);
            double maxLabel = GuiElement.scaled(UnscaledMaxLabelWidth);

            double left = ModCellLayout.TextIndent(element);
            double right = ModCellLayout.ContentRight(element.Bounds);
            double available = right - left;
            if (!(available > 0.0)) return placed;

            int count = state.Buttons.Count;
            string[] texts = new string[count];
            double[] widths = new double[count];
            bool[] kept = new bool[count];

            for (int i = 0; i < count; i++)
            {
                texts[i] = ModCellLayout.Elide(state.Buttons[i].Label, font, maxLabel);
                widths[i] = font.GetTextExtents(texts[i]).XAdvance + 2.0 * pad;
            }

            // Admit buttons highest priority first (add order within a priority); a button that does
            // not fit beside those already admitted is dropped. An essential one that does not fit
            // even alone is elided down to the whole row instead.
            double used = 0.0;
            for (int priority = (int)ModCardButtonPriority.Essential; priority >= (int)ModCardButtonPriority.Optional; priority--)
            {
                for (int i = 0; i < count; i++)
                {
                    if ((int)state.Buttons[i].Priority != priority) continue;

                    double extra = widths[i] + (used > 0.0 ? gap : 0.0);
                    if (used + extra <= available)
                    {
                        kept[i] = true;
                        used += extra;
                        continue;
                    }

                    if (priority != (int)ModCardButtonPriority.Essential || used > 0.0) continue;

                    texts[i] = ModCellLayout.Elide(state.Buttons[i].Label, font, available - 2.0 * pad);
                    if (string.IsNullOrEmpty(texts[i])) continue;

                    widths[i] = font.GetTextExtents(texts[i]).XAdvance + 2.0 * pad;
                    if (widths[i] > available) continue;

                    kept[i] = true;
                    used = widths[i];
                }
            }

            // Lay the admitted ones out right-aligned, in add order: the last one added ends at the
            // version slot's right edge.
            double cursorRight = right;
            for (int i = count - 1; i >= 0; i--)
            {
                if (!kept[i]) continue;

                double x = cursorRight - widths[i];
                placed.Add(new PlacedButton { Button = state.Buttons[i], Text = texts[i], X = x, Width = widths[i] });
                cursorRight = x - gap;
            }

            return placed;
        }

        // ---------------------------------------------------------------- drawing

        /// <summary>
        /// Paints the row's buttons and returns the top of the button row (whether or not anything was
        /// in it), for the status line to centre against; -1 when there is no row band. Never throws.
        /// </summary>
        internal static double DrawButtons(GuiElementModCell element, Context ctx)
        {
            double rowTop = -1.0;

            try
            {
                List<PlacedButton> placed = Layout(element, out rowTop, out double rowHeight);
                if (placed.Count == 0) return rowTop;

                foreach (PlacedButton button in placed)
                {
                    DrawButton(element, ctx, button, rowTop, rowHeight);
                }
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not draw a mod card's buttons: " + e.Message);
            }

            return rowTop;
        }

        /// <summary>
        /// A <see cref="ModCardButtonStyle.Button"/> is a small raised button - the engine's own
        /// emboss, the opposite way round from the sunken on/off box and picker well, so it reads as
        /// something to press rather than a field. The two label styles are bare coloured text.
        /// </summary>
        private static void DrawButton(GuiElementModCell element, Context ctx, PlacedButton placed, double y, double height)
        {
            CairoFont font = ButtonFont();
            double[] color = placed.Button.Style switch
            {
                ModCardButtonStyle.Warning => GuiStyle.WarningTextColor,
                ModCardButtonStyle.WarningButton => GuiStyle.WarningTextColor,
                ModCardButtonStyle.Info => GuiStyle.SuccessTextColor,
                ModCardButtonStyle.DisabledButton => Dimmed(GuiStyle.DialogDefaultTextColor),
                _ => GuiStyle.DialogDefaultTextColor
            };
            if (color != null) font.WithColor(color);

            ctx.Save();
            try
            {
                double x = placed.X;
                double width = placed.Width;

                if (placed.Button.Style == ModCardButtonStyle.DisabledButton)
                {
                    // Flat: the same footprint as a live button, but no emboss - nothing to press.
                    ctx.SetSourceRGBA(1.0, 1.0, 1.0, 0.03);
                    GuiElement.RoundRectangle(ctx, x, y, width, height, 3.0);
                    ctx.FillPreserve();
                    ctx.SetSourceRGBA(1.0, 1.0, 1.0, 0.12);
                    ctx.LineWidth = Math.Max(1.0, GuiElement.scaled(1.0));
                    ctx.Stroke();
                }
                else if (placed.Button.IsFramed)
                {
                    ctx.SetSourceRGBA(1.0, 1.0, 1.0, 0.08);
                    GuiElement.RoundRectangle(ctx, x, y, width, height, 3.0);
                    ctx.Fill();

                    element.EmbossRoundRectangleElement(ctx, x, y, width, height, inverse: false, (int)GuiElement.scaled(2.0), 2);
                }

                // A button centres its label. A bare label carries no frame, so it is flush with the
                // row's right-hand packing edge instead - lined up with the version text above it when
                // it is last - and its padding only widens the hit zone.
                double textWidth = font.GetTextExtents(placed.Text).XAdvance;
                double textX = placed.Button.IsFramed ? x + (width - textWidth) / 2.0 : x + width - textWidth;

                ModCellLayout.DrawLineCentred(ctx, font, placed.Text, textX, y + height / 2.0);
            }
            finally
            {
                ctx.Restore();
            }
        }

        /// <summary>A text colour at reduced opacity, for a disabled button's label.</summary>
        private static double[] Dimmed(double[] color)
        {
            if (color == null || color.Length < 3) return new[] { 1.0, 1.0, 1.0, 0.35 };

            double alpha = color.Length >= 4 ? color[3] : 1.0;
            return new[] { color[0], color[1], color[2], alpha * 0.4 };
        }

        // ---------------------------------------------------------------- hit testing

        /// <summary>
        /// Handles a left-half click on a card: when the mouse is over a button, runs its action (if
        /// it is clickable) and returns true, meaning the click is consumed and must not reach
        /// anything else - in particular not the mod toggle. False when the click was not on a button.
        /// Cannot throw.
        /// </summary>
        public static bool TryClick(GuiElementModCell element, int mouseX, int mouseY)
        {
            ModCardButton hit;

            try
            {
                hit = ButtonAt(element, mouseX, mouseY);
                if (hit == null) return false;
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not hit-test a mod card's buttons: " + e.Message);
                return false;
            }

            if (!hit.Clickable) return true;

            try
            {
                hit.OnClick();
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Error("[BetterMods] A mod card button failed (" + hit.Label + ").");
                BetterModsModSystem.Logger?.Error(e);
            }

            return true;
        }

        /// <summary>
        /// True when the mouse is over any of the card's controls: a button or label in the button row
        /// (except a disabled button), the version picker trigger, or the on/off column right of the
        /// click split. The hover tooltip stays out of the way there. Cannot throw.
        /// </summary>
        public static bool IsOverControl(GuiElementModCell element, int mouseX, int mouseY)
        {
            try
            {
                if (element?.Bounds == null) return false;

                Vec2d local = element.Bounds.PositionInside(mouseX, mouseY);
                if (local == null) return false;
                if (local.X > ModCellLayout.ClickSplit(element.Bounds)) return true;

                // A disabled button is not a control: hovering it is exactly when the player wants
                // the tooltip that says why it is disabled.
                ModCardButton button = ButtonAt(element, mouseX, mouseY);
                if (button != null) return button.Style != ModCardButtonStyle.DisabledButton;

                return ModCellLayout.IsInVersionZone(element, mouseX, mouseY);
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not hit-test a mod card: " + e.Message);
                return false;
            }
        }

        private static ModCardButton ButtonAt(GuiElementModCell element, int mouseX, int mouseY)
        {
            List<PlacedButton> placed = Layout(element, out double rowTop, out double rowHeight);
            if (placed.Count == 0) return null;

            double localX = mouseX - element.Bounds.absX;
            double localY = mouseY - element.Bounds.absY;
            if (localY < rowTop || localY > rowTop + rowHeight) return null;

            foreach (PlacedButton button in placed)
            {
                if (localX >= button.X && localX <= button.X + button.Width) return button.Button;
            }

            return null;
        }
    }
}
