namespace BetterMods.Gui
{
    /// <summary>
    /// One shared "that click belonged to a popup" gate for every floating BetterMods element.
    /// <para>
    /// The popups - the version picker and the rename panel - float OVER the mod list, and the rows
    /// underneath them react on mouse-UP (<c>GuiElementModCell.OnMouseUpOnElement</c> is what invokes
    /// <c>OnMouseDownOnCellLeft</c>/<c>Right</c>, despite the names). Without this a click on a popup
    /// would also toggle whatever row happens to be behind it. A popup raises the gate on mouse-down
    /// and lowers it again on mouse-up; the row handlers on both surfaces check it in between.
    /// </para>
    /// <para>
    /// The gate is shared rather than per-popup because the row handlers must not have to know which
    /// popup was open, and because the composer dispatches mouse-up to EVERY element - so whichever
    /// popup runs last would otherwise lower its own flag while another one's stayed raised. Popups
    /// are added to the composer after the mod list, so their mouse-up always runs after the rows
    /// have read the gate.
    /// </para>
    /// <para>
    /// <strong>The gate deliberately outlives the element that raised it.</strong> Picking a version
    /// happens on mouse-DOWN and queues a rebuild of the whole surface; that rebuild drains from the
    /// main-thread task queue, which is a different point in the frame from input dispatch, so for
    /// any click held longer than one frame - i.e. every human click - the popup that raised the gate
    /// has already been disposed and replaced by the time the matching mouse-up arrives. The
    /// replacement popup must therefore NOT lower the gate when it is constructed: if it did, the
    /// leaked mouse-up would land on whichever row is now under the cursor (the popup hangs below its
    /// anchor row, so it is never the row that was clicked) and silently toggle that mod. Only a
    /// mouse-up, or an explicitly fresh surface (<see cref="Release"/> from
    /// <c>GuiDialogModProfiles.OnGuiOpened</c> / a genuine open of the mods screen), lowers it.
    /// </para>
    /// <para>
    /// A gate can therefore only be left raised when the matching mouse-up never reaches ANY composer
    /// holding one of our popups - the window losing focus mid-click is the realistic case. The cost
    /// is one swallowed row click, which the same click's mouse-up then clears; the fresh-open
    /// releases above close even that.
    /// </para>
    /// </summary>
    public static class PopupClickGate
    {
        /// <summary>
        /// True from the moment an open popup swallows a mouse-down until the matching mouse-up has
        /// been dispatched.
        /// </summary>
        public static bool ClickConsumed { get; private set; }

        public static void Consume()
        {
            ClickConsumed = true;
        }

        public static void Release()
        {
            ClickConsumed = false;
        }
    }
}
