using System;
using HarmonyLib;

namespace BetterMods
{
    /// <summary>
    /// One place for "bind a private or internal game field by name, or degrade".
    /// <para>
    /// Several of the things BetterMods needs are fields the game does not expose -
    /// <c>ScreenManager.allMods</c>, <c>ScreenManager.CurrentScreen</c>,
    /// <c>StartServerArgs.DisabledMods</c>, <c>GuiScreenMods.listBounds</c>. Every one of them is
    /// reached the same way and every one of them is allowed to be missing on a game version this
    /// build has not seen, so the accessor is resolved ONCE into a static readonly field and a null
    /// result is the caller's "this feature stays vanilla" signal.
    /// </para>
    /// <para>
    /// The throw has to be caught here rather than left to a static constructor: an exception out of
    /// a field initialiser becomes a <c>TypeInitializationException</c> that poisons the whole type
    /// for the process, which would turn one missing field into a dead patch class.
    /// </para>
    /// </summary>
    internal static class GameFields
    {
        /// <summary>
        /// The accessor for <typeparamref name="TObject"/>'s <paramref name="fieldName"/>, or null
        /// when this game version has no such field.
        /// </summary>
        /// <param name="fieldName">Field name, exactly as the game declares it.</param>
        /// <param name="consequence">
        /// What the caller does without it, appended to the log line. Optional, because most callers
        /// simply degrade and the field name is the whole story.
        /// </param>
        internal static AccessTools.FieldRef<TObject, TField> Ref<TObject, TField>(string fieldName, string consequence = null)
        {
            try
            {
                return AccessTools.FieldRefAccess<TObject, TField>(fieldName);
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Error(
                    "[BetterMods] " + typeof(TObject).Name + "." + fieldName + " not found on this game version" +
                    (string.IsNullOrEmpty(consequence) ? "" : "; " + consequence) + ": " + e.Message);
                return null;
            }
        }
    }
}
