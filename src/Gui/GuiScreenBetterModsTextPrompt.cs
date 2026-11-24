using System;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.Client;

namespace BetterMods.Gui
{
    /// <summary>
    /// A main-menu modal asking for a name and an optional description - the sibling of
    /// <see cref="GuiScreenBetterModsConfirm"/>, with the same rules: sized from the framebuffer and
    /// <c>RuntimeEnv.GUIScale</c>, sidebar hidden (so it cannot navigate away mid-question) and the
    /// screen re-centred on the full window, <see cref="ShouldDisposePreviousScreen"/> false so the
    /// screen underneath survives. Escape is Cancel, Enter is the confirm button. The answer is
    /// reported exactly once; the caller decides where to go next.
    /// <para>
    /// The confirm button runs <c>validate</c> first; a non-null message is shown under the fields
    /// and the prompt stays open. What is handed back is ASCII-cleaned and length-capped (Cairo's toy
    /// font API does no glyph fallback).
    /// </para>
    /// </summary>
    public class GuiScreenBetterModsTextPrompt : GuiScreen
    {
        private const string CancelButtonKey = "bettermods-prompt-cancel";
        private const string ConfirmButtonKey = "bettermods-prompt-ok";
        private const string NameKey = "bettermods-prompt-name";
        private const string DescriptionKey = "bettermods-prompt-description";
        private const string ErrorKey = "bettermods-prompt-error";

        private const double PreferredDialogWidth = 480.0;
        private const double MinDialogWidth = 260.0;
        private const double TitleHeight = 34.0;
        private const double LabelHeight = 24.0;
        private const double InputHeight = 30.0;
        private const double FieldGap = 10.0;
        private const double ErrorHeight = 22.0;
        private const double ButtonGap = 12.0;
        private const double ButtonRowHeight = 40.0;

        private readonly Func<string, string> validate;
        private readonly Action<bool, string, string> onAnswer;
        private readonly int nameMax;
        private readonly int descriptionMax;
        private readonly bool hasDescription;
        private bool answered;

        public override bool ShouldDisposePreviousScreen => false;

        /// <param name="introText">Plain text under the title; may be null.</param>
        /// <param name="descriptionLabel">Null for a name-only prompt.</param>
        /// <param name="validate">Name in, error message out (null = fine). May be null.</param>
        /// <param name="onAnswer">(confirmed, name, description).</param>
        public GuiScreenBetterModsTextPrompt(
            string title,
            string introText,
            string nameLabel,
            string initialName,
            int nameMaxLength,
            string descriptionLabel,
            string initialDescription,
            int descriptionMaxLength,
            string confirmLabel,
            Func<string, string> validate,
            Action<bool, string, string> onAnswer,
            ScreenManager screenManager,
            GuiScreen parentScreen)
            : base(screenManager, parentScreen)
        {
            this.validate = validate;
            this.onAnswer = onAnswer;
            nameMax = Math.Max(1, nameMaxLength);
            descriptionMax = Math.Max(1, descriptionMaxLength);
            hasDescription = !string.IsNullOrEmpty(descriptionLabel);

            ShowMainMenu = false;

            int windowWidth = screenManager.GamePlatform.FramebufferSize.Width;
            double guiScale = RuntimeEnv.GUIScale;
            if (!(guiScale > 0.01)) guiScale = 1.0;

            double maxDialogWidth = windowWidth / guiScale
                - 2.0 * GuiStyle.ElementToDialogPadding
                - 2.0 * MenuLayout.UnscaledWindowMargin;
            double dialogWidth = Math.Max(MinDialogWidth, Math.Min(PreferredDialogWidth, maxDialogWidth));

            string intro = Ascii(introText, 400);
            CairoFont introFont = CairoFont.WhiteDetailText();
            double introHeight = 0.0;
            if (intro.Length > 0)
            {
                introHeight = screenManager.api.Gui.Text.GetMultilineTextHeight(introFont, intro, GuiElement.scaled(dialogWidth)) / guiScale + 6.0;
            }

            ElementBounds titleBounds = ElementBounds.Fixed(0.0, 0.0, dialogWidth, TitleHeight);
            ElementBounds introBounds = ElementBounds.Fixed(0.0, TitleHeight, dialogWidth, introHeight);
            double y = TitleHeight + introHeight + (introHeight > 0 ? 4.0 : 0.0);

            ElementBounds nameLabelBounds = ElementBounds.Fixed(0.0, y, dialogWidth, LabelHeight);
            y += LabelHeight;
            ElementBounds nameBounds = ElementBounds.Fixed(0.0, y, dialogWidth, InputHeight);
            y += InputHeight + FieldGap;

            ElementBounds descriptionLabelBounds = null;
            ElementBounds descriptionBounds = null;
            if (hasDescription)
            {
                descriptionLabelBounds = ElementBounds.Fixed(0.0, y, dialogWidth, LabelHeight);
                y += LabelHeight;
                descriptionBounds = ElementBounds.Fixed(0.0, y, dialogWidth, InputHeight);
                y += InputHeight + 4.0;
            }

            ElementBounds errorBounds = ElementBounds.Fixed(0.0, y, dialogWidth, ErrorHeight);
            y += ErrorHeight + ButtonGap;

            ElementBounds buttonBounds = ElementBounds.Fixed(0.0, y, 0.0, 0.0).WithFixedPadding(10.0, 2.0);
            double contentHeight = y + ButtonRowHeight;

            CairoFont labelFont = CairoFont.WhiteSmallText();

            GuiComposer composer = dialogBase("mainmenu-bettermods-prompt", dialogWidth, contentHeight)
                .AddStaticText(Ascii(title, 80), CairoFont.WhiteSmallishText().WithWeight(FontWeight.Bold), titleBounds);

            if (intro.Length > 0) composer.AddStaticText(intro, introFont, introBounds);

            composer
                .AddStaticText(Ascii(nameLabel, 80), labelFont, nameLabelBounds)
                .AddTextInput(nameBounds, null, CairoFont.TextInput(), NameKey);

            if (hasDescription)
            {
                composer
                    .AddStaticText(Ascii(descriptionLabel, 80), CairoFont.WhiteSmallText(), descriptionLabelBounds)
                    .AddTextInput(descriptionBounds, null, CairoFont.TextInput(), DescriptionKey);
            }

            composer
                .AddDynamicText("", CairoFont.WhiteDetailText().WithColor(GuiStyle.ErrorTextColor), errorBounds, ErrorKey)
                .AddButton("Cancel", OnCancel, buttonBounds, EnumButtonStyle.Normal, CancelButtonKey)
                .AddButton(string.IsNullOrEmpty(confirmLabel) ? "OK" : Ascii(confirmLabel, 40), OnConfirm,
                    buttonBounds.FlatCopy().WithAlignment(EnumDialogArea.RightFixed), EnumButtonStyle.Normal, ConfirmButtonKey);

            composer.OnComposed += () => MenuLayout.Centre(this, composer);

            // Compose() focuses the first focusable element: the name field.
            ElementComposer = composer.EndChildElements().Compose();

            try
            {
                GuiElementTextInput name = ElementComposer.GetTextInput(NameKey);
                name.SetMaxLength(nameMax);
                name.SetValue(Ascii(initialName, nameMax));

                if (hasDescription)
                {
                    GuiElementTextInput description = ElementComposer.GetTextInput(DescriptionKey);
                    description.SetMaxLength(descriptionMax);
                    description.SetValue(Ascii(initialDescription, descriptionMax));
                }
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not prefill the prompt: " + e.Message);
            }
        }

        private static string Ascii(string text, int max)
        {
            return Updates.ModUpdateRules.AsciiLine(text ?? "", max);
        }

        /// <summary>Enter confirms. The text field leaves Enter unhandled, so it reaches us here.</summary>
        public override void OnKeyDown(KeyEvent e)
        {
            if (!answered && (e.KeyCode == (int)GlKeys.Enter || e.KeyCode == (int)GlKeys.KeypadEnter))
            {
                e.Handled = true;
                OnConfirm();
                return;
            }

            base.OnKeyDown(e);
        }

        /// <summary>Escape is Cancel.</summary>
        public override bool OnBackPressed()
        {
            Answer(false);
            return true;
        }

        private bool OnCancel()
        {
            Answer(false);
            return true;
        }

        private bool OnConfirm()
        {
            if (answered) return true;

            string name = Ascii(ElementComposer?.GetTextInput(NameKey)?.GetText(), nameMax);

            string message = null;
            try
            {
                message = validate?.Invoke(name);
            }
            catch (Exception e)
            {
                message = "Could not check that name.";
                BetterModsModSystem.Logger?.Warning("[BetterMods] Validating a prompt answer failed: " + e.Message);
            }

            if (!string.IsNullOrEmpty(message))
            {
                ElementComposer?.GetDynamicText(ErrorKey)?.SetNewText(Ascii(message, 120));
                return true;
            }

            Answer(true);
            return true;
        }

        /// <summary>Exactly once, buttons disabled first.</summary>
        private void Answer(bool confirmed)
        {
            if (answered) return;
            answered = true;

            string name = "";
            string description = "";

            try
            {
                name = Ascii(ElementComposer?.GetTextInput(NameKey)?.GetText(), nameMax);
                if (hasDescription) description = Ascii(ElementComposer?.GetTextInput(DescriptionKey)?.GetText(), descriptionMax);

                if (ElementComposer?.GetButton(CancelButtonKey) is GuiElementTextButton cancel) cancel.Enabled = false;
                if (ElementComposer?.GetButton(ConfirmButtonKey) is GuiElementTextButton confirm) confirm.Enabled = false;
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not read the prompt: " + e.Message);
            }

            try
            {
                onAnswer?.Invoke(confirmed, name, description);
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Error("[BetterMods] Handling a prompt answer failed.");
                BetterModsModSystem.Logger?.Error(e);
            }
        }
    }
}
