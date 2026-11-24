using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace BetterMods.Profiles
{
    /// <summary>One mod the world loaded with at a version other than the one about to be enabled.</summary>
    public class ModVersionDifference
    {
        public string ModId;

        /// <summary>The version recorded against the world.</summary>
        public string WorldVersion;

        /// <summary>The version the profile being evaluated would enable.</summary>
        public string ProfileVersion;
    }

    /// <summary>
    /// What comparing a world's recorded mod list against one profile's enabled set produced.
    /// </summary>
    public class ModListComparison
    {
        /// <summary>
        /// Mods the world loaded with whose MOD ID will not be enabled at all, as the world recorded
        /// them (<c>"modid@version"</c>). This is the only warning case.
        /// </summary>
        public readonly List<string> Missing = new List<string>();

        /// <summary>Same mod id, different version. Information, not a warning.</summary>
        public readonly List<ModVersionDifference> Differences = new List<ModVersionDifference>();

        /// <summary>Enabled mods the world's list does not mention. Deliberately never warned about.</summary>
        public int ExtraCount;

        /// <summary>
        /// True when the two sets are identical down to the version — no missing, no version
        /// difference, nothing extra. Core mods are excluded from both sides before this is computed.
        /// </summary>
        public bool IsExactMatch => Missing.Count == 0 && Differences.Count == 0 && ExtraCount == 0;

        /// <summary>True when there is something the player would want to be asked about.</summary>
        public bool HasWarning => Missing.Count > 0;
    }

    /// <summary>
    /// The mod list a world actually loaded with: how it is captured, and how it is compared with
    /// what a profile would enable.
    /// <para>
    /// This is the second, stronger of the two savegame signals. A profile binding says what the
    /// player CHOSE; this says what the world was observed to NEED, so it survives the player
    /// renaming, cloning or losing profiles, and it travels with a copied world through the
    /// <c>SaveGame.ModData</c> mirror.
    /// </para>
    /// <para>
    /// Core mods (<c>ModInfo.CoreMod</c> — game, survival, creative, essentials) are excluded on both
    /// sides. They are present in every load and would be identical noise in every comparison; the
    /// vanilla mods screen already treats them as a special case (GuiScreenMods.cs:200).
    /// </para>
    /// <para>Nothing here throws; a failure reads as "no opinion".</para>
    /// </summary>
    public static class WorldModList
    {
        /// <summary>
        /// The non-core mods currently loaded, as <c>"modid@version"</c>, sorted so two captures of
        /// the same load compare equal. Returns null — meaning "could not observe" — rather than an
        /// empty list, which would be recorded as "this world loads no mods".
        /// <para>
        /// <c>IModLoader.Mods</c> is <c>loadedMods.Values.Where(mod =&gt; mod.Enabled)</c>
        /// (ModLoader.cs:44), i.e. exactly the mods that survived disabling, blocking, duplicate-id
        /// resolution and dependency checking. That is the point: this is an observation of a real
        /// load, not a prediction.
        /// </para>
        /// </summary>
        public static List<string> Capture(ICoreClientAPI api)
        {
            try
            {
                IEnumerable<Mod> mods = api?.ModLoader?.Mods;
                if (mods == null) return null;

                List<string> keys = new List<string>();
                HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (Mod mod in mods)
                {
                    ModInfo info = mod?.Info;
                    if (info == null || info.CoreMod) continue;

                    string key = InstalledModScanner.EntryKey(info);
                    if (key == null) continue;

                    if (seen.Add(key)) keys.Add(key);
                }

                keys.Sort(StringComparer.OrdinalIgnoreCase);
                return keys;
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Error("[BetterMods] Could not record the mod list this world loaded with.");
                BetterModsModSystem.Logger?.Error(e);
                return null;
            }
        }

        /// <summary>
        /// Compares a world's recorded list against the set a profile would enable. Both are
        /// <c>"modid@version"</c>, core mods already excluded.
        /// <para>
        /// Matching is by MOD ID, ignoring the version: a mod installed at a different version is a
        /// version difference, not a missing mod. That distinction is the whole reason the comparison
        /// is not a set subtraction.
        /// </para>
        /// </summary>
        public static ModListComparison Compare(IEnumerable<string> worldList, IEnumerable<string> enabledKeys)
        {
            ModListComparison result = new ModListComparison();

            try
            {
                Dictionary<string, string> world = ByModId(worldList);
                Dictionary<string, string> enabled = ByModId(enabledKeys);

                foreach (KeyValuePair<string, string> entry in world)
                {
                    if (!enabled.TryGetValue(entry.Key, out string enabledVersion))
                    {
                        result.Missing.Add(Key(entry.Key, entry.Value));
                        continue;
                    }

                    if (string.Equals(entry.Value ?? "", enabledVersion ?? "", StringComparison.OrdinalIgnoreCase)) continue;

                    result.Differences.Add(new ModVersionDifference
                    {
                        ModId = entry.Key,
                        WorldVersion = entry.Value,
                        ProfileVersion = enabledVersion
                    });
                }

                foreach (string modId in enabled.Keys)
                {
                    if (!world.ContainsKey(modId)) result.ExtraCount++;
                }

                result.Missing.Sort(StringComparer.OrdinalIgnoreCase);
                result.Differences.Sort((a, b) => string.Compare(a.ModId, b.ModId, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Error("[BetterMods] Comparing this world's mod list failed; treating it as a match.");
                BetterModsModSystem.Logger?.Error(e);
                return new ModListComparison();
            }

            return result;
        }

        /// <summary>Splits <c>"modid@version"</c>. A key with no <c>@</c> is all mod id.</summary>
        public static void Split(string entry, out string modId, out string version)
        {
            modId = null;
            version = "";

            if (string.IsNullOrWhiteSpace(entry)) return;

            string trimmed = entry.Trim();
            int at = trimmed.IndexOf('@');
            if (at <= 0)
            {
                modId = trimmed;
                return;
            }

            modId = trimmed.Substring(0, at);
            version = trimmed.Substring(at + 1);
        }

        /// <summary>Rebuilds a <c>"modid@version"</c> key, tolerating a blank version.</summary>
        public static string Key(string modId, string version)
        {
            return string.IsNullOrEmpty(version) ? modId : modId + "@" + version;
        }

        /// <summary>
        /// Collapses a key list to one version per mod id. Duplicates cannot happen in a well-formed
        /// list — <c>ModLoader</c> refuses to load two versions of one mod id
        /// (<c>CheckDuplicateModIDMods</c>, ModLoader.cs) — so first wins rather than merging.
        /// </summary>
        private static Dictionary<string, string> ByModId(IEnumerable<string> keys)
        {
            Dictionary<string, string> byId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (keys == null) return byId;

            foreach (string entry in keys)
            {
                Split(entry, out string modId, out string version);
                if (string.IsNullOrEmpty(modId)) continue;
                if (!byId.ContainsKey(modId)) byId[modId] = version;
            }

            return byId;
        }
    }
}
