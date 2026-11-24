using System;
using BetterMods.Profiles;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace BetterMods.Gui
{
    /// <summary>
    /// The compact rename/delete panel that drops out of a profile tab.
    /// <para>
    /// Renaming one short string does not deserve a screen of its own, so this is built the same way
    /// as <see cref="GuiElementVersionMenu"/>: a composer element added AFTER everything else, whose
    /// bounds are cut out of the layout tree and positioned in raw screen pixels, drawn unclipped
    /// over whatever it overlaps and given last refusal on every mouse event.
    /// </para>
    /// <para>
    /// Unlike a mod row - which is baked to a Cairo texture and can host nothing - the tab strip is
    /// a normal composed element in a normal composer, on BOTH surfaces. That is what lets this panel
    /// host a real <see cref="GuiElementTextInput"/>: the input is a second composer element
    /// (<see cref="GuiElementRenameInput"/>) added straight after the panel, so it gets focus, key
    /// and mouse events from the composer exactly like any other text field. The panel draws the
    /// frame, the input's well, the Delete button and the status line; the input draws only its text,
    /// selection and caret, on top.
    /// </para>
    /// <para>
    /// Delete confirms in place - first click arms, second click deletes - rather than opening
    /// anything: the whole point of the panel is that nothing else has to appear.
    /// <c>ProfileStore.DeleteProfile</c> refuses to delete the last profile, so the button is not
    /// drawn at all when there is only one.
    /// </para>
    /// </summary>
    public class GuiElementRenamePanel : GuiElement
    {
        /// <summary>Interior inset, unscaled: clears the 2px outline and the 2px emboss of the frame.</summary>
        private const double UnscaledFramePad = 6.0;

        /// <summary>Height of the name field, unscaled.</summary>
        private const double UnscaledInputHeight = 28.0;

        /// <summary>Gap between the name field and the button row, unscaled.</summary>
        private const double UnscaledRowGap = 5.0;

        /// <summary>Height of the Delete button, unscaled.</summary>
        private const double UnscaledButtonHeight = 22.0;

        /// <summary>Padding either side of the Delete button's label, unscaled.</summary>
        private const double UnscaledButtonPadX = 10.0;

        /// <summary>Clear space between the Delete button and the status text, unscaled.</summary>
        private const double UnscaledStatusGap = 12.0;

        /// <summary>Narrowest the panel may get, unscaled.</summary>
        private const double UnscaledMinWidth = 230.0;

        /// <summary>Widest the panel may get, unscaled. A long name scrolls inside the field instead.</summary>
        private const double UnscaledMaxWidth = 460.0;

        /// <summary>Gap between the tab and the panel, unscaled.</summary>
        private const double UnscaledAnchorGap = 3.0;

        /// <summary>How close to the screen edge the panel may be pushed, unscaled.</summary>
        private const double UnscaledScreenMargin = 4.0;

        /// <summary>What this element is called in the log lines <see cref="PopupChrome"/> writes.</summary>
        private const string PopupName = "rename panel";

        /// <summary>
        /// Leftmost screen x the panel may be placed at, read each time it opens; null means the
        /// screen margin. The main-menu host points it at <see cref="MenuLayout.ClearLeftPx"/> so the
        /// panel never slides under the sidebar. See <see cref="PopupChrome.Place"/>.
        /// </summary>
        public Func<double> LeftLimit { get; set; }

        private const string HintText = "Enter saves, Esc cancels";
        private const string BlankNameText = "Name cannot be empty";
        private const string ArmedText = "Click again to delete";
        private const string DeleteLabel = "Delete";
        private const string ConfirmLabel = "Confirm";

        private readonly GuiComposer composer;
        private readonly GuiElementRenameInput input;
        private readonly TextDrawUtil textUtil = new TextDrawUtil();
        private readonly CairoFont statusFont = CairoFont.WhiteDetailText();
        private readonly CairoFont buttonFont = CairoFont.WhiteSmallText().WithFontSize((float)GuiStyle.DetailFontSize);

        private LoadedTexture bodyTexture;
        private LoadedTexture frameTexture;

        /// <summary>
        /// The Delete button, baked separately from the panel body in its two states.
        /// <para>
        /// Hovering it used to re-bake the ENTIRE panel - background, well, emboss, button, status
        /// text and a full GL upload - to change one fill's alpha from 0.25 to 0.1, so cursor jitter
        /// on the button's edge re-baked repeatedly. Both states are now baked once and the hover
        /// merely picks which texture is stamped over the body.
        /// </para>
        /// </summary>
        private LoadedTexture buttonTexture;
        private LoadedTexture buttonHoverTexture;

        // Measured once per open, in Layout(), which already has to measure all five to size the
        // panel. CairoFont.GetTextExtents news up an ImageSurface and a Context per call, so
        // re-measuring compile-time-constant strings on every bake is not free.
        private double deleteLabelWidth;
        private double confirmLabelWidth;
        private double hintWidth;
        private double blankNameWidth;
        private double armedWidth;

        // The drawn rectangles, in absolute screen pixels. The body texture, the frame, the hit tests
        // and the hosted input's bounds are all expressed against these, so drawn and clickable
        // geometry cannot drift apart.
        private double panelX;
        private double panelY;
        private double panelWidth;
        private double panelHeight;

        private double inputX;
        private double inputY;
        private double inputWidth;
        private double inputHeight;

        private double buttonX;
        private double buttonY;
        private double buttonWidth;
        private double buttonHeight;

        private bool showDelete;
        private bool deleteArmed;
        private bool blankName;
        private bool buttonHovered;

        private string profileId;
        private Action onChanged;

        /// <summary>
        /// Set when the panel wants the caret back in the name field, consumed on the next draw.
        /// <para>
        /// Focus cannot be taken from inside a mouse handler: <c>GuiComposer.OnMouseDown</c> keeps
        /// walking its element table after a handler returns and calls <c>OnFocusLost</c> on
        /// everything that follows the element which handled the click - including this panel's own
        /// input. Doing it a frame later, once that loop has finished, is what makes the focus stick.
        /// </para>
        /// </summary>
        private bool pendingFocus;

        public bool IsOpen { get; private set; }

        /// <summary>
        /// After the version picker (0.5) and after everything else, so the panel is drawn over the
        /// whole dialog. The hosted input is added straight after this element and shares the order,
        /// which - insertion order breaks the tie - puts its text over the panel's own body.
        /// </summary>
        public override double DrawOrder => 0.6;

        /// <summary>
        /// The panel itself is never focused; the field inside it is. Staying unfocusable also keeps
        /// it out of the composer's tab cycle and out of <c>MaxTabIndex</c>.
        /// </summary>
        public override bool Focusable => false;

        private GuiElementRenamePanel(GuiComposer composer)
            : base(composer.Api, ElementBounds.Fixed(0.0, 0.0, UnscaledMinWidth, 60.0).WithEmptyParent())
        {
            this.composer = composer;

            bodyTexture = new LoadedTexture(composer.Api);
            frameTexture = new LoadedTexture(composer.Api);
            buttonTexture = new LoadedTexture(composer.Api);
            buttonHoverTexture = new LoadedTexture(composer.Api);

            input = new GuiElementRenameInput(composer.Api, ElementBounds.Fixed(0.0, 0.0, UnscaledMinWidth, UnscaledInputHeight).WithEmptyParent());

            // Deliberately NOT PopupClickGate.Release(): Confirm and Delete act on mouse-down and
            // queue a rebuild that constructs this element again before the matching mouse-up
            // arrives, so releasing here would leak that mouse-up onto a mod row. See PopupClickGate.
        }

        /// <summary>
        /// Adds a closed panel, and the field it hosts, to a composer.
        /// <para>
        /// Must be called after <c>EndClip()</c> so neither is scissored by the mod list, and last,
        /// so every other element gets first refusal on a mouse event - the composer dispatches input
        /// in insertion order, not draw order. The panel is added BEFORE its field so that it can
        /// answer Enter/Escape first and so that its body is drawn underneath the text.
        /// </para>
        /// </summary>
        public static GuiElementRenamePanel AddTo(GuiComposer composer, string key)
        {
            GuiElementRenamePanel panel = new GuiElementRenamePanel(composer);

            composer.AddInteractiveElement(panel, key);
            PopupChrome.Detach(panel.Bounds, PopupName);

            composer.AddInteractiveElement(panel.input, key + "-input");
            PopupChrome.Detach(panel.input.Bounds, PopupName + " field");

            return panel;
        }

        // ---------------------------------------------------------------- open / close

        /// <summary>
        /// Opens the panel under one profile tab, prefilled with that profile's name. Returns false -
        /// and stays closed - if the tab cannot be located.
        /// </summary>
        /// <param name="tabs">The strip the tab belongs to.</param>
        /// <param name="tabIndex">Index of the tab that was right-clicked.</param>
        /// <param name="profileId">Profile the panel edits.</param>
        /// <param name="name">Name to prefill the field with.</param>
        /// <param name="changed">
        /// Invoked after a rename or a delete has been written. Callers must rebuild their surface
        /// from it - and must do so deferred, because this fires from inside the composer's own
        /// mouse/key dispatch loop.
        /// </param>
        public bool OpenFor(GuiElementProfileTabs tabs, int tabIndex, string profileId, string name, Action changed)
        {
            try
            {
                if (tabs == null || string.IsNullOrEmpty(profileId)) return false;
                if (!tabs.TryGetTabRect(tabIndex, out double tabX, out double tabY, out double tabWidth, out double tabHeight)) return false;

                this.profileId = profileId;
                onChanged = changed;

                deleteArmed = false;
                blankName = false;
                buttonHovered = false;
                showDelete = ProfileStore.Profiles.Count > 1;

                IsOpen = true;
                input.IsOpen = true;

                Layout(tabX, tabY, tabWidth, tabHeight);

                // After Layout: the field's texture is sized from the bounds Layout just gave it.
                // SetValue leaves the caret at the end of the name, which is what makes typing append
                // and Backspace trim - the engine's text input has no reachable select-all.
                input.SetValue(name ?? "");

                Compose();

                pendingFocus = true;
                return true;
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Error("[BetterMods] Could not open the rename panel.");
                BetterModsModSystem.Logger?.Error(e);
                CloseMenu();
                return false;
            }
        }

        /// <summary>Closes the panel, drops the keyboard and forgets whatever it was going to report.</summary>
        public void CloseMenu()
        {
            IsOpen = false;
            pendingFocus = false;
            deleteArmed = false;
            blankName = false;
            buttonHovered = false;
            profileId = null;
            onChanged = null;

            input.IsOpen = false;

            // Explicit, not left to the composer: the field is unfocusable while closed, so nothing
            // else would ever take the focus off it and every following keystroke would vanish.
            try
            {
                if (input.HasFocus) input.OnFocusLost();
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not release the rename field's focus: " + e.Message);
            }
        }

        // ---------------------------------------------------------------- layout

        /// <summary>
        /// Sizes the panel to its contents and hangs it off the tab that was right-clicked.
        /// <para>
        /// The panel's LEFT edge meets the tab's left edge, so it reads as belonging to that tab, and
        /// it sits <see cref="UnscaledAnchorGap"/> below it. If that would run it off the bottom of
        /// the screen and there is room above, it opens upward instead; either way it is finally
        /// clamped into the screen, so a tab near an edge still gets a usable panel.
        /// </para>
        /// <para>
        /// The width covers the widest status line the panel can ever show, not the one it is showing
        /// now, so arming Delete or failing validation never resizes or re-anchors it under the
        /// player's cursor.
        /// </para>
        /// </summary>
        private void Layout(double tabX, double tabY, double tabWidth, double tabHeight)
        {
            double framePad = GuiElement.scaled(UnscaledFramePad);
            double rowGap = GuiElement.scaled(UnscaledRowGap);
            double statusGap = GuiElement.scaled(UnscaledStatusGap);

            buttonHeight = GuiElement.scaled(UnscaledButtonHeight);
            inputHeight = GuiElement.scaled(UnscaledInputHeight);

            deleteLabelWidth = buttonFont.GetTextExtents(DeleteLabel).Width;
            confirmLabelWidth = buttonFont.GetTextExtents(ConfirmLabel).Width;
            hintWidth = statusFont.GetTextExtents(HintText).Width;
            blankNameWidth = statusFont.GetTextExtents(BlankNameText).Width;
            armedWidth = statusFont.GetTextExtents(ArmedText).Width;

            buttonWidth = showDelete
                ? Math.Ceiling(Math.Max(deleteLabelWidth, confirmLabelWidth) + 2.0 * GuiElement.scaled(UnscaledButtonPadX))
                : 0.0;

            double widestStatus = Math.Max(hintWidth, Math.Max(blankNameWidth, armedWidth));

            double contentWidth = buttonWidth + (showDelete ? statusGap : 0.0) + widestStatus;

            panelWidth = Math.Ceiling(GameMath.Clamp(
                2.0 * framePad + contentWidth,
                Math.Max(GuiElement.scaled(UnscaledMinWidth), tabWidth),
                GuiElement.scaled(UnscaledMaxWidth)));
            panelHeight = Math.Ceiling(2.0 * framePad + inputHeight + rowGap + buttonHeight);

            double gap = GuiElement.scaled(UnscaledAnchorGap);
            double margin = GuiElement.scaled(UnscaledScreenMargin);
            double screenWidth = api.Render.FrameWidth;
            double screenHeight = api.Render.FrameHeight;

            // Left edge on the tab's left edge, so the panel reads as belonging to that tab.
            PopupChrome.Place(
                tabX, tabY, tabY + tabHeight,
                panelWidth, panelHeight,
                gap, margin, screenWidth, screenHeight,
                out panelX, out panelY,
                PopupChrome.ReadLimit(LeftLimit, PopupName));

            inputX = panelX + framePad;
            inputY = panelY + framePad;
            inputWidth = panelWidth - 2.0 * framePad;

            buttonX = panelX + framePad;
            buttonY = inputY + inputHeight + rowGap;

            // Bounds IS the drawn rectangle, so the inherited hit test can never disagree with ours.
            double scale = RuntimeEnv.GUIScale;
            if (!(scale > 0.01)) scale = 1.0;

            Bounds.fixedX = panelX / scale;
            Bounds.fixedY = panelY / scale;
            Bounds.fixedWidth = panelWidth / scale;
            Bounds.fixedHeight = panelHeight / scale;
            Bounds.CalcWorldBounds();

            input.Bounds.fixedX = inputX / scale;
            input.Bounds.fixedY = inputY / scale;
            input.Bounds.fixedWidth = inputWidth / scale;
            input.Bounds.fixedHeight = inputHeight / scale;
            input.Relayout();
        }

        // ---------------------------------------------------------------- drawing

        private void Compose()
        {
            ComposeBody();
            ComposeButton();
            ComposeFrame();
        }

        /// <summary>
        /// Bakes the panel: background, the name field's well and the status line. The Delete button
        /// is <see cref="ComposeButton"/>'s, so that a hover does not re-bake all of this.
        /// <para>
        /// The well is the engine's closed-control recipe - a <c>rgba(0,0,0,0.2)</c> fill with an
        /// inverse emboss - which is what <c>GuiElementTextInput.ComposeTextElements</c> would have
        /// drawn had it not been suppressed. It is drawn here rather than there because the field is
        /// repositioned every time the panel opens, and anything a normal element paints goes into
        /// the composer's single static texture at the position it had when the composer was composed.
        /// </para>
        /// </summary>
        private void ComposeBody()
        {
            try
            {
                int width = (int)panelWidth;
                int height = (int)panelHeight;
                if (width < 4 || height < 4) return;

                ImageSurface surface = new ImageSurface(Format.Argb32, width, height);
                Context ctx = genContext(surface);

                try
                {
                    ctx.SetSourceRGBA(GuiStyle.DialogStrongBgColor);
                    GuiElement.RoundRectangle(ctx, 0.0, 0.0, width, height, GuiStyle.ElementBGRadius);
                    ctx.Fill();

                    DrawInputWell(ctx, inputX - panelX, inputY - panelY, inputWidth, inputHeight);
                    DrawStatus(ctx);

                    generateTexture(surface, ref bodyTexture);
                }
                finally
                {
                    ctx.Dispose();
                    surface.Dispose();
                }
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not compose the rename panel: " + e.Message);
            }
        }

        private void DrawInputWell(Context ctx, double x, double y, double width, double height)
        {
            ctx.SetSourceRGBA(0.0, 0.0, 0.0, 0.2);
            GuiElement.RoundRectangle(ctx, x, y, width, height, GuiStyle.ElementBGRadius);
            ctx.Fill();

            EmbossRoundRectangleElement(ctx, x, y, width, height, inverse: true, depth: (int)GuiElement.scaled(2.0), radius: 1);

            // A rejected name has to say so somewhere the eye already is, not only in the status line.
            if (!blankName) return;

            GuiElement.RoundRectangle(ctx, x + 1.0, y + 1.0, width - 2.0, height - 2.0, GuiStyle.ElementBGRadius);
            ctx.SetSourceRGBA(GuiStyle.ErrorTextColor[0], GuiStyle.ErrorTextColor[1], GuiStyle.ErrorTextColor[2], 0.9);
            ctx.LineWidth = 2.0;
            ctx.Stroke();
        }

        /// <summary>
        /// Bakes the Delete button into its own pair of textures - not hovered and hovered - stamped
        /// over the body at render time.
        /// <para>
        /// The surfaces start transparent and carry only the button's own semi-transparent fill,
        /// emboss and label. Compositing that over the body a draw later is the same result as
        /// painting it straight onto the body, which is what this used to do.
        /// </para>
        /// <para>
        /// The button is in the engine's own idiom: a dark well with an emboss, brighter while
        /// hovered. Armed, it swaps to <see cref="ConfirmLabel"/> in <c>GuiStyle.ErrorTextColor</c>
        /// over a red-tinted well - and ignores the hover, so both textures are then the same - so
        /// the destructive step never looks like the step that armed it.
        /// </para>
        /// </summary>
        private void ComposeButton()
        {
            if (!showDelete) return;

            ComposeButtonState(hovered: false, ref buttonTexture);
            ComposeButtonState(hovered: true, ref buttonHoverTexture);
        }

        private void ComposeButtonState(bool hovered, ref LoadedTexture texture)
        {
            try
            {
                // One pixel of slack: the emboss strokes right at the rectangle's edge, which on a
                // surface sized exactly to it would lose half a pixel of bevel.
                int width = (int)Math.Ceiling(buttonWidth) + 1;
                int height = (int)Math.Ceiling(buttonHeight) + 1;
                if (width < 3 || height < 3) return;

                ImageSurface surface = new ImageSurface(Format.Argb32, width, height);
                Context ctx = genContext(surface);

                try
                {
                    if (deleteArmed)
                    {
                        ctx.SetSourceRGBA(GuiStyle.ErrorTextColor[0] * 0.45, GuiStyle.ErrorTextColor[1] * 0.2, GuiStyle.ErrorTextColor[2] * 0.2, 0.85);
                    }
                    else
                    {
                        ctx.SetSourceRGBA(0.0, 0.0, 0.0, hovered ? 0.1 : 0.25);
                    }

                    GuiElement.RoundRectangle(ctx, 0.0, 0.0, buttonWidth, buttonHeight, GuiStyle.ElementBGRadius);
                    ctx.Fill();

                    EmbossRoundRectangleElement(ctx, 0.0, 0.0, buttonWidth, buttonHeight, inverse: false, depth: (int)GuiElement.scaled(2.0), radius: 1);

                    CairoFont font = deleteArmed ? buttonFont.Clone().WithColor(GuiStyle.ErrorTextColor) : buttonFont;
                    string label = deleteArmed ? ConfirmLabel : DeleteLabel;
                    double textWidth = deleteArmed ? confirmLabelWidth : deleteLabelWidth;

                    font.SetupContext(ctx);
                    DrawCentredLine(ctx, font, label, (buttonWidth - textWidth) / 2.0, 0.0, buttonHeight);

                    generateTexture(surface, ref texture);
                }
                finally
                {
                    ctx.Dispose();
                    surface.Dispose();
                }
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not compose the rename panel's Delete button: " + e.Message);
            }
        }

        private void DrawStatus(Context ctx)
        {
            string text = blankName ? BlankNameText : (deleteArmed ? ArmedText : HintText);
            double textWidth = blankName ? blankNameWidth : (deleteArmed ? armedWidth : hintWidth);

            CairoFont font = statusFont.Clone();
            if (blankName || deleteArmed) font.WithColor(GuiStyle.ErrorTextColor);
            else font.Color[3] *= 0.6;

            font.SetupContext(ctx);

            double right = panelWidth - GuiElement.scaled(UnscaledFramePad);
            double left = buttonX - panelX + buttonWidth + (showDelete ? GuiElement.scaled(UnscaledStatusGap) : 0.0);

            DrawCentredLine(ctx, font, text, Math.Max(left, right - textWidth), buttonY - panelY, buttonHeight);
        }

        /// <summary>
        /// Draws one line vertically centred in a row of <paramref name="height"/> - centring is
        /// <see cref="PopupChrome.CentringSlack"/>'s.
        /// </summary>
        private void DrawCentredLine(Context ctx, CairoFont font, string text, double x, double top, double height)
        {
            textUtil.DrawTextLine(ctx, font, text, x, top + PopupChrome.CentringSlack(font, height));
        }

        /// <summary>
        /// Bakes the panel's frame - <see cref="PopupChrome.ComposeFrame"/>, the same frame
        /// <see cref="GuiElementVersionMenu"/> draws, so the two popups read as siblings. The
        /// interior is left transparent; <see cref="ComposeBody"/> is what fills the panel.
        /// </summary>
        private void ComposeFrame()
        {
            PopupChrome.ComposeFrame(this, api, panelWidth, panelHeight, ref frameTexture, PopupName);
        }

        public override void RenderInteractiveElements(float deltaTime)
        {
            if (!IsOpen) return;

            if (pendingFocus)
            {
                pendingFocus = false;
                FocusInput();
            }

            try
            {
                // The panel is added after EndClip, but the mod list's scissor may still be live at
                // this point in the draw order and the panel deliberately overhangs it.
                bool scissored = api.Render.ScissorStack.Count > 0;
                if (scissored) api.Render.GlScissorFlag(enable: false);

                try
                {
                    int x = (int)panelX;
                    int y = (int)panelY;

                    if (bodyTexture != null && bodyTexture.TextureId != 0)
                    {
                        api.Render.Render2DTexturePremultipliedAlpha(bodyTexture.TextureId, x, y, (int)panelWidth, (int)panelHeight);
                    }

                    LoadedTexture button = buttonHovered ? buttonHoverTexture : buttonTexture;
                    if (showDelete && button != null && button.TextureId != 0)
                    {
                        api.Render.Render2DTexturePremultipliedAlpha(button.TextureId, (int)buttonX, (int)buttonY, button.Width, button.Height);
                    }

                    if (frameTexture != null && frameTexture.TextureId != 0)
                    {
                        api.Render.Render2DTexturePremultipliedAlpha(frameTexture.TextureId, x, y, (int)panelWidth, (int)panelHeight);
                    }
                }
                finally
                {
                    if (scissored) api.Render.GlScissorFlag(enable: true);
                }
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not draw the rename panel: " + e.Message);
            }
        }

        /// <summary>
        /// Puts the caret back in the name field and takes it off everything else. Only ever called
        /// from the draw pass - see <see cref="pendingFocus"/>.
        /// </summary>
        private void FocusInput()
        {
            try
            {
                composer?.UnfocusOwnElementsExcept(input);
                if (!input.HasFocus) input.OnFocusGained();
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not focus the rename field: " + e.Message);
            }
        }

        // ---------------------------------------------------------------- hit testing

        /// <summary>The drawn rectangle, exactly, and nothing at all while the panel is closed.</summary>
        public override bool IsPositionInside(int posX, int posY)
        {
            if (!IsOpen) return false;

            return posX >= panelX && posX <= panelX + panelWidth
                && posY >= panelY && posY <= panelY + panelHeight;
        }

        private bool IsOnInput(int posX, int posY)
        {
            return posX >= inputX && posX <= inputX + inputWidth
                && posY >= inputY && posY <= inputY + inputHeight;
        }

        private bool IsOnDeleteButton(int posX, int posY)
        {
            return showDelete
                && posX >= buttonX && posX <= buttonX + buttonWidth
                && posY >= buttonY && posY <= buttonY + buttonHeight;
        }

        // ---------------------------------------------------------------- input

        public override void OnMouseDown(ICoreClientAPI api, MouseEvent args)
        {
            if (!IsOpen) return;

            // Whether the click landed on the panel or dismissed it, it was meant for the panel and
            // must not also toggle the mod row underneath it on the matching mouse-up.
            PopupClickGate.Consume();

            if (!IsPositionInside(args.X, args.Y))
            {
                CloseMenu();
                api.Gui.PlaySound("menubutton");
                return;
            }

            if (IsOnInput(args.X, args.Y))
            {
                // Deliberately NOT handled: the field is the very next element the composer offers
                // this click to, and it needs it to place the caret and to keep the focus.
                Disarm();
                return;
            }

            args.Handled = true;

            if (IsOnDeleteButton(args.X, args.Y))
            {
                OnDeleteClicked(api);
                return;
            }

            // A click on the panel's own chrome: swallowed, but the caret belongs back in the field.
            pendingFocus = true;
        }

        public override void OnMouseUp(ICoreClientAPI api, MouseEvent args)
        {
            // Unconditional, and after every row handler in this composer - see PopupClickGate. The
            // panel may well have closed itself on the matching mouse-down.
            PopupClickGate.Release();
        }

        public override void OnMouseMove(ICoreClientAPI api, MouseEvent args)
        {
            if (!IsOpen) return;

            // Not handled: the hosted field is offered mouse-moves after this element and needs them
            // to drag-select. Nothing is re-baked - both button states already exist, and this only
            // picks which one is drawn.
            buttonHovered = IsOnDeleteButton(api.Input.MouseX, api.Input.MouseY);
        }

        public override void OnMouseWheel(ICoreClientAPI api, MouseWheelEventArgs args)
        {
            // Nothing to scroll here, but letting the wheel through would scroll the mod list under
            // an open panel, which reads as the panel having come loose from its tab.
            if (IsOpen && IsPositionInside(api.Input.MouseX, api.Input.MouseY)) args.SetHandled();
        }

        public override void OnKeyDown(ICoreClientAPI api, KeyEvent args)
        {
            if (!IsOpen) return;

            switch (args.KeyCode)
            {
                case (int)GlKeys.Escape:
                    CloseMenu();
                    api.Gui.PlaySound("menubutton");
                    args.Handled = true;
                    return;

                case (int)GlKeys.Enter:
                case (int)GlKeys.KeypadEnter:
                    // Handled either way, so GuiComposer.OnKeyDown does not follow up by unfocusing
                    // every editable field in the dialog - which is what it does with an Enter that
                    // nothing claimed.
                    OnCommit(api);
                    args.Handled = true;
                    return;

                case (int)GlKeys.Tab:
                    // The panel owns the keyboard while it is open and there is nowhere useful to tab
                    // to, so Tab is swallowed rather than allowed to strand the focus behind it.
                    pendingFocus = true;
                    args.Handled = true;
                    return;
            }

            // Anything else is the player typing or moving the caret: they are no longer deleting.
            Disarm();
        }

        private void Disarm()
        {
            if (!deleteArmed && !blankName) return;

            bool wasArmed = deleteArmed;
            deleteArmed = false;
            blankName = false;

            ComposeBody();
            if (wasArmed) ComposeButton();
        }

        /// <summary>
        /// Commits the rename. A blank or whitespace-only name is refused - <c>RenameProfile</c> would
        /// refuse it anyway - but visibly: the field is outlined and the status line says so, rather
        /// than the panel closing as though something happened.
        /// </summary>
        private void OnCommit(ICoreClientAPI api)
        {
            string name = input.GetText();

            if (string.IsNullOrWhiteSpace(name))
            {
                deleteArmed = false;
                blankName = true;
                ComposeBody();
                pendingFocus = true;
                api.Gui.PlaySound("menubutton");
                return;
            }

            string id = profileId;
            Action changed = onChanged;
            CloseMenu();

            try
            {
                ProfileStore.RenameProfile(id, name);
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Error("[BetterMods] Renaming profile '" + id + "' failed.");
                BetterModsModSystem.Logger?.Error(e);
            }

            api.Gui.PlaySound("menubutton");
            changed?.Invoke();
        }

        /// <summary>
        /// First click arms, second click deletes. The confirmation lives in the panel on purpose:
        /// a compact popup that opens a dialog to ask a question is no lighter than the screen it
        /// replaced.
        /// </summary>
        private void OnDeleteClicked(ICoreClientAPI api)
        {
            if (!deleteArmed)
            {
                deleteArmed = true;
                blankName = false;
                ComposeBody();
                ComposeButton();

                // The click that armed the button took the focus off the field (the composer
                // unfocuses everything after whichever element handled the click); the player may
                // still change their mind and carry on typing.
                pendingFocus = true;

                api.Gui.PlaySound("menubutton");
                return;
            }

            string id = profileId;
            Action changed = onChanged;
            CloseMenu();

            try
            {
                // Refuses to delete the last profile; the button is not drawn in that case anyway.
                ProfileStore.DeleteProfile(id);
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Error("[BetterMods] Deleting profile '" + id + "' failed.");
                BetterModsModSystem.Logger?.Error(e);
            }

            api.Gui.PlaySound("menubutton");
            changed?.Invoke();
        }

        public override void Dispose()
        {
            base.Dispose();

            bodyTexture?.Dispose();
            frameTexture?.Dispose();
            buttonTexture?.Dispose();
            buttonHoverTexture?.Dispose();
        }
    }

    /// <summary>
    /// The name field inside <see cref="GuiElementRenamePanel"/>.
    /// <para>
    /// A stock <c>GuiElementTextInput</c> paints its well into the composer's single static texture
    /// at the position its bounds had when the composer was composed. This field is repositioned
    /// every time the panel opens - it has to land under whichever tab was right-clicked - so that
    /// paint is suppressed (<see cref="ComposeTextElements"/> draws nothing static) and the panel
    /// draws the well instead. Everything else about the element is inherited: text, caret,
    /// selection, clipboard, focus handling.
    /// </para>
    /// </summary>
    public class GuiElementRenameInput : GuiElementTextInput
    {
        public GuiElementRenameInput(ICoreClientAPI capi, ElementBounds bounds)
            : base(capi, bounds, null, CairoFont.TextInput())
        {
        }

        /// <summary>Set by the panel; everything below is inert while it is false.</summary>
        public bool IsOpen { get; set; }

        /// <summary>Shares the panel's draw order, and is added after it, so the text lands on top.</summary>
        public override double DrawOrder => 0.6;

        /// <summary>
        /// Focusable only while the panel is open, so a closed panel cannot leave an invisible field
        /// holding the keyboard. It is deliberately never focusable at the moment the composer adds
        /// it, which keeps it out of <c>MaxTabIndex</c> and out of the tab cycle entirely; the panel
        /// focuses it directly instead of through <c>GuiComposer.FocusElement</c>.
        /// </summary>
        public override bool Focusable => IsOpen && base.Focusable;

        public override bool IsPositionInside(int posX, int posY)
        {
            return IsOpen && base.IsPositionInside(posX, posY);
        }

        /// <summary>
        /// Deliberately does not draw. The base implementation stamps the field's well and emboss
        /// onto the composer's shared static surface, which is baked once and cannot follow a field
        /// that moves; <see cref="Relayout"/> composes the parts that ARE per-field textures.
        /// </summary>
        public override void ComposeTextElements(Context ctx, ImageSurface surface)
        {
            ComposeOwnTextures();
        }

        /// <summary>Re-sizes and re-bakes the field for the bounds it has just been given.</summary>
        public void Relayout()
        {
            Bounds.CalcWorldBounds();
            ComposeOwnTextures();
        }

        private void ComposeOwnTextures()
        {
            try
            {
                // Vanilla grows this by the bounds' padding; there is none here, so the drawn
                // rectangle and the focus wash are the same rectangle by construction.
                highlightBounds = Bounds;

                ImageSurface surface = new ImageSurface(Format.Argb32, Math.Max(1, (int)Bounds.OuterWidth), Math.Max(1, (int)Bounds.OuterHeight));
                Context ctx = genContext(surface);

                try
                {
                    ctx.SetSourceRGBA(1.0, 1.0, 1.0, 0.2);
                    ctx.Paint();
                    generateTexture(surface, ref highlightTexture);
                }
                finally
                {
                    ctx.Dispose();
                    surface.Dispose();
                }

                // The only public route to the base class's internal RecomposeText(), which is what
                // re-renders the text at the new size.
                SetValue(GetText());
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not compose the rename field: " + e.Message);
            }
        }

        public override void RenderInteractiveElements(float deltaTime)
        {
            if (!IsOpen) return;

            try
            {
                base.RenderInteractiveElements(deltaTime);
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not draw the rename field: " + e.Message);
            }
        }
    }
}
