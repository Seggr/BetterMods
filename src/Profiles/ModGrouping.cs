using System;
using System.Collections.Generic;
using Vintagestory.API.Config;
using Vintagestory.Common;

namespace BetterMods.Profiles
{
    /// <summary>
    /// Turns a flat list of installed mods into one <see cref="ModVersionGroup"/> per ModID and owns
    /// the "one version on, siblings off" rule.
    /// <para>
    /// Both surfaces go through here - the in-game dialog feeds it <see cref="InstalledModScanner"/>
    /// output, the main-menu screen feeds it the mods behind vanilla's <c>LoadModCells</c> result -
    /// so the grouping and selection rules exist once.
    /// </para>
    /// <para>
    /// Nothing here touches <c>ClientSettings</c>. Editing a profile and applying one are separate
    /// operations everywhere else in this mod and stay separate here.
    /// </para>
    /// </summary>
    public static class ModGrouping
    {
        /// <summary>
        /// Remembers a per-profile version choice for a mod that is currently switched OFF.
        /// <para>
        /// The profile file stores the DISABLED set, so "off" means every version is listed and there
        /// is nowhere to record which one was picked. Rather than change the on-disk shape for a
        /// transient UI detail, the choice lives here for the session; switching the mod back on
        /// writes it to the profile, and a lost entry just falls back to the newest version, which is
        /// the documented default anyway.
        /// </para>
        /// </summary>
        private static readonly Dictionary<string, string> PendingSelections =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// One group per ModID, in the order the mods were supplied, each with its versions sorted
        /// newest first and its selection resolved against <paramref name="profileId"/>.
        /// <para>
        /// A profile that has several versions of one mod enabled at once - a fresh profile, or one
        /// written before versions were collapsed - is normalised to the newest of them and saved,
        /// so what a later <c>ApplyProfile</c> pushes into <c>ClientSettings</c> already obeys the
        /// invariant.
        /// </para>
        /// <para>
        /// The normalisation is collected across every group and written ONCE, at the end. Both
        /// surfaces rebuild the whole list on every tab switch and vanilla runs <c>LoadModCells</c>
        /// twice per rebuild, so a save per group made simply displaying the mod list a burst of full
        /// config rewrites.
        /// </para>
        /// <para>Never throws; a failure degrades to however many groups were built before it.</para>
        /// </summary>
        public static List<ModVersionGroup> Build(IEnumerable<ModContainer> mods, string profileId)
        {
            List<ModVersionGroup> groups = new List<ModVersionGroup>();
            List<KeyValuePair<List<string>, string>> normalisations = new List<KeyValuePair<List<string>, string>>();

            try
            {
                Dictionary<string, ModVersionGroup> byModId = new Dictionary<string, ModVersionGroup>(StringComparer.OrdinalIgnoreCase);

                if (mods != null)
                {
                    foreach (ModContainer mod in mods)
                    {
                        string modId = InstalledModScanner.ModIdOf(mod);
                        string key = InstalledModScanner.EntryKey(mod);
                        if (string.IsNullOrEmpty(modId) || key == null) continue;

                        if (!byModId.TryGetValue(modId, out ModVersionGroup group))
                        {
                            group = new ModVersionGroup { ModId = modId };
                            byModId[modId] = group;
                            groups.Add(group);
                        }

                        // The same modid@version present twice on disk is one choice, not two - but
                        // the group still has to own the second copy, or its row escapes grouping.
                        if (IndexOfKey(group, key) >= 0)
                        {
                            group.Duplicates.Add(mod);
                            continue;
                        }

                        group.Versions.Add(mod);
                        group.Keys.Add(key);
                    }
                }

                HashSet<string> disabled = new HashSet<string>(ProfileStore.GetDisabledMods(profileId), StringComparer.OrdinalIgnoreCase);

                foreach (ModVersionGroup group in groups)
                {
                    bool ordered = SortNewestFirst(group);
                    Resolve(group, profileId, disabled, ordered, normalisations);
                }

                if (normalisations.Count > 0) ProfileStore.SetVersionSelections(profileId, normalisations);
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Error("[BetterMods] Grouping installed mods by mod id failed.");
                BetterModsModSystem.Logger?.Error(e);
            }

            return groups;
        }

        /// <summary>
        /// Switches the whole mod on or off in <paramref name="profileId"/>: on leaves exactly the
        /// selected version enabled and disables its siblings, off disables all of them.
        /// </summary>
        public static void SetEnabled(string profileId, ModVersionGroup group, bool enabled)
        {
            if (group == null) return;

            try
            {
                // Remembered in both directions, so switching a mod off and on again comes back on
                // the version the player had chosen rather than snapping to the newest.
                SetPending(profileId, group.ModId, group.SelectedKey);

                ProfileStore.SetVersionSelection(profileId, group.Keys, enabled ? group.SelectedKey : null);
                group.Enabled = enabled;
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Error("[BetterMods] Could not switch '" + group.ModId + "' in profile '" + profileId + "'.");
                BetterModsModSystem.Logger?.Error(e);
            }
        }

        /// <summary>
        /// Points the profile at another installed version of the same mod. Only writes the profile
        /// when the mod is switched on - when it is off every version is disabled and there is
        /// nothing to write, so the choice is only remembered.
        /// </summary>
        public static void SelectVersion(string profileId, ModVersionGroup group, int index)
        {
            if (group == null || index < 0 || index >= group.Versions.Count) return;

            try
            {
                group.SelectedIndex = index;
                SetPending(profileId, group.ModId, group.SelectedKey);

                if (group.Enabled) ProfileStore.SetVersionSelection(profileId, group.Keys, group.SelectedKey);
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Error("[BetterMods] Could not select a version of '" + group.ModId + "'.");
                BetterModsModSystem.Logger?.Error(e);
            }
        }

        /// <summary>
        /// Makes <paramref name="versionKey"/> the version a switched-OFF group shows in
        /// <paramref name="profileId"/> - what switching it on would then enable. Used after an update
        /// is installed: a profile that has the mod off still "switches" to the new version, it just
        /// stays off. Session-only, like every pending selection.
        /// </summary>
        public static void RememberSelection(string profileId, string modId, string versionKey)
        {
            if (string.IsNullOrEmpty(modId)) return;
            SetPending(profileId, modId, versionKey);
        }

        /// <summary>
        /// Orders two version strings newest first.
        /// <para>
        /// <c>GameVersion.IsNewerVersionThan</c> is the game's own comparer (VintagestoryAPI,
        /// Vintagestory.API.Config/GameVersion.cs:227) and understands the <c>1.2.3-rc.4</c> shape
        /// that <c>string.CompareOrdinal</c> gets wrong. It throws on a null or empty version and
        /// can trip over exotic ones, hence the guards; anything it cannot judge falls back to a
        /// reverse ordinal compare so the order is at least stable.
        /// </para>
        /// </summary>
        public static int CompareNewestFirst(string a, string b)
        {
            bool aBlank = string.IsNullOrWhiteSpace(a);
            bool bBlank = string.IsNullOrWhiteSpace(b);
            if (aBlank || bBlank)
            {
                if (aBlank && bBlank) return 0;
                return aBlank ? 1 : -1;
            }

            try
            {
                if (GameVersion.IsNewerVersionThan(a, b)) return -1;
                if (GameVersion.IsNewerVersionThan(b, a)) return 1;
                return 0;
            }
            catch (Exception)
            {
                return string.CompareOrdinal(b, a);
            }
        }

        // ---------------------------------------------------------------- internals

        /// <summary>
        /// Orders a group newest version first. Returns false when the order could NOT be
        /// established, which the caller must treat as "do not persist anything derived from this
        /// order" — the group is left in discovery order and the newest version is then whatever
        /// happened to be read off disk first.
        /// </summary>
        private static bool SortNewestFirst(ModVersionGroup group)
        {
            if (group.Versions.Count < 2) return true;

            List<int> order = new List<int>(group.Versions.Count);
            for (int i = 0; i < group.Versions.Count; i++) order.Add(i);

            try
            {
                order.Sort((a, b) =>
                {
                    int result = CompareNewestFirst(group.Versions[a].Info?.Version, group.Versions[b].Info?.Version);
                    return result != 0 ? result : a.CompareTo(b);
                });
            }
            catch (InvalidOperationException e)
            {
                // List.Sort throws if the comparer contradicts itself, which GameVersion's asymmetric
                // handling of odd suffixes can manage. Leave the group in discovery order rather
                // than losing it: the selection logic below still works, just not newest-first.
                BetterModsModSystem.Logger?.Warning(
                    "[BetterMods] Could not order the installed versions of '" + group.ModId +
                    "': " + e.Message + ". It is displayed in discovery order and its version choice will not be rewritten.");
                return false;
            }

            List<ModContainer> sortedMods = new List<ModContainer>(group.Versions.Count);
            List<string> sortedKeys = new List<string>(group.Keys.Count);
            foreach (int index in order)
            {
                sortedMods.Add(group.Versions[index]);
                sortedKeys.Add(group.Keys[index]);
            }

            group.Versions.Clear();
            group.Versions.AddRange(sortedMods);
            group.Keys.Clear();
            group.Keys.AddRange(sortedKeys);
            return true;
        }

        /// <summary>
        /// Decides whether the group is on, and on which version.
        /// <para>
        /// A normalisation - the profile has several versions of one mod enabled and only the newest
        /// may stay on - is APPENDED to <paramref name="normalisations"/> rather than written here,
        /// and only when <paramref name="ordered"/> says the version order is trustworthy. Persisting
        /// a choice derived from discovery order would let merely opening the mods screen rewrite the
        /// profile to an older version, traced by nothing but a warning line.
        /// </para>
        /// </summary>
        private static void Resolve(
            ModVersionGroup group,
            string profileId,
            HashSet<string> disabled,
            bool ordered,
            List<KeyValuePair<List<string>, string>> normalisations)
        {
            // A bare mod id in the disabled set means every installed copy is off, including copies
            // the caller could not enumerate - see ProfileStore.SetVersionSelection.
            bool allOff = !string.IsNullOrEmpty(group.ModId) && disabled.Contains(group.ModId);

            List<int> enabled = new List<int>();
            if (!allOff)
            {
                for (int i = 0; i < group.Keys.Count; i++)
                {
                    if (disabled.Contains(group.Keys[i])) continue;

                    // A blocked mod is never really enabled - vanilla refuses to switch it on at all.
                    if (group.Versions[i].Error == ModError.Blocked) continue;

                    enabled.Add(i);
                }
            }

            if (enabled.Count > 0)
            {
                group.Enabled = true;
                group.SelectedIndex = enabled[0];

                if (enabled.Count > 1 && ordered)
                {
                    normalisations.Add(new KeyValuePair<List<string>, string>(new List<string>(group.Keys), group.SelectedKey));
                }

                return;
            }

            group.Enabled = false;
            group.SelectedIndex = PreferredIndex(group, profileId);
        }

        /// <summary>Which version a switched-off group shows: the remembered one, else the newest usable.</summary>
        private static int PreferredIndex(ModVersionGroup group, string profileId)
        {
            string pending = GetPending(profileId, group.ModId);
            if (pending != null)
            {
                int remembered = IndexOfKey(group, pending);
                if (remembered >= 0) return remembered;
            }

            for (int i = 0; i < group.Versions.Count; i++)
            {
                if (group.Versions[i].Error != ModError.Blocked) return i;
            }

            return 0;
        }

        private static int IndexOfKey(ModVersionGroup group, string key)
        {
            for (int i = 0; i < group.Keys.Count; i++)
            {
                if (string.Equals(group.Keys[i], key, StringComparison.OrdinalIgnoreCase)) return i;
            }

            return -1;
        }

        private static string PendingKey(string profileId, string modId) => (profileId ?? "") + "|" + (modId ?? "");

        private static void SetPending(string profileId, string modId, string versionKey)
        {
            if (versionKey == null) return;
            PendingSelections[PendingKey(profileId, modId)] = versionKey;
        }

        private static string GetPending(string profileId, string modId)
        {
            return PendingSelections.TryGetValue(PendingKey(profileId, modId), out string key) ? key : null;
        }
    }
}
