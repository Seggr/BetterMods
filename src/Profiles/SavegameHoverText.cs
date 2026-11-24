using System;
using System.Collections.Generic;
using System.Text;
using Vintagestory.Client;
using Vintagestory.Common;

namespace BetterMods.Profiles
{
    /// <summary>
    /// Builds the block BetterMods appends to a world's hover tooltip on the singleplayer save list:
    /// the profile the world is bound to, and the mods it was last observed to load with.
    /// <para>
    /// The tooltip is composed text, not a live element — <c>GuiScreenSingleplayer.InitGui</c> bakes
    /// one <c>AddHoverText</c> per row from the string <c>getHoverText</c> returned — so this runs once
    /// per row per compose, never per frame. It still has to stay cheap, because a compose walks every
    /// save the player has.
    /// </para>
    /// <para>
    /// Nothing here throws and nothing here reads a <c>.vcdbs</c>: the recorded list comes from
    /// <c>bettermods.json</c>, which is already in memory. A world whose list has never been observed
    /// gets NOTHING appended — a "not recorded" stub on every unplayed world would be pure noise.
    /// </para>
    /// </summary>
    public static class SavegameHoverText
    {
        /// <summary>
        /// How many mods are named before the rest are summarised.
        /// <para>
        /// The hover panel's height is unbounded: <c>GuiElementHoverText.RecalcBounds</c> sizes it
        /// purely from its content and <c>RenderInteractiveElements</c> only slides it UP to fit the
        /// frame, so a panel taller than the screen loses its top rows off the edge. At
        /// <c>GuiStyle.DetailFontSize</c> (14 unscaled) a 1080p screen holds roughly 55 lines at GUI
        /// scale 1 and 37 at scale 1.5. Vanilla already spends a title line, up to a handful of
        /// world-config lines and a trailer, so 15 mods plus at most three lines of our own keeps the
        /// worst case near 25 lines — comfortably inside the tighter of those budgets, while still
        /// naming the whole list for the great majority of worlds.
        /// </para>
        /// </summary>
        private const int MaxListedMods = 15;

        /// <summary>
        /// How long <see cref="memo"/> stays valid, in milliseconds. Long enough that one compose pass
        /// over every save reuses a single answer, short enough that no sequence of player actions —
        /// which needs at least a screen change and a click — can observe a stale one.
        /// </summary>
        private const int MemoLifetimeMs = 1000;

        /// <summary>
        /// What the active profile would enable, cached across the rows of one compose.
        /// <para>
        /// This, not the per-savegame lookup, is the expensive half. Reading a world's binding is a
        /// scan of a short list under a lock; <c>ProfileMatcher.EnabledKeys</c> copies the installed
        /// mod list, builds a set from the profile's disabled entries and sorts the result, and the
        /// answer is IDENTICAL for every row. Caching per savegame identifier would cache the cheap
        /// half and repeat the costly one.
        /// </para>
        /// </summary>
        private sealed class EnabledSetMemo
        {
            public string ProfileId;
            public long Stamp;

            /// <summary>The keys, or null for "no usable answer" — never an empty list.</summary>
            public List<string> Keys;

            /// <summary>Installed mod ids the client never loads; see <c>ProfileMatcher.ClientLoadable</c>.</summary>
            public HashSet<string> ServerOnly;
        }

        private static EnabledSetMemo memo;

        /// <summary>
        /// The text to append to one row's tooltip, or null when there is nothing to say. Never throws.
        /// </summary>
        public static string Build(ScreenManager screenManager, SaveGame savegame)
        {
            try
            {
                string savegameId = savegame?.SavegameIdentifier;
                if (string.IsNullOrWhiteSpace(savegameId)) return null;

                // Path is unknown here and not needed: getHoverText is handed the SaveGame itself, so
                // the identifier is in hand. (This is what TODO-0008's blocker was about — recovering
                // a row's filename from the private, internally-typed `entries` array — and it does
                // not apply on this path.)
                List<string> worldList = ProfileStore.GetModList(savegameId, null);
                if (worldList == null) return null;

                List<string> mods = new List<string>(worldList.Count);
                foreach (string entry in worldList)
                {
                    if (!string.IsNullOrWhiteSpace(entry)) mods.Add(entry.Trim());
                }

                mods.Sort(StringComparer.OrdinalIgnoreCase);

                StringBuilder text = new StringBuilder();

                string boundId = ProfileStore.GetBoundProfileId(savegameId, null);
                string profileName = boundId == null ? null : ProfileStore.FindProfile(boundId)?.Name;
                if (!string.IsNullOrWhiteSpace(profileName))
                {
                    text.Append(Dim("BetterMods profile:")).Append(' ').Append(Plain(profileName));
                    text.Append('\n');
                }

                text.Append(Dim(mods.Count == 0
                    ? "Last loaded with no mods"
                    : "Last loaded with " + mods.Count + (mods.Count == 1 ? " mod:" : " mods:")));

                int shown = Math.Min(mods.Count, MaxListedMods);
                for (int i = 0; i < shown; i++) text.Append('\n').Append(Plain(mods[i]));

                int rest = mods.Count - shown;
                if (rest > 0) text.Append('\n').Append(Dim("+" + rest + " more"));

                string summary = CompareSummary(screenManager, mods);
                if (summary != null) text.Append('\n').Append(Dim(summary));

                return text.ToString();
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not describe a world's mod list: " + e.Message);
                return null;
            }
        }

        /// <summary>
        /// One line about how the world's list compares with what the ACTIVE profile would enable, or
        /// null when there is nothing worth saying.
        /// <para>
        /// Only missing mods are reported, for the same reason <c>SavegameProfileGate.Evaluate</c> only
        /// warns about them: a different version of the same mod usually loads fine, and extra mods are
        /// the normal state of a world you have since added things to.
        /// </para>
        /// </summary>
        private static string CompareSummary(ScreenManager screenManager, List<string> mods)
        {
            if (mods.Count == 0) return null;

            ModProfile active = ProfileStore.ActiveProfile;
            if (active == null) return null;

            EnabledSetMemo answer = ActiveEnabledKeys(screenManager, active);
            if (answer?.Keys == null) return null;

            List<string> world = ProfileMatcher.ClientLoadable(mods, answer.ServerOnly);
            int missing = WorldModList.Compare(world, answer.Keys).Missing.Count;
            if (missing == 0) return null;

            return missing + (missing == 1 ? " mod" : " mods") + " not enabled by the active profile";
        }

        /// <summary>
        /// The keys the active profile would enable, memoised for <see cref="MemoLifetimeMs"/>, with
        /// the installed server-only mod ids alongside. <c>Keys</c> is null when the installed mod
        /// list could not be established COMPLETELY — a short
        /// list makes mods the player has look missing, and "N mods not enabled" is exactly the line
        /// that must never be wrong.
        /// </summary>
        private static EnabledSetMemo ActiveEnabledKeys(ScreenManager screenManager, ModProfile active)
        {
            EnabledSetMemo current = memo;
            if (current != null &&
                string.Equals(current.ProfileId, active.Id, StringComparison.Ordinal) &&
                Environment.TickCount64 - current.Stamp < MemoLifetimeMs)
            {
                return current;
            }

            List<ModContainer> installed = ProfileMatcher.InstalledMods(screenManager, out bool reliable);
            List<string> keys = reliable ? ProfileMatcher.EnabledKeys(installed, active.Id) : null;
            HashSet<string> serverOnly = reliable ? InstalledModScanner.ServerOnlyModIds(installed) : null;

            memo = new EnabledSetMemo { ProfileId = active.Id, Stamp = Environment.TickCount64, Keys = keys, ServerOnly = serverOnly };
            return memo;
        }

        /// <summary>Wraps a label in the same dimmed font vanilla's own hover text uses.</summary>
        private static string Dim(string value)
        {
            return "<font opacity=\"0.6\">" + value + "</font>";
        }

        /// <summary>
        /// Makes third-party text safe to drop into a tooltip. Profile names, mod ids and versions are
        /// all attacker-adjacent input: the tooltip is VTML, so an unescaped <c>&lt;</c> would be
        /// parsed as markup, and <c>CairoFont.SetupContext</c> uses Cairo's toy font API, which has no
        /// glyph fallback — a non-ASCII character renders as a box. Both become '?'.
        /// </summary>
        private static string Plain(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";

            StringBuilder safe = new StringBuilder(value.Length);
            foreach (char c in value)
            {
                if (c < ' ' || c > '~' || c == '<' || c == '>') safe.Append('?');
                else safe.Append(c);
            }

            return safe.ToString();
        }
    }
}
