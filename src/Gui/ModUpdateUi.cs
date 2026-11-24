using System.Collections.Generic;
using BetterMods.Profiles;
using BetterMods.Updates;
using Vintagestory.API.Client;

namespace BetterMods.Gui
{
    /// <summary>
    /// The update check's surface-level words - the main-menu status line and the in-game hint - so the
    /// two surfaces say the same thing. What a single card shows is <see cref="ModCardContent"/>'s.
    /// ASCII only throughout - Cairo's toy font API does no glyph fallback.
    /// </summary>
    internal static class ModUpdateUi
    {
        /// <summary>
        /// The main-menu status line and the label of the one button next to it (null for no button).
        /// The button installs every offered update when there are any, and re-checks otherwise.
        /// <para>
        /// Never "up to date": on a preview build almost nothing is tagged for the game version yet,
        /// and "nothing newer for this version" is all the DB can actually tell us.
        /// </para>
        /// </summary>
        public static string HeaderStatus(IEnumerable<ModVersionGroup> groups, out double[] color, out string buttonLabel, out bool buttonInstalls)
        {
            color = null;
            buttonLabel = null;
            buttonInstalls = false;

            if (ModUpdateService.IsInstalling)
            {
                return ModUpdateService.InstallLabel + " (" + ModUpdateService.InstallFinished + "/" + ModUpdateService.InstallTotal + ")...";
            }

            // "Update all" is tier 1 only; tier-2 offers (releases for an older game version) are
            // installed one by one, each after a confirmation, and only counted here.
            bool done = ModUpdateService.State == UpdateCheckState.Done;
            int updates = done ? ModUpdateService.CountUpdates(groups) : 0;
            int older = done ? ModUpdateService.CountOlderGameVersionOffers(groups) : 0;
            if (updates > 0)
            {
                buttonLabel = "Update all";
                buttonInstalls = true;
            }

            if (ModUpdateService.InstallSummary != null)
            {
                if (ModUpdateService.InstallSummaryIsError) color = GuiStyle.WarningTextColor;
                buttonLabel ??= "Check again";
                return ModUpdateService.InstallSummary;
            }

            switch (ModUpdateService.State)
            {
                case UpdateCheckState.Checking:
                    return "Checking for updates...";

                case UpdateCheckState.Failed:
                    color = GuiStyle.WarningTextColor;
                    buttonLabel = "Retry";
                    return "Update check failed";

                case UpdateCheckState.Done:
                    if (updates > 0)
                    {
                        string line = updates == 1 ? "1 update available" : updates + " updates available";
                        if (older > 0) line += ", " + older + " more for " + OlderLabel();
                        return ModUpdateService.CheckIncomplete ? line + " (some mods unchecked)" : line;
                    }

                    buttonLabel = "Check again";
                    if (older > 0)
                    {
                        color = GuiStyle.WarningTextColor;
                        string line = (older == 1 ? "1 update" : older + " updates") + " only for " + OlderLabel() + " - confirm on the card";
                        return ModUpdateService.CheckIncomplete ? line + " (some mods unchecked)" : line;
                    }

                    if (ModUpdateService.CheckIncomplete)
                    {
                        color = GuiStyle.WarningTextColor;
                        return "Some mods could not be checked";
                    }

                    return "No newer releases for " + ModUpdateService.GameVersionChecked;

                default:
                    buttonLabel = "Check for updates";
                    return "";
            }
        }

        /// <summary>The in-game dialog's hint, or null when there is nothing to say.</summary>
        public static string InGameHint(IEnumerable<ModVersionGroup> groups)
        {
            if (ModUpdateService.State != UpdateCheckState.Done) return null;

            int updates = ModUpdateService.CountUpdates(groups);
            int older = ModUpdateService.CountOlderGameVersionOffers(groups);
            if (updates <= 0 && older <= 0) return null;

            if (updates <= 0)
            {
                return (older == 1 ? "1 mod update" : older + " mod updates") + " for " + OlderLabel() + " available - install from the Mods screen in the main menu.";
            }

            string extra = older > 0 ? " (+" + older + " for " + OlderLabel() + ")" : "";
            return updates == 1
                ? "1 mod update available" + extra + " - install it from the Mods screen in the main menu."
                : updates + " mod updates available" + extra + " - install them from the Mods screen in the main menu.";
        }

        /// <summary>The older game version tier 2 asked about, as words.</summary>
        private static string OlderLabel()
        {
            string older = ModUpdateService.OlderGameVersion;
            return string.IsNullOrEmpty(older) ? "an older game version" : "game " + ModUpdateRules.AsciiLine(older, 20);
        }
    }
}
