using System;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.Client;

namespace BetterMods.Gui
{
    /// <summary>The answer to a <see cref="GuiScreenBetterModsConfirm"/>.</summary>
    public enum BetterModsConfirmChoice
    {
        /// <summary>Cancel, or Escape.</summary>
        Cancel,

        /// <summary>The confirm button (rightmost).</summary>
        Confirm,

        /// <summary>The optional extra button, between Cancel and Confirm.</summary>
        Extra
    }

    /// <summary>
    /// A main-menu yes/no question with a scrolling VTML body - BetterMods' stand-in for vanilla's
    /// <c>GuiScreenConfirmAction</c>, which is <c>internal</c> (NOTES, "Mods screen").
    /// <para>
    /// Same shape and the same layout rules as <see cref="GuiScreenProfileMismatch"/>: sized from the
    /// framebuffer and <c>RuntimeEnv.GUIScale</c>, body scrolls instead of the dialog outgrowing the
    /// window, sidebar hidden so it cannot navigate away mid-question (and the screen re-centres itself
    /// on the full window), <see cref="ShouldDisposePreviousScreen"/> false so the screen underneath
    /// survives. Escape is Cancel. The answer is reported exactly once; the caller decides where to go
    /// next (usually <c>LoadScreen</c> back to the parent).
    /// </para>
    /// <para>
    /// An optional third button sits left of the confirm button (the mod page link's "Always trust
    /// &lt;host&gt;"); the dialog widens, within the window, until all three fit on one row.
    /// </para>
    /// <para>ASCII only - Cairo's toy font API does no glyph fallback.</para>
    /// </summary>
    public class GuiScreenBetterModsConfirm : GuiScreen
    {
        private const string CancelButtonKey = "bettermods-confirm-cancel";
        private const string ConfirmButtonKey = "bettermods-confirm-ok";
        private const string ExtraButtonKey = "bettermods-confirm-extra";
        private const string BodyKey = "bettermods-confirm-body";
        private const string ScrollbarKey = "scrollbar";

        private const double TitleHeight = 34.0;
        private const double TitleGap = 10.0;
        private const double ButtonGap = 14.0;
        private const double ButtonRowHeight = 40.0;
        private const double ScrollbarGutter = 40.0;
        private const double MinBodyHeight = 60.0;
        private const double MinDialogWidth = 200.0 + ScrollbarGutter;

        /// <summary>Narrower than the mismatch prompt: this is a short question, not a report.</summary>
        private const double PreferredDialogWidth = 560.0;

        /// <summary>Horizontal padding of every button, unscaled (what the bounds below set).</summary>
        private const double ButtonPadX = 10.0;

        /// <summary>Clear space between two buttons in the row, unscaled.</summary>
        private const double ButtonSpacing = 12.0;

        private readonly Action<BetterModsConfirmChoice> onChoice;
        private bool answered;

        public override bool ShouldDisposePreviousScreen => false;

        public GuiScreenBetterModsConfirm(
            string title,
            string bodyVtml,
            string confirmLabel,
            Action<bool> onAnswer,
            ScreenManager screenManager,
            GuiScreen parentScreen)
            : this(title, bodyVtml, confirmLabel, null, choice => onAnswer?.Invoke(choice == BetterModsConfirmChoice.Confirm), screenManager, parentScreen)
        {
        }

        /// <summary>
        /// A three-way question: Cancel, <paramref name="extraLabel"/> and <paramref name="confirmLabel"/>,
        /// left to right. A null or empty <paramref name="extraLabel"/> is the two-button form.
        /// </summary>
        public GuiScreenBetterModsConfirm(
            string title,
            string bodyVtml,
            string confirmLabel,
            string extraLabel,
            Action<BetterModsConfirmChoice> onChoice,
            ScreenManager screenManager,
            GuiScreen parentScreen)
            : base(screenManager, parentScreen)
        {
            this.onChoice = onChoice;
            string confirmText = string.IsNullOrEmpty(confirmLabel) ? "Confirm" : confirmLabel;
            string extraText = string.IsNullOrEmpty(extraLabel) ? null : extraLabel;

            // A modal question; see GuiScreenProfileMismatch for why the sidebar is hidden.
            ShowMainMenu = false;

            int windowWidth = screenManager.GamePlatform.WindowSize.Width;
            int windowHeight = screenManager.GamePlatform.WindowSize.Height;

            double guiScale = RuntimeEnv.GUIScale;
            if (!(guiScale > 0.01)) guiScale = 1.0;

            double maxDialogWidth = windowWidth / guiScale
                - 2.0 * GuiStyle.ElementToDialogPadding
                - 2.0 * MenuLayout.UnscaledWindowMargin;
            // Wide enough for the button row (sized the way the buttons size themselves) when the
            // window allows; for that row the preferred width is a floor, not a cap.
            double confirmWidth = ButtonWidth(confirmText);
            double rowWidth = ButtonWidth("Cancel") + ButtonSpacing + confirmWidth;
            if (extraText != null) rowWidth += ButtonSpacing + ButtonWidth(extraText);

            double dialogWidth = Math.Max(MinDialogWidth, Math.Min(Math.Max(PreferredDialogWidth, rowWidth), maxDialogWidth));
            double bodyWidth = Math.Max(200.0, dialogWidth - ScrollbarGutter);

            double maxContentHeight = Math.Max(300.0, windowHeight) / guiScale - 120.0;
            double maxBodyHeight = Math.Max(MinBodyHeight, maxContentHeight - (TitleHeight + TitleGap + ButtonGap + ButtonRowHeight));

            string body = string.IsNullOrEmpty(bodyVtml) ? "" : bodyVtml;
            CairoFont font = CairoFont.WhiteSmallText().WithFontSize(17f).WithLineHeightMultiplier(1.25);
            double textHeight = screenManager.api.Gui.Text.GetMultilineTextHeight(font, body, GuiElement.scaled(bodyWidth)) / guiScale;
            double bodyHeight = Math.Min(textHeight + 10.0, maxBodyHeight);

            ElementBounds titleBounds = ElementBounds.Fixed(EnumDialogArea.LeftTop, 0.0, 0.0, bodyWidth, TitleHeight);

            // The credits-screen idiom (see GuiScreenProfileMismatch): the clip is a sibling copy taken
            // before compose, the richtext grows its own bounds to the real height.
            ElementBounds bodyBounds = ElementBounds.Fixed(0.0, 0.0, bodyWidth, bodyHeight);
            ElementBounds insetBounds = bodyBounds.ForkBoundingParent(3.0, 3.0, 3.0, 3.0).FixedUnder(titleBounds, TitleGap);
            ElementBounds clipBounds = bodyBounds.CopyOffsetedSibling();

            ElementBounds buttonBounds = ElementBounds.Fixed(0.0, 0.0, 0.0, 0.0)
                .WithFixedPadding(ButtonPadX, 2.0)
                .FixedUnder(insetBounds, ButtonGap);

            double contentHeight = TitleHeight + TitleGap + insetBounds.fixedHeight + ButtonGap + ButtonRowHeight;

            GuiComposer composer = dialogBase("mainmenu-bettermods-confirm", dialogWidth, contentHeight)
                .AddStaticText(SafeTitle(title), CairoFont.WhiteSmallishText().WithWeight(FontWeight.Bold), titleBounds)
                .AddInset(insetBounds)
                .AddVerticalScrollbar(OnNewScrollbarValue, ElementStdBounds.VerticalScrollbar(insetBounds), ScrollbarKey)
                .BeginClip(clipBounds)
                .AddRichtext(body, font, bodyBounds, BodyKey)
                .EndClip()
                .AddButton("Cancel", OnCancel, buttonBounds, EnumButtonStyle.Normal, CancelButtonKey)
                .AddButton(confirmText, OnConfirm,
                    buttonBounds.FlatCopy().WithAlignment(EnumDialogArea.RightFixed), EnumButtonStyle.Normal, ConfirmButtonKey);

            if (extraText != null)
            {
                composer.AddButton(extraText, OnExtra,
                    buttonBounds.FlatCopy().WithAlignment(EnumDialogArea.RightFixed).WithFixedAlignmentOffset(-(confirmWidth + ButtonSpacing), 0.0),
                    EnumButtonStyle.Normal, ExtraButtonKey);
            }

            composer.OnComposed += () => MenuLayout.Centre(this, composer);

            ElementComposer = composer.EndChildElements().Compose();

            try
            {
                clipBounds.CalcWorldBounds();
                ElementComposer.GetScrollbar(ScrollbarKey)?.SetHeights((float)clipBounds.fixedHeight, (float)bodyBounds.fixedHeight);
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not size the confirmation's scrollbar: " + e.Message);
            }
        }

        /// <summary>
        /// A button's outer width, unscaled, the way <c>GuiElementTextButton</c> sizes itself
        /// (<c>CairoFont.AutoBoxSize</c>: ink width / GUIScale + 1) plus its padding on both sides.
        /// </summary>
        private static double ButtonWidth(string text)
        {
            try
            {
                double scale = RuntimeEnv.GUIScale;
                if (!(scale > 0.01)) scale = 1.0;

                return CairoFont.ButtonText().GetTextExtents(text ?? "").Width / scale + 1.0 + 2.0 * ButtonPadX;
            }
            catch (Exception)
            {
                return 10.0 * (text?.Length ?? 0) + 2.0 * ButtonPadX;
            }
        }

        private static string SafeTitle(string title)
        {
            return Updates.ModUpdateRules.AsciiLine(string.IsNullOrWhiteSpace(title) ? "Please confirm" : title, 80);
        }

        private void OnNewScrollbarValue(float value)
        {
            try
            {
                ElementBounds bounds = ElementComposer?.GetRichtext(BodyKey)?.Bounds;
                if (bounds == null) return;

                bounds.fixedY = 0.0 - value;
                bounds.CalcWorldBounds();
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not scroll the confirmation: " + e.Message);
            }
        }

        /// <summary>Escape is Cancel.</summary>
        public override bool OnBackPressed()
        {
            Answer(BetterModsConfirmChoice.Cancel);
            return true;
        }

        private bool OnCancel()
        {
            Answer(BetterModsConfirmChoice.Cancel);
            return true;
        }

        private bool OnConfirm()
        {
            Answer(BetterModsConfirmChoice.Confirm);
            return true;
        }

        private bool OnExtra()
        {
            Answer(BetterModsConfirmChoice.Extra);
            return true;
        }

        /// <summary>Exactly once, buttons disabled first (a second click must not install twice).</summary>
        private void Answer(BetterModsConfirmChoice choice)
        {
            if (answered) return;
            answered = true;

            try
            {
                if (ElementComposer?.GetButton(CancelButtonKey) is GuiElementTextButton cancel) cancel.Enabled = false;
                if (ElementComposer?.GetButton(ConfirmButtonKey) is GuiElementTextButton confirm) confirm.Enabled = false;
                if (ElementComposer?.GetButton(ExtraButtonKey) is GuiElementTextButton extra) extra.Enabled = false;
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not disable the confirmation buttons: " + e.Message);
            }

            try
            {
                onChoice?.Invoke(choice);
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Error("[BetterMods] Handling a confirmation answer failed.");
                BetterModsModSystem.Logger?.Error(e);
            }
        }
    }
}
