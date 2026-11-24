using System;
using System.Collections.Generic;
using System.Text;
using BetterMods.Profiles;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace BetterMods.Gui
{
    /// <summary>What the player picked on <see cref="GuiScreenProfileMismatch"/>.</summary>
    public enum ProfileMismatchChoice
    {
        /// <summary>Go back to the world list and start nothing.</summary>
        Cancel,

        /// <summary>Start the world on the active profile, re-binding the world to it.</summary>
        PlayWithActive,

        /// <summary>Apply the suggested profile, then start the world.</summary>
        SwitchToSuggested
    }

    /// <summary>
    /// Everything the prompt needs to say. Built by <see cref="SavegameProfileGate"/>, which owns the
    /// decision about whether there is anything worth saying at all.
    /// </summary>
    public class ProfileMismatchPrompt
    {
        public string WorldName;

        /// <summary>The profile that is about to be used.</summary>
        public string ActiveProfileName;

        /// <summary>Name behind the Switch button, or null when there is nothing to switch to.</summary>
        public string SuggestedProfileName;

        /// <summary>True when the suggestion enables exactly the world's recorded mod list.</summary>
        public bool SuggestedIsExact;

        /// <summary>True when the suggestion is also the profile the world is bound to.</summary>
        public bool SuggestedIsBound;

        /// <summary>How many profiles matched the world's list exactly. 0 when none did.</summary>
        public int ExactMatchCount;

        /// <summary>
        /// The profile the world is bound to, when that is neither the active nor the suggested one.
        /// Null otherwise — the binding is then already covered by the suggestion line.
        /// </summary>
        public string BoundProfileName;

        /// <summary>Mods the world loaded with that will not be enabled, as <c>"modid@version"</c>.</summary>
        public List<string> Missing = new List<string>();

        /// <summary>Same mod, different version. Information only.</summary>
        public List<ModVersionDifference> Differences = new List<ModVersionDifference>();

        /// <summary>How many non-core mods the world's recorded list holds. -1 when there is no record.</summary>
        public int RecordedModCount = -1;

        public bool HasRecord => RecordedModCount >= 0;
    }

    /// <summary>
    /// Asked when the world being started does not line up with the profile that is about to be used —
    /// because the world is bound elsewhere, because mods it loaded with will not be enabled, because
    /// another profile matches it exactly, or any combination of the three.
    /// <para>
    /// One screen, one decision, on purpose. The three conditions overlap constantly (a world bound to
    /// another profile usually IS missing mods, and that other profile usually IS the exact match), so
    /// stacking a prompt per condition would ask the same question up to three times.
    /// </para>
    /// <para>
    /// This is a screen rather than an in-screen popup on purpose. <c>GuiComposer.OnMouseUp</c>
    /// (GuiComposer.cs:432-442) foreaches the whole element table without breaking or checking
    /// <c>Handled</c>, so an anchored popup over the world list would leak its click through, and it
    /// would have to be re-added after every <c>InitGui()</c>. A screen has neither problem.
    /// </para>
    /// <para>
    /// The shape is <c>GuiScreenConfirmAction</c>'s (VintagestoryLib,
    /// Vintagestory.Client/GuiScreenConfirmAction.cs:8-19), which is <c>internal</c> and so cannot be
    /// used directly. <see cref="ShouldDisposePreviousScreen"/> is false so that
    /// <c>ScreenManager.LoadScreen</c> leaves the screen underneath alive — the world list is in
    /// <c>CachedScreens</c> and would survive anyway, but the world-modify screen is not.
    /// </para>
    /// </summary>
    public class GuiScreenProfileMismatch : GuiScreen
    {
        private const string CancelButtonKey = "bettermods-mismatch-cancel";
        private const string PlayButtonKey = "bettermods-mismatch-play";
        private const string SwitchButtonKey = "bettermods-mismatch-switch";
        private const string BodyKey = "bettermods-mismatch-body";
        private const string ScrollbarKey = "scrollbar";

        /// <summary>Longest profile name a button label will carry before it is elided.</summary>
        private const int ButtonNameLimit = 18;

        /// <summary>How many missing mods are listed before the rest are summarised.</summary>
        private const int MissingListLimit = 8;

        /// <summary>How many version differences are listed before the rest are summarised.</summary>
        private const int DifferenceListLimit = 4;

        /// <summary>Height reserved for the headline, unscaled.</summary>
        private const double TitleHeight = 34.0;

        /// <summary>Gap between the headline and the body, unscaled.</summary>
        private const double TitleGap = 10.0;

        /// <summary>Gap between the body and the button row, unscaled.</summary>
        private const double ButtonGap = 14.0;

        /// <summary>Height reserved for the button row, unscaled - the buttons size themselves.</summary>
        private const double ButtonRowHeight = 40.0;

        /// <summary>Width the scrollbar and the inset's own padding need beside the body, unscaled.</summary>
        private const double ScrollbarGutter = 40.0;

        /// <summary>Shortest the body may get before the dialog simply stops shrinking, unscaled.</summary>
        private const double MinBodyHeight = 60.0;

        /// <summary>
        /// Narrowest the dialog is ever made, unscaled: the 200 floor the body already had, plus its
        /// scrollbar gutter. Below this the window is simply too small and the dialog overhangs it.
        /// </summary>
        private const double MinDialogWidth = 200.0 + ScrollbarGutter;

        private readonly Action<ProfileMismatchChoice> onChoice;
        private bool answered;

        public override bool ShouldDisposePreviousScreen => false;

        public GuiScreenProfileMismatch(
            ProfileMismatchPrompt prompt,
            Action<ProfileMismatchChoice> onChoice,
            ScreenManager screenManager,
            GuiScreen parentScreen)
            : base(screenManager, parentScreen)
        {
            this.onChoice = onChoice;

            // A modal decision: the only ways out are its own buttons and Escape. With the sidebar
            // shown, it would be drawn over the prompt wherever the two overlap and would take every
            // click there first (GuiScreen.OnMouseDown/Up/Move), and its buttons and logo would
            // navigate away mid-question. False stops it rendering AND taking input; the background
            // logo then centres itself on the full window, as it does under GuiScreenGetUpdate.
            ShowMainMenu = false;

            string active = Sanitize(prompt.ActiveProfileName);
            string suggested = Sanitize(prompt.SuggestedProfileName);
            bool canSwitch = !string.IsNullOrEmpty(suggested);

            string text = BuildText(prompt, active, suggested, canSwitch);

            // Exactly the inputs dialogBase reads on 1.22: the WINDOW size and the GUI scale
            // (RuntimeEnv.GUIScale, which 1.22 keeps equal to ClientSettings.GUIScale).
            int windowWidth = screenManager.GamePlatform.WindowSize.Width;
            int windowHeight = screenManager.GamePlatform.WindowSize.Height;

            double guiScale = RuntimeEnv.GUIScale;
            if (!(guiScale > 0.01)) guiScale = 1.0;

            // The width dialogBase's own default branch computes (GuiScreen.cs:38-42), so the body is
            // measured against the box it will really be drawn in rather than a fixed 650.
            // Capped so the whole dialog - this width plus dialogBase's 2 * ElementToDialogPadding -
            // fits the window with a margin either side; dialogBase uses an explicit width verbatim,
            // so nothing else would stop it hanging off both edges on a narrow window.
            double maxDialogWidth = windowWidth / guiScale
                - 2.0 * GuiStyle.ElementToDialogPadding
                - 2.0 * MenuLayout.UnscaledWindowMargin;
            double dialogWidth = Math.Max(
                MinDialogWidth,
                Math.Min(Math.Max(400.0, windowWidth * 0.5) / guiScale + 40.0, maxDialogWidth));
            double bodyWidth = Math.Max(200.0, dialogWidth - ScrollbarGutter);

            // The budget dialogBase clamps itself to when it is left to pick a height
            // (GuiScreen.cs:43-46). An EXPLICIT height is not clamped, and the dialog is
            // AutosizedMainDialog + FixedMiddle, so anything taller than this is centred and hangs off
            // both edges of the screen - with the buttons at the bottom, off the screen entirely and
            // Escape mapped to Cancel, that makes the world unstartable. So the body scrolls instead.
            double maxContentHeight = Math.Max(300.0, windowHeight) / guiScale - 120.0;
            double maxBodyHeight = Math.Max(MinBodyHeight, maxContentHeight - (TitleHeight + TitleGap + ButtonGap + ButtonRowHeight));

            CairoFont font = CairoFont.WhiteSmallText().WithFontSize(17f).WithLineHeightMultiplier(1.25);
            double textHeight = screenManager.api.Gui.Text.GetMultilineTextHeight(font, text, GuiElement.scaled(bodyWidth)) / RuntimeEnv.GUIScale;
            double bodyHeight = Math.Min(textHeight + 10.0, maxBodyHeight);

            ElementBounds titleBounds = ElementBounds.Fixed(EnumDialogArea.LeftTop, 0.0, 0.0, bodyWidth, TitleHeight);

            // The credits screen's shape (GuiScreenCredits.cs:47-63): the body's own bounds are forked
            // into the inset that positions them, and the clip is a sibling copy taken BEFORE compose,
            // so it keeps the visible height while GuiElementRichtext grows the body's bounds to the
            // real content height. That difference is what the scrollbar is sized from.
            ElementBounds bodyBounds = ElementBounds.Fixed(0.0, 0.0, bodyWidth, bodyHeight);
            ElementBounds insetBounds = bodyBounds.ForkBoundingParent(3.0, 3.0, 3.0, 3.0).FixedUnder(titleBounds, TitleGap);
            ElementBounds clipBounds = bodyBounds.CopyOffsetedSibling();

            ElementBounds buttonBounds = ElementBounds.Fixed(0.0, 0.0, 0.0, 0.0)
                .WithFixedPadding(10.0, 2.0)
                .FixedUnder(insetBounds, ButtonGap);

            double contentHeight = TitleHeight + TitleGap + insetBounds.fixedHeight + ButtonGap + ButtonRowHeight;

            GuiComposer composer = dialogBase("mainmenu-bettermods-profilemismatch", dialogWidth, contentHeight)
                .AddStaticText(Headline(prompt), CairoFont.WhiteSmallishText().WithWeight(FontWeight.Bold), titleBounds)
                .AddInset(insetBounds)
                .AddVerticalScrollbar(OnNewScrollbarValue, ElementStdBounds.VerticalScrollbar(insetBounds), ScrollbarKey)
                .BeginClip(clipBounds)
                .AddRichtext(text, font, bodyBounds, BodyKey)
                .EndClip()
                .AddButton("Cancel", OnCancel, buttonBounds, EnumButtonStyle.Normal, CancelButtonKey);

            // With nothing to switch to there are only two buttons, so Play takes the right-hand slot
            // rather than leaving a hole where Switch would have been.
            composer.AddButton(
                "Play with " + Elide(active),
                OnPlayWithActive,
                buttonBounds.FlatCopy().WithAlignment(canSwitch ? EnumDialogArea.CenterFixed : EnumDialogArea.RightFixed),
                EnumButtonStyle.Normal,
                PlayButtonKey);

            if (canSwitch)
            {
                composer.AddButton(
                    "Switch to " + Elide(suggested),
                    OnSwitchToSuggested,
                    buttonBounds.FlatCopy().WithAlignment(EnumDialogArea.RightFixed),
                    EnumButtonStyle.Normal,
                    SwitchButtonKey);
            }

            // dialogBase's own OnComposed handler (GuiScreen.Cmp_OnRecomposed) centres every menu
            // screen in the space RIGHT of the sidebar, whether or not the sidebar is shown. With it
            // hidden, that leaves the prompt off-centre - and on a narrow window still overlapping
            // the sidebar's footprint. Subscribed after it, so this has the last word on every
            // compose, including the engine's own recompose after a resize.
            composer.OnComposed += () => MenuLayout.Centre(this, composer);

            ElementComposer = composer.EndChildElements().Compose();

            // Same tail as every scrolling vanilla screen: the richtext has just published its real
            // height into bodyBounds, and the clip still holds the visible one.
            try
            {
                clipBounds.CalcWorldBounds();
                ElementComposer.GetScrollbar(ScrollbarKey)?.SetHeights((float)clipBounds.fixedHeight, (float)bodyBounds.fixedHeight);
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not size the profile prompt's scrollbar: " + e.Message);
            }
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
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not scroll the profile prompt: " + e.Message);
            }
        }

        // ---------------------------------------------------------------- wording

        /// <summary>
        /// The title, leading with whichever condition is worst. Missing mods beat an exact match,
        /// which beats a bare binding difference.
        /// </summary>
        private static string Headline(ProfileMismatchPrompt prompt)
        {
            int missing = prompt.Missing.Count;
            if (missing == 1) return "1 mod this world used is not enabled";
            if (missing > 1) return missing + " mods this world used are not enabled";

            if (prompt.SuggestedIsExact) return "A profile matches this world exactly";

            return "Mod Profile Mismatch";
        }

        private static string BuildText(ProfileMismatchPrompt prompt, string active, string suggested, bool canSwitch)
        {
            StringBuilder sb = new StringBuilder();
            string world = Sanitize(prompt.WorldName);
            string worldRef = string.IsNullOrEmpty(world) ? "This world" : "The world <strong>" + world + "</strong>";

            if (prompt.HasRecord)
            {
                sb.Append(worldRef).Append(" last loaded with ").Append(Count(prompt.RecordedModCount, "mod")).Append('.');
            }
            else
            {
                sb.Append(worldRef).Append(" has no recorded mod list yet.");
            }

            sb.Append(" The profile about to be used is <strong>").Append(active).Append("</strong>.\n");

            if (prompt.Missing.Count > 0)
            {
                sb.Append("\nNot enabled by <strong>").Append(active).Append("</strong>:\n");
                Bullets(sb, prompt.Missing, MissingListLimit);
            }

            if (prompt.Differences.Count > 0)
            {
                sb.Append("\nInstalled at a different version (usually fine):\n");

                List<string> lines = new List<string>();
                foreach (ModVersionDifference difference in prompt.Differences)
                {
                    lines.Add(difference.ModId + ": " + Blank(difference.WorldVersion) + " to " + Blank(difference.ProfileVersion));
                }

                Bullets(sb, lines, DifferenceListLimit);
            }

            if (canSwitch)
            {
                sb.Append('\n');
                if (prompt.SuggestedIsExact)
                {
                    sb.Append("The profile <strong>").Append(suggested)
                      .Append("</strong> enables exactly the mods this world last loaded with.");

                    if (prompt.ExactMatchCount > 1)
                    {
                        sb.Append(' ').Append(Count(prompt.ExactMatchCount, "profile")).Append(" match; this is ");
                        sb.Append(prompt.SuggestedIsBound ? "the one the world is bound to." : "the first one you created.");
                    }
                    else if (prompt.SuggestedIsBound)
                    {
                        sb.Append(" It is also the profile the world is bound to.");
                    }
                }
                else
                {
                    sb.Append("This world is bound to the profile <strong>").Append(suggested).Append("</strong>.");
                }

                sb.Append('\n');
            }

            if (!string.IsNullOrEmpty(prompt.BoundProfileName))
            {
                sb.Append("\nThe world is bound to <strong>").Append(Sanitize(prompt.BoundProfileName)).Append("</strong>.\n");
            }

            sb.Append('\n');
            if (canSwitch)
            {
                sb.Append("Switching applies ").Append(suggested).Append(" and then starts the world.\n");
            }

            sb.Append("Playing starts the world as it is and binds it to ").Append(active)
              .Append(", so you will not be asked again.");

            return sb.ToString();
        }

        /// <summary>
        /// One list item per line, elided past <paramref name="limit"/>. Deliberately ASCII: the Cairo
        /// toy font API has no fallback, so a bullet glyph can render as a box.
        /// </summary>
        private static void Bullets(StringBuilder sb, List<string> items, int limit)
        {
            int shown = Math.Min(items.Count, limit);
            for (int i = 0; i < shown; i++)
            {
                sb.Append("  - ").Append(Sanitize(items[i])).Append('\n');
            }

            int rest = items.Count - shown;
            if (rest > 0) sb.Append("  ... and ").Append(rest).Append(" more\n");
        }

        private static string Count(int n, string noun) => n + " " + noun + (n == 1 ? "" : "s");

        private static string Blank(string version) => string.IsNullOrWhiteSpace(version) ? "--" : version;

        // ---------------------------------------------------------------- choices

        /// <summary>Escape closes the prompt the same way Cancel does.</summary>
        public override bool OnBackPressed()
        {
            Answer(ProfileMismatchChoice.Cancel);
            return true;
        }

        private bool OnCancel()
        {
            Answer(ProfileMismatchChoice.Cancel);
            return true;
        }

        private bool OnPlayWithActive()
        {
            Answer(ProfileMismatchChoice.PlayWithActive);
            return true;
        }

        private bool OnSwitchToSuggested()
        {
            Answer(ProfileMismatchChoice.SwitchToSuggested);
            return true;
        }

        /// <summary>
        /// Reports the choice exactly once. The buttons are disabled first, the way vanilla's own
        /// confirmation screen does (GuiScreenConfirmAction.cs:37), because the handler hands control
        /// straight to <c>ConnectToSingleplayer</c> and a second click would start a second world.
        /// </summary>
        private void Answer(ProfileMismatchChoice choice)
        {
            if (answered) return;
            answered = true;

            try
            {
                Disable(CancelButtonKey);
                Disable(PlayButtonKey);
                Disable(SwitchButtonKey);
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not disable the profile prompt buttons: " + e.Message);
            }

            try
            {
                onChoice?.Invoke(choice);
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Error("[BetterMods] Handling the profile prompt choice failed.");
                BetterModsModSystem.Logger?.Error(e);
            }
        }

        private void Disable(string key)
        {
            GuiElementTextButton button = ElementComposer?.GetButton(key);
            if (button != null) button.Enabled = false;
        }

        /// <summary>
        /// Profile names, world names and mod ids are all free text and the body is VTML, so strip the
        /// two characters that would otherwise be read as markup. Also collapses newlines, which would
        /// break the layout.
        /// </summary>
        private static string Sanitize(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";

            return name.Trim()
                .Replace("<", "(")
                .Replace(">", ")")
                .Replace("\r", " ")
                .Replace("\n", " ");
        }

        private static string Elide(string name)
        {
            if (string.IsNullOrEmpty(name)) return "profile";
            if (name.Length <= ButtonNameLimit) return name;

            return name.Substring(0, ButtonNameLimit - 1).TrimEnd() + "...";
        }
    }
}
