using System;
using System.Runtime.CompilerServices;
using BetterMods.Patches;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.Client.NoObf;
using SurfaceDrawImage = Vintagestory.API.Common.SurfaceDrawImage;

namespace BetterMods.Gui
{
    /// <summary>
    /// Geometry and drawing of the BetterMods mod card - the fixed-height layout a vanilla
    /// <see cref="GuiElementModCell"/> is drawn in once every IL edit in <c>GuiElementModCellPatches</c>
    /// has landed.
    /// <para>
    /// The card, top to bottom (all surface-local; the cell surface starts at the row's OUTER corner,
    /// so <c>absPaddingX/Y</c> are already folded in - NOTES, "Mod row horizontal landmarks"):
    /// </para>
    /// <list type="bullet">
    /// <item><b>Top line</b>, vertically centred on vanilla's on/off box
    /// (<see cref="TopLineCentreY"/>): the locked icon at left (it spans the whole card), the title
    /// right of it - one line, elided - then the update marker, the version label (or its picker
    /// trigger), then the warning-coloured version note (<c>"(Retracted)"</c>, when the row has one)
    /// ending at the version slot's right edge, and vanilla's on/off box beyond the click
    /// split.</item>
    /// <item><b>Status line</b>, between the two: only for a row that has something to say on the card
    /// itself (a load error); otherwise empty. Authors, description, dependencies and update details
    /// live in the hover tooltip (<see cref="GuiElementModCardTooltip"/>).</item>
    /// <item><b>Button row</b>, its bottom aligned with the icon's: see <see cref="ModCard"/>.</item>
    /// </list>
    /// <para>
    /// Every member reached from patched IL goes through <c>ModIconGate</c>, which answers vanilla
    /// until every edit has landed, and nothing here may throw - a Cairo draw that dies mid-
    /// <c>Compose</c> would leave the cell blank forever. Everything that touches Cairo, the element
    /// or the bounds swallows and logs. <see cref="LockedIconSize"/> does not: it is arithmetic over a
    /// constant and a static field, on the hot path of every row's compose.
    /// </para>
    /// </summary>
    public static class ModCellLayout
    {
        /// <summary>
        /// Side of the icon box, unscaled, so it tracks <c>ClientSettings.GUIScale</c> like the rest
        /// of the GUI. Vanilla derives the box from the row height; this is the fixed replacement, and
        /// it is also what sets the card's height (<see cref="MinUnscaledRowHeight"/>).
        /// </summary>
        public const double UnscaledIconSize = 88.0;

        /// <summary>
        /// Vertical cell padding, unscaled - <c>GuiElementCellList.UnscaledCellVerPadding</c>, which
        /// is what the list stamps onto every cell's bounds.
        /// </summary>
        public const double UnscaledCellPadding = 4.0;

        /// <summary>The shortest row vanilla will produce (<c>GuiElementModCell.UpdateCellHeight</c>).</summary>
        public const double VanillaMinRowHeight = 73.0;

        /// <summary>
        /// Gap vanilla leaves between a row's version text and the on/off column - the
        /// <c>scaled(10.0)</c> in <c>GuiElementModCell.Compose</c>'s <c>RightTopText</c> draw.
        /// </summary>
        public const double UnscaledVersionTextGap = 10.0;

        /// <summary>
        /// Clear space vanilla leaves between the icon box and the title, unscaled
        /// (<c>GuiElementModCell.Compose</c>'s <c>icon + 15</c>).
        /// </summary>
        public const double UnscaledIconTextGap = 15.0;

        /// <summary>
        /// Side of vanilla's on/off box, unscaled (<c>GuiElementModCell.unscaledSwitchSize</c>, internal).
        /// The box is drawn at <c>y = 2 * absPaddingY</c>, and its centre is the card's top line.
        /// </summary>
        private const double VanillaSwitchSize = 25.0;

        /// <summary>
        /// Widest a row's version label may be drawn, unscaled, before it is elided. The trigger box
        /// is sized around the label, so an unbounded label is an unbounded box. The full string is
        /// still shown, in the picker.
        /// </summary>
        public const double UnscaledMaxVersionWidth = 120.0;

        /// <summary>ASCII, deliberately: Cairo's toy font API does no glyph fallback.</summary>
        private const string Ellipsis = "...";

        /// <summary>Padding between the trigger box's edge and its contents, unscaled.</summary>
        private const double UnscaledTriggerPadX = 5.0;

        /// <summary>Padding above and below the version text inside the trigger box, unscaled.</summary>
        private const double UnscaledTriggerPadY = 3.0;

        /// <summary>Clear space either side of the divider rule, unscaled.</summary>
        private const double UnscaledTriggerDividerGap = 5.0;

        /// <summary>Width of the divider rule - one dark pixel plus one light one.</summary>
        private const double UnscaledTriggerDividerWidth = 2.0;

        /// <summary>Width of the drawn arrow, unscaled.</summary>
        private const double UnscaledTriggerArrowWidth = 9.0;

        /// <summary>Height of the drawn arrow, unscaled.</summary>
        private const double UnscaledTriggerArrowHeight = 5.0;

        /// <summary>
        /// Everything the trigger box reserves to the RIGHT of the version text, unscaled: the gap
        /// before the divider, the divider, the gap after it, the arrow, and the right padding.
        /// </summary>
        private const double UnscaledTriggerArrowColumn =
            UnscaledTriggerDividerGap + UnscaledTriggerDividerWidth + UnscaledTriggerDividerGap
            + UnscaledTriggerArrowWidth + UnscaledTriggerPadX;

        /// <summary>Width of the update marker triangle, unscaled.</summary>
        private const double UnscaledMarkerWidth = 16.0;

        /// <summary>Height of the update marker triangle, unscaled.</summary>
        private const double UnscaledMarkerHeight = 14.0;

        /// <summary>Clear space between the update marker and the version slot, unscaled.</summary>
        private const double UnscaledMarkerGap = 6.0;

        /// <summary>Clear space between the end of the title and whatever follows it, unscaled.</summary>
        private const double UnscaledTitleGap = 10.0;

        /// <summary>Clear space between the version (or its trigger box) and the version note, unscaled.</summary>
        private const double UnscaledVersionNoteGap = 5.0;

        /// <summary>
        /// Room the top line must keep for the title and the version once the version note is placed,
        /// unscaled. A row narrower than that drops the note (it is in the tooltip anyway) rather than
        /// squeezing the version or the picker trigger.
        /// </summary>
        private const double UnscaledMinTopLineRoom = 140.0;

        // ---------------------------------------------------------------- icon and height

        /// <summary>
        /// True once every card edit has landed (<see cref="ModIconGate.Enabled"/>). False means the
        /// rows are drawn, laid out and hit-tested exactly as vanilla does: no card, no picker trigger,
        /// no buttons, no marker - and no invisible click target where any of them would have been.
        /// </summary>
        public static bool CardLayoutAvailable => ModIconGate.Enabled;

        /// <summary>
        /// The locked icon box side, in scaled pixels. Reached from patched IL through
        /// <c>ModIconGate.IconSize</c>; <paramref name="vanillaSize"/> is what vanilla would have
        /// used and is deliberately IGNORED, so <c>Compose</c> and <c>UpdateCellHeight</c> agree.
        /// </summary>
        public static int LockedIconSize(double vanillaSize)
        {
            return (int)Math.Max(1.0, GuiElement.scaled(UnscaledIconSize));
        }

        /// <summary>
        /// The card's height, unscaled (the cell's <c>fixedHeight</c>; padding comes on top).
        /// <para>
        /// Reached from patched IL through <c>ModIconGate.MinRowHeight</c> (the two <c>73.0</c>
        /// literals of <c>UpdateCellHeight</c>) and from the <c>UpdateCellHeight</c> postfix, which
        /// pins every card to exactly this height.
        /// </para>
        /// <para>
        /// Derivation: the icon is drawn at <c>absPaddingY + 5</c> with side
        /// <c>scaled(UnscaledIconSize)</c>, and the row's usable box is
        /// <c>scaled(height) + 2 * absPaddingY</c>. Asking for the same 5px margin underneath gives
        /// <c>scaled(height) &gt;= scaled(UnscaledIconSize - UnscaledCellPadding) + 10</c>. The two
        /// 5px margins are RAW pixels in vanilla's draw, hence the <c>/ GUIScale</c>. The top line
        /// (<see cref="TopLineCentreY"/>) and the button row (<see cref="ModCard"/>) both fit inside
        /// the icon's span, so nothing else needs room.
        /// </para>
        /// </summary>
        public static double MinUnscaledRowHeight()
        {
            try
            {
                double scale = RuntimeEnv.GUIScale;
                if (!(scale > 0.01)) scale = 1.0;

                return Math.Max(VanillaMinRowHeight, UnscaledIconSize - UnscaledCellPadding + 10.0 / scale);
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not size a mod row: " + e.Message);
                return VanillaMinRowHeight;
            }
        }

        /// <summary>
        /// Draws <paramref name="bmp"/> centred inside the box, scaled to fit with the aspect ratio
        /// preserved - a wide banner letterboxes and a tall icon pillarboxes instead of being
        /// stretched. Signature matches the <c>BitmapExternal</c> overload of
        /// <c>SurfaceDrawImage.Image</c> exactly, and so does <c>ModIconGate.DrawIcon</c>, the operand
        /// the transpiler actually writes.
        /// </summary>
        public static void DrawIconFitted(ImageSurface surface, BitmapExternal bmp, int xPos, int yPos, int width, int height)
        {
            try
            {
                if (surface == null || bmp == null || width <= 0 || height <= 0) return;

                int sourceWidth = bmp.Width;
                int sourceHeight = bmp.Height;

                if (sourceWidth <= 0 || sourceHeight <= 0)
                {
                    // Called statically, not as an extension: cairo-sharp ships a same-named
                    // Image() for SKBitmap, and letting the compiler weigh both overloads would drag
                    // a SkiaSharp reference into this project for no reason.
                    SurfaceDrawImage.Image(surface, bmp, xPos, yPos, width, height);
                    return;
                }

                double scale = Math.Min((double)width / sourceWidth, (double)height / sourceHeight);
                int drawWidth = Math.Max(1, (int)Math.Round(sourceWidth * scale));
                int drawHeight = Math.Max(1, (int)Math.Round(sourceHeight * scale));

                SurfaceDrawImage.Image(surface, bmp, xPos + (width - drawWidth) / 2, yPos + (height - drawHeight) / 2, drawWidth, drawHeight);
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not draw a mod icon (fitted): " + e.Message);
            }
        }

        // ---------------------------------------------------------------- text helpers

        /// <summary>
        /// The version string as it should be DRAWN in a picker row: the label itself when it fits in
        /// <see cref="UnscaledMaxVersionWidth"/>, otherwise cut down with an ASCII ellipsis. Never
        /// throws; on any failure the label is returned untouched.
        /// </summary>
        public static string ElideVersionLabel(string label, CairoFont font)
        {
            return Elide(label, font, GuiElement.scaled(UnscaledMaxVersionWidth));
        }

        /// <summary>
        /// <paramref name="text"/> cut to fit <paramref name="maxPx"/> screen pixels with an ASCII
        /// ellipsis. Never throws; on any failure the text is returned untouched.
        /// </summary>
        public static string Elide(string text, CairoFont font, double maxPx)
        {
            try
            {
                if (string.IsNullOrEmpty(text)) return text;
                if (!(maxPx > 0.0)) return "";

                CairoFont measure = font ?? CairoFont.WhiteSmallText();
                if (measure.GetTextExtents(text).XAdvance <= maxPx) return text;
                if (measure.GetTextExtents(Ellipsis).XAdvance > maxPx) return "";

                // Binary chop rather than one measure per character.
                int low = 0;
                int high = text.Length;
                while (low < high)
                {
                    int mid = (low + high + 1) / 2;
                    if (measure.GetTextExtents(text.Substring(0, mid) + Ellipsis).XAdvance <= maxPx) low = mid;
                    else high = mid - 1;
                }

                return low <= 0 ? Ellipsis : text.Substring(0, low).TrimEnd() + Ellipsis;
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not measure a label: " + e.Message);
                return text;
            }
        }

        /// <summary>Ascent + descent of <paramref name="font"/> - the ink band a line is centred on.</summary>
        internal static double InkHeight(CairoFont font)
        {
            FontExtents extents = font.GetFontExtents();
            return extents.Ascent + extents.Descent;
        }

        /// <summary>
        /// Draws one line of text whose ink band is vertically centred on <paramref name="centreY"/>.
        /// Centred on <c>Ascent + Descent</c>, not <c>Height</c>: Height folds in the line gap below
        /// the descent and would sit the text visibly low (NOTES, text measurement traps). The font's
        /// own colour is used. Caller wraps in Save/Restore.
        /// </summary>
        internal static void DrawLineCentred(Context ctx, CairoFont font, string text, double x, double centreY)
        {
            if (string.IsNullOrEmpty(text)) return;

            font.SetupContext(ctx);
            FontExtents extents = ctx.FontExtents;
            double baseline = centreY - (extents.Ascent + extents.Descent) / 2.0 + extents.Ascent;

            ctx.MoveTo(x, Math.Round(baseline));
            ctx.ShowText(text);
        }

        // ---------------------------------------------------------------- picker rows

        /// <summary>
        /// The rows that have more than one installed version, i.e. the ones that get a dropdown
        /// trigger instead of plain text. A weak table because <c>ModCellEntry</c> is the game's type
        /// and has nowhere to put a flag; entries die with the composer that holds them.
        /// </summary>
        private static readonly ConditionalWeakTable<ModCellEntry, object> VersionPickerRows =
            new ConditionalWeakTable<ModCellEntry, object>();

        private static readonly object PickerRowMarker = new object();

        /// <summary>Marks a row as having a version to pick, before its cell is composed.</summary>
        public static void MarkVersionPicker(ModCellEntry cell)
        {
            if (cell == null) return;

            try
            {
                VersionPickerRows.AddOrUpdate(cell, PickerRowMarker);
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not flag a row as a version picker: " + e.Message);
            }
        }

        /// <summary>
        /// True if the row was marked by <see cref="MarkVersionPicker"/> and the card - which is what
        /// draws the trigger - is available on this game build.
        /// </summary>
        public static bool HasVersionPicker(ModCellEntry cell)
        {
            return CardLayoutAvailable && cell != null && VersionPickerRows.TryGetValue(cell, out _);
        }

        // ---------------------------------------------------------------- card geometry

        /// <summary>
        /// The y every element of the top line is centred on: the centre of vanilla's on/off box,
        /// which <c>Compose</c> draws at <c>absPaddingY + absPaddingY</c> with side
        /// <c>scaled(25)</c>.
        /// </summary>
        internal static double TopLineCentreY(ElementBounds bounds)
        {
            return 2.0 * bounds.absPaddingY + GuiElement.scaled(VanillaSwitchSize) / 2.0;
        }

        /// <summary>
        /// Bottom of the top line's band - half the on/off box below <see cref="TopLineCentreY"/>.
        /// </summary>
        internal static double TopLineBottom(ElementBounds bounds)
        {
            return TopLineCentreY(bounds) + GuiElement.scaled(VanillaSwitchSize) / 2.0;
        }

        /// <summary>
        /// Right edge of the version slot - where vanilla ended its version text,
        /// <c>absPaddingX + InnerWidth - scaled(40) - scaled(10)</c> - clamped so nothing drawn there
        /// can cross <c>InnerWidth - scaled(40)</c>, the point <c>OnMouseUpOnElement</c> splits a row
        /// into left (BetterMods) and right (the on/off switch). Everything the card adds is left of
        /// it, so every click on the card's own controls arrives through
        /// <c>OnMouseDownOnCellLeft</c>. The button row ends here too.
        /// </summary>
        internal static double ContentRight(ElementBounds bounds)
        {
            double right = bounds.absPaddingX + bounds.InnerWidth
                           - GuiElement.scaled(GuiElementModCell.unscaledRightBoxWidth)
                           - GuiElement.scaled(UnscaledVersionTextGap);

            double clickSplit = ClickSplit(bounds);
            return right > clickSplit ? clickSplit : right;
        }

        /// <summary>
        /// Where a row splits into its left and right click halves, surface-local. Reads
        /// <c>GuiElementMainMenuCell.unscaledRightBoxWidth</c>, as vanilla's own split does.
        /// </summary>
        internal static double ClickSplit(ElementBounds bounds)
        {
            return bounds.InnerWidth - GuiElement.scaled(GuiElementMainMenuCell.unscaledRightBoxWidth);
        }

        /// <summary>
        /// Left edge of the title, status line and button row: vanilla's text indent, i.e. the icon
        /// box plus 15 when the mod has an icon, the bare padding when it has none. Computed rather
        /// than taken from <c>Compose</c>'s argument, so the hit test (which has no such argument)
        /// lands on exactly what was drawn.
        /// </summary>
        internal static double TextIndent(GuiElementModCell element)
        {
            double indent = element.Bounds.absPaddingX;
            if (element.cell?.Mod?.Icon != null) indent += LockedIconSize(0.0) + UnscaledIconTextGap;
            return indent;
        }

        /// <summary>
        /// Where the version label (and, on a picker row, its trigger box) sits, surface-local. False
        /// for a row without a version label - vanilla's error rows have none.
        /// </summary>
        private struct VersionSlot
        {
            public string Text;
            public CairoFont Font;
            public double TextLeft;
            public double CentreY;
            public bool HasBox;
            public double BoxX;
            public double BoxY;
            public double BoxWidth;
            public double BoxHeight;

            /// <summary>Leftmost pixel the slot occupies - what the marker and the title must stay clear of.</summary>
            public double Left;
        }

        private static bool TryGetVersionSlot(GuiElementModCell element, out VersionSlot slot)
        {
            slot = default;

            ElementBounds bounds = element?.Bounds;
            ModCellEntry cell = element?.cell;
            if (bounds?.ParentBounds == null || cell == null || string.IsNullOrEmpty(cell.RightTopText)) return false;

            CairoFont font = cell.DetailTextFont ?? CairoFont.WhiteSmallText();
            double textWidth = font.GetTextExtents(cell.RightTopText).XAdvance;
            double right = VersionRight(element);

            slot.Text = cell.RightTopText;
            slot.Font = font;
            slot.CentreY = TopLineCentreY(bounds);

            if (!HasVersionPicker(cell))
            {
                slot.TextLeft = right - textWidth;
                slot.Left = slot.TextLeft;
                return true;
            }

            double textRight = right - GuiElement.scaled(UnscaledTriggerArrowColumn);
            double x = textRight - textWidth - GuiElement.scaled(UnscaledTriggerPadX);

            // Backstop, not the mechanism: the label is elided to UnscaledMaxVersionWidth before it
            // ever reaches a cell. The floor is the text indent; it yields to keeping the box wide
            // enough to still be a control on a narrow row.
            double narrowest = GuiElement.scaled(UnscaledTriggerArrowColumn + UnscaledTriggerPadX + 12.0);
            double leftLimit = Math.Min(TextIndent(element), right - narrowest);
            x = Math.Max(x, leftLimit);

            double padY = GuiElement.scaled(UnscaledTriggerPadY);
            double height = InkHeight(font) + 2.0 * padY;

            slot.HasBox = true;
            slot.BoxX = x;
            slot.BoxWidth = right - x;
            slot.BoxHeight = height;
            slot.BoxY = slot.CentreY - height / 2.0;
            slot.TextLeft = textRight - textWidth;
            slot.Left = x;

            return slot.BoxWidth > 4.0 && slot.BoxHeight > 4.0;
        }

        /// <summary>
        /// The version note (<see cref="ModCard.VersionNote"/>) as it is drawn: its text, font (the
        /// version's, in <c>GuiStyle.WarningTextColor</c>) and left edge, surface-local, ending at
        /// <see cref="ContentRight"/>. False when the row has no note, no version for it to follow, or
        /// too little room for it (<see cref="UnscaledMinTopLineRoom"/>). Pure geometry, so the draw
        /// and every hit test that shifts with it (the picker trigger above all) agree.
        /// </summary>
        private static bool TryGetVersionNote(GuiElementModCell element, out string text, out CairoFont font, out double left)
        {
            text = null;
            font = null;
            left = 0.0;

            ModCellEntry cell = element?.cell;
            if (element?.Bounds == null || cell == null || string.IsNullOrEmpty(cell.RightTopText)) return false;

            string note = ModCard.VersionNote(cell);
            if (string.IsNullOrEmpty(note)) return false;

            CairoFont noteFont = (cell.DetailTextFont ?? CairoFont.WhiteSmallText()).Clone();
            if (GuiStyle.WarningTextColor != null) noteFont.WithColor(GuiStyle.WarningTextColor);

            double right = ContentRight(element.Bounds);
            double width = noteFont.GetTextExtents(note).XAdvance;
            double reserve = width + GuiElement.scaled(UnscaledVersionNoteGap);
            if (right - TextIndent(element) < reserve + GuiElement.scaled(UnscaledMinTopLineRoom)) return false;

            text = note;
            font = noteFont;
            left = right - width;
            return true;
        }

        /// <summary>
        /// Where the version label (or its trigger box) ends: <see cref="ContentRight"/>, less the
        /// version note and its gap when the row shows one.
        /// </summary>
        private static double VersionRight(GuiElementModCell element)
        {
            double right = ContentRight(element.Bounds);
            if (TryGetVersionNote(element, out _, out _, out double noteLeft)) right = noteLeft - GuiElement.scaled(UnscaledVersionNoteGap);
            return right;
        }

        /// <summary>
        /// The picker trigger in absolute screen pixels - the same rectangle the card paints, so the
        /// affordance and the hit target are one thing. False for a row that has no picker.
        /// </summary>
        public static bool TryGetVersionZone(GuiElementModCell element, out double x, out double y, out double width, out double height)
        {
            x = 0.0;
            y = 0.0;
            width = 0.0;
            height = 0.0;

            try
            {
                if (!TryGetVersionSlot(element, out VersionSlot slot) || !slot.HasBox) return false;

                x = element.Bounds.absX + slot.BoxX;
                y = element.Bounds.absY + slot.BoxY;
                width = slot.BoxWidth;
                height = slot.BoxHeight;
                return true;
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not locate a row's version area: " + e.Message);
                return false;
            }
        }

        /// <summary>True if the given screen position is over the row's version trigger. Cannot throw.</summary>
        public static bool IsInVersionZone(GuiElementModCell element, int mouseX, int mouseY)
        {
            if (!TryGetVersionZone(element, out double x, out double y, out double width, out double height)) return false;

            return mouseX >= x && mouseX <= x + width && mouseY >= y && mouseY <= y + height;
        }

        /// <summary>
        /// Right edge of the title: clear of the update marker, or of the version slot, or - for a row
        /// with neither - of the click split.
        /// </summary>
        private static double TitleRight(GuiElementModCell element)
        {
            double right = ContentRight(element.Bounds);
            if (TryGetVersionSlot(element, out VersionSlot slot)) right = slot.Left;
            if (ModCard.HasUpdateMarker(element.cell)) right -= GuiElement.scaled(UnscaledMarkerGap + UnscaledMarkerWidth);

            return right - GuiElement.scaled(UnscaledTitleGap);
        }

        // ---------------------------------------------------------------- card drawing

        /// <summary>
        /// The card's title: one line, elided to end clear of the top line's right-hand group,
        /// centred on the top line. Reached from <c>ModIconGate.DrawTitle</c>, i.e. in place of
        /// vanilla's wrapping title draw. Returns the line height, which is what vanilla stored.
        /// </summary>
        internal static double DrawCardTitle(GuiElementModCell element, Context ctx, CairoFont font, string text)
        {
            try
            {
                if (ctx == null || element?.Bounds == null) return 0.0;

                CairoFont titleFont = font ?? CairoFont.WhiteSmallishText();
                double x = TextIndent(element);
                string line = Elide(OneLine(text), titleFont, TitleRight(element) - x);

                ctx.Save();
                try
                {
                    DrawLineCentred(ctx, titleFont, line, x, TopLineCentreY(element.Bounds));
                }
                finally
                {
                    ctx.Restore();
                }

                return titleFont.GetFontExtents().Height;
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not draw a mod card's title: " + e.Message);
                return 0.0;
            }
        }

        /// <summary>
        /// Everything on the card below and right of the title: the status line, the version slot, the
        /// update marker and the button row. Reached from <c>ModIconGate.DrawDetail</c>, i.e. in place
        /// of vanilla's wrapping detail-text draw - which, unlike the version draw, runs for EVERY row,
        /// error rows included. Each part swallows its own faults.
        /// </summary>
        internal static void DrawCardBody(GuiElementModCell element, Context ctx)
        {
            if (ctx == null || element?.Bounds == null || element.cell == null) return;

            bool hasSlot = false;
            VersionSlot slot = default;

            try
            {
                hasSlot = TryGetVersionSlot(element, out slot);
                if (hasSlot) DrawVersionSlot(element, ctx, slot);
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not draw a mod card's version: " + e.Message);
            }

            try
            {
                if (hasSlot && TryGetVersionNote(element, out string note, out CairoFont noteFont, out double noteLeft))
                {
                    ctx.Save();
                    try
                    {
                        DrawLineCentred(ctx, noteFont, note, noteLeft, slot.CentreY);
                    }
                    finally
                    {
                        ctx.Restore();
                    }
                }
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not draw a mod card's version note: " + e.Message);
            }

            try
            {
                if (ModCard.HasUpdateMarker(element.cell))
                {
                    double right = (hasSlot ? slot.Left : ContentRight(element.Bounds)) - GuiElement.scaled(UnscaledMarkerGap);
                    DrawUpdateMarker(ctx, right, TopLineCentreY(element.Bounds));
                }
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not draw a mod card's update marker: " + e.Message);
            }

            double buttonTop = ModCard.DrawButtons(element, ctx);

            try
            {
                DrawStatusLine(element, ctx, buttonTop);
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not draw a mod card's status line: " + e.Message);
            }
        }

        /// <summary>
        /// The row's <c>DetailText</c> as one elided line, centred between the top line and the button
        /// row. Both surfaces clear it for a healthy mod; what is left is a load error, drawn in the
        /// error colour (vanilla's 50% black wash over a failed row comes later in <c>Compose</c>).
        /// </summary>
        private static void DrawStatusLine(GuiElementModCell element, Context ctx, double buttonTop)
        {
            ModCellEntry cell = element.cell;
            string text = OneLine(cell.DetailText);
            if (string.IsNullOrEmpty(text)) return;

            CairoFont font = (cell.DetailTextFont ?? CairoFont.WhiteSmallText()).Clone();
            bool failed = cell.Mod == null || cell.Mod.Info == null || cell.Mod.Error.HasValue;
            if (failed) font.WithColor(GuiStyle.ErrorTextColor);

            double x = TextIndent(element);
            double width = ContentRight(element.Bounds) - x;
            string line = Elide(text, font, width);

            double top = TopLineBottom(element.Bounds);
            double bottom = buttonTop > top ? buttonTop : element.Bounds.OuterHeight - element.Bounds.absPaddingY;

            ctx.Save();
            try
            {
                DrawLineCentred(ctx, font, line, x, (top + bottom) / 2.0);
            }
            finally
            {
                ctx.Restore();
            }
        }

        /// <summary>
        /// The version label, and on a picker row the dropdown trigger around it.
        /// <para>
        /// The box is the engine's own closed-combobox recipe: a <c>rgba(0,0,0,0.2)</c> well with a
        /// radius-3 corner and an inverse emboss - exactly what <c>GuiElementDropDown.ComposeElements</c>
        /// paints, and what this row paints for its on/off box, so the trigger reads as a sibling of
        /// the switch it is now level with. The arrow is the dropdown's own filled triangle in
        /// <c>rgba(1,1,1,0.6)</c>, a path rather than a glyph (no font fallback). The separator is a
        /// one-pixel engraved rule in the emboss's two tones.
        /// </para>
        /// </summary>
        private static void DrawVersionSlot(GuiElementModCell element, Context ctx, VersionSlot slot)
        {
            ctx.Save();

            try
            {
                if (slot.HasBox)
                {
                    double x = slot.BoxX;
                    double y = slot.BoxY;
                    double width = slot.BoxWidth;
                    double height = slot.BoxHeight;

                    ctx.SetSourceRGBA(0.0, 0.0, 0.0, 0.2);
                    GuiElement.RoundRectangle(ctx, x, y, width, height, 3.0);
                    ctx.Fill();

                    element.EmbossRoundRectangleElement(ctx, x, y, width, height, inverse: true, (int)GuiElement.scaled(2.0), 2);

                    double arrowRight = x + width - GuiElement.scaled(UnscaledTriggerPadX);
                    double arrowLeft = arrowRight - GuiElement.scaled(UnscaledTriggerArrowWidth);
                    double arrowHeight = GuiElement.scaled(UnscaledTriggerArrowHeight);
                    double arrowTop = y + (height - arrowHeight) / 2.0;

                    DrawTriggerDivider(ctx, arrowLeft - GuiElement.scaled(UnscaledTriggerDividerGap), y, height);

                    ctx.NewPath();
                    ctx.LineTo(arrowLeft, arrowTop);
                    ctx.LineTo(arrowRight, arrowTop);
                    ctx.LineTo((arrowLeft + arrowRight) / 2.0, arrowTop + arrowHeight);
                    ctx.ClosePath();
                    ctx.SetSourceRGBA(1.0, 1.0, 1.0, 0.6);
                    ctx.Fill();
                }

                DrawLineCentred(ctx, slot.Font, slot.Text, slot.TextLeft, slot.CentreY);
            }
            finally
            {
                ctx.Restore();
            }
        }

        /// <summary>The engraved rule between the version text and the arrow.</summary>
        private static void DrawTriggerDivider(Context ctx, double right, double boxTop, double boxHeight)
        {
            double inset = GuiElement.scaled(UnscaledTriggerPadY);
            double top = boxTop + inset;
            double bottom = boxTop + boxHeight - inset;
            if (bottom - top < 2.0) return;

            // Half-pixel offsets so a 1px stroke lands on one pixel column instead of straddling two.
            double dark = Math.Floor(right - GuiElement.scaled(UnscaledTriggerDividerWidth)) + 0.5;

            ctx.LineWidth = 1.0;
            ctx.Antialias = Antialias.None;

            ctx.NewPath();
            ctx.MoveTo(dark, top);
            ctx.LineTo(dark, bottom);
            ctx.SetSourceRGBA(0.0, 0.0, 0.0, 0.4);
            ctx.Stroke();

            ctx.NewPath();
            ctx.MoveTo(dark + 1.0, top);
            ctx.LineTo(dark + 1.0, bottom);
            ctx.SetSourceRGBA(1.0, 1.0, 1.0, 0.15);
            ctx.Stroke();

            ctx.Antialias = Antialias.Default;
        }

        /// <summary>
        /// The "update available" marker: a warning triangle with an exclamation mark, drawn as
        /// paths (Cairo's toy font API has no glyph fallback, so no U+26A0). Filled with
        /// <c>GuiStyle.WarningTextColor</c> - the game's own amber/yellow (#F2CA83), the colour vanilla
        /// uses for warning text - with the "!" cut out in the dialog's darkest brown
        /// (<c>GuiStyle.DialogStrongBgColor</c>) so it reads on the opaque row background.
        /// </summary>
        private static void DrawUpdateMarker(Context ctx, double right, double centreY)
        {
            double width = GuiElement.scaled(UnscaledMarkerWidth);
            double height = GuiElement.scaled(UnscaledMarkerHeight);
            double left = right - width;
            double top = centreY - height / 2.0;

            double[] fill = GuiStyle.WarningTextColor ?? new[] { 0.95, 0.79, 0.51, 1.0 };
            double[] ink = GuiStyle.DialogStrongBgColor ?? new[] { 0.25, 0.21, 0.16, 1.0 };

            ctx.Save();
            try
            {
                ctx.Antialias = Antialias.Default;
                ctx.LineJoin = LineJoin.Round;
                ctx.LineWidth = Math.Max(1.0, GuiElement.scaled(1.5));

                ctx.NewPath();
                ctx.MoveTo(left + width / 2.0, top);
                ctx.LineTo(left + width, top + height);
                ctx.LineTo(left, top + height);
                ctx.ClosePath();
                ctx.SetSourceRGBA(fill[0], fill[1], fill[2], 1.0);
                ctx.FillPreserve();

                // The rounded join softens the three points the way the engine's own SVG icons are.
                ctx.Stroke();

                double barWidth = Math.Max(1.5, width * 0.14);
                double cx = left + width / 2.0;

                ctx.SetSourceRGBA(ink[0], ink[1], ink[2], 1.0);
                ctx.Rectangle(cx - barWidth / 2.0, top + height * 0.34, barWidth, height * 0.34);
                ctx.Fill();

                ctx.NewPath();
                ctx.Arc(cx, top + height * 0.83, barWidth * 0.62, 0.0, 2.0 * Math.PI);
                ctx.Fill();
            }
            finally
            {
                ctx.Restore();
            }
        }

        /// <summary>First line of <paramref name="text"/>, trimmed; the card never wraps.</summary>
        private static string OneLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            int newline = text.IndexOfAny(new[] { '\r', '\n' });
            return (newline >= 0 ? text.Substring(0, newline) : text).Trim();
        }
    }
}
