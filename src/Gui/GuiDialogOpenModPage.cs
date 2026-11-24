using System;
using BetterMods.Updates;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace BetterMods.Gui
{
    /// <summary>
    /// The in-game first-click question for a card's "Open mod page" on a host the player has not
    /// trusted yet: Cancel / Always trust &lt;host&gt; / Open. BetterMods' own because the public
    /// <c>GuiDialogConfirm</c> has exactly two buttons.
    /// <para>
    /// Built like <c>GuiDialogConfirm</c> (shaded background, title bar, small buttons) and at its draw
    /// order, 2.0 - above the Mod Profiles dialog (0.25) and everything ordinary (NOTES, "In-game dialog
    /// stacking"). Opened off the main-thread queue, never from inside a mouse handler. Every answer
    /// re-validates the address in <see cref="ModPageLinks"/> before anything is opened or stored;
    /// closing the dialog any other way (Escape, the title bar) is Cancel. ASCII only.
    /// </para>
    /// </summary>
    public class GuiDialogOpenModPage : GuiDialog
    {
        /// <summary>Text column width, unscaled - <c>GuiDialogConfirm</c>'s; widened if the buttons need more.</summary>
        private const double TextWidth = 500.0;

        /// <summary>Horizontal padding of every button, unscaled.</summary>
        private const double ButtonPadX = 6.0;

        /// <summary>Clear space between two buttons, unscaled.</summary>
        private const double ButtonSpacing = 12.0;

        private readonly string url;
        private readonly string host;
        private bool answered;

        public override double DrawOrder => 2.0;

        public override string ToggleKeyCombinationCode => null;

        public override bool PrefersUngrabbedMouse => true;

        public GuiDialogOpenModPage(ICoreClientAPI capi, string url, string host)
            : base(capi)
        {
            this.url = url ?? "";
            this.host = host ?? "";
        }

        private void Compose()
        {
            string text = "Open this mod page in your browser?\n\n" + url +
                          "\n\nAlways trust " + host + " opens mod pages on " + host +
                          " without asking from now on. Remove it from TrustedLinkHosts in bettermods.json to undo.";

            CairoFont font = CairoFont.WhiteSmallText();
            double scale = RuntimeEnv.GUIScale;
            if (!(scale > 0.01)) scale = 1.0;

            string trustLabel = "Always trust " + host;
            double openWidth = SmallButtonWidth("Open", ButtonPadX);
            double rowWidth = SmallButtonWidth("Cancel", ButtonPadX) + ButtonSpacing + SmallButtonWidth(trustLabel, ButtonPadX) + ButtonSpacing + openWidth;
            double width = Math.Max(TextWidth, rowWidth);

            double textHeight = capi.Gui.Text.GetMultilineTextHeight(font, text, GuiElement.scaled(width)) / scale;

            ElementBounds textBounds = ElementBounds.Fixed(0.0, GuiStyle.TitleBarHeight, width, textHeight);
            ElementBounds buttonBounds = ElementBounds.Fixed(0.0, 0.0, 0.0, 0.0)
                .WithFixedPadding(ButtonPadX, 6.0)
                .FixedUnder(textBounds, 20.0);

            ElementBounds background = ElementStdBounds.DialogBackground()
                .WithFixedPadding(GuiStyle.ElementToDialogPadding, GuiStyle.ElementToDialogPadding);

            SingleComposer = capi.Gui.CreateCompo("bettermods-openmodpage", ElementStdBounds.AutosizedMainDialog)
                .AddShadedDialogBG(background)
                .AddDialogTitleBar("Open mod page", () => TryClose())
                .BeginChildElements(background)
                .AddStaticText(text, font, textBounds)
                .AddSmallButton("Cancel", OnCancel, buttonBounds.FlatCopy().WithAlignment(EnumDialogArea.LeftFixed))
                .AddSmallButton(trustLabel, OnTrust,
                    buttonBounds.FlatCopy().WithAlignment(EnumDialogArea.RightFixed).WithFixedAlignmentOffset(-(openWidth + ButtonSpacing), 0.0))
                .AddSmallButton("Open", OnOpen, buttonBounds.FlatCopy().WithAlignment(EnumDialogArea.RightFixed))
                .EndChildElements()
                .Compose();
        }

        /// <summary>
        /// A small button's outer width, unscaled, the way <c>GuiElementTextButton</c> sizes itself
        /// (<c>CairoFont.AutoBoxSize</c>: ink width / GUIScale + 1) plus its padding on both sides.
        /// </summary>
        private static double SmallButtonWidth(string text, double padX)
        {
            try
            {
                double scale = RuntimeEnv.GUIScale;
                if (!(scale > 0.01)) scale = 1.0;

                return CairoFont.SmallButtonText().GetTextExtents(text).Width / scale + 1.0 + 2.0 * padX;
            }
            catch (Exception)
            {
                return 8.0 * text.Length + 2.0 * padX;
            }
        }

        public override void OnGuiOpened()
        {
            try
            {
                Compose();
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Error("[BetterMods] Could not build the open-mod-page question.");
                BetterModsModSystem.Logger?.Error(e);
            }

            base.OnGuiOpened();
        }

        private bool OnCancel()
        {
            answered = true;
            TryClose();
            return true;
        }

        private bool OnOpen()
        {
            Answer(() => ModPageLinks.OpenOnce(url));
            return true;
        }

        private bool OnTrust()
        {
            Answer(() => ModPageLinks.TrustAndOpen(url, host));
            return true;
        }

        /// <summary>Exactly once; the dialog closes first, then the address is re-validated and opened.</summary>
        private void Answer(Func<bool> open)
        {
            if (answered) return;
            answered = true;

            TryClose();

            try
            {
                if (!open()) BetterModsModSystem.Logger?.Warning("[BetterMods] The mod page was not opened: " + url);
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Error("[BetterMods] Opening a mod page failed.");
                BetterModsModSystem.Logger?.Error(e);
            }
        }
    }
}
