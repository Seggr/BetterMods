using System;
using System.Collections.Generic;
using Vintagestory.API.Common;

namespace BetterMods.Profiles
{
    /// <summary>
    /// Which mod versions have had their assembly loaded into THIS PROCESS, and therefore which
    /// version swaps the player has to restart the game for.
    /// <para>
    /// <c>ModAssemblyLoader.LoadFrom</c> is <c>Assembly.UnsafeLoadFrom</c>: the same path returns the
    /// assembly already loaded, but a DIFFERENT path with the same simple name throws "Assembly with
    /// same name is already loaded", which <c>ModContainer.LoadAssembly</c> turns into
    /// <c>ModError.ChangedVersion</c> for the whole mod (ModContainer.cs:558-566). Nothing can undo
    /// that inside the running process - loading mod assemblies into a collectible
    /// AssemblyLoadContext was built, measured and abandoned as a mitigation. So the only honest
    /// thing left is to say so before the player loads a world and finds the mod missing.
    /// </para>
    /// <para>
    /// Note what this is NOT: it is not "has a world been loaded". A version swap is free for a mod
    /// whose assembly was never loaded - a cold launch, a content mod, or a mod that was switched off
    /// for every world so far - and warning about those would train the player to ignore the line.
    /// The only mods recorded here are the CODE mods that a world actually loaded, taken from
    /// <c>IModLoader.Mods</c>, which is the post-disable, post-block, post-dependency set.
    /// </para>
    /// <para>
    /// Process-lifetime state on purpose, exactly like the resident assemblies it describes: it is
    /// never cleared on world exit, and the FIRST version seen for a mod id wins, because that is the
    /// one that is resident and that no later load can displace.
    /// </para>
    /// </summary>
    public static class LoadedModVersions
    {
        /// <summary>ModID to the version whose assembly this process is holding.</summary>
        private static readonly Dictionary<string, string> ResidentVersions =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The tab-row form, for the main-menu screen where there is only a narrow slot. ASCII only -
        /// Cairo's toy font API does no glyph fallback.
        /// </summary>
        public const string ShortNotice = "Restart needed - version changed";

        /// <summary>What the in-game dialog says once a profile has been applied.</summary>
        public const string AppliedSuffix = " Restart the game to use the new version.";

        /// <summary>
        /// Records the code mods a world has just loaded. Called from
        /// <c>BetterModsModSystem.StartClientSide</c>: by then every enabled mod's assembly is already
        /// resident (instantiating a mod system requires having loaded it), and <c>IModLoader.Mods</c>
        /// is fixed for the lifetime of the world.
        /// </summary>
        public static void Record(IModLoader loader)
        {
            try
            {
                IEnumerable<Mod> mods = loader?.Mods;
                if (mods == null) return;

                foreach (Mod mod in mods)
                {
                    ModInfo info = mod?.Info;
                    if (info == null) continue;
                    if (string.IsNullOrEmpty(info.ModID) || string.IsNullOrEmpty(info.Version)) continue;

                    // A content or theme mod has no assembly - LoadAssembly returns early for
                    // anything but Code (ModContainer.cs:515-522) - so swapping its version is free.
                    if (info.Type != EnumModType.Code) continue;

                    // First load wins; see the class remarks.
                    if (ResidentVersions.ContainsKey(info.ModID)) continue;

                    ResidentVersions[info.ModID] = info.Version;
                }
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not record which mod versions this session loaded: " + e.Message);
            }
        }

        /// <summary>
        /// True when applying this group would ask the game to load a version of a mod whose
        /// assembly is already resident under another version.
        /// <para>
        /// Both conditions matter. A group that is switched OFF loads nothing, and disabling a mod
        /// needs no restart at all (<c>InstantiateModSystems</c> early-returns on <c>!Enabled</c>);
        /// and picking the version that is already loaded is the no-op that
        /// <c>Assembly.UnsafeLoadFrom</c> handles by handing back the resident assembly.
        /// </para>
        /// </summary>
        public static bool NeedsRestart(ModVersionGroup group)
        {
            if (group == null || !group.Enabled || string.IsNullOrEmpty(group.ModId)) return false;
            if (!ResidentVersions.TryGetValue(group.ModId, out string resident)) return false;

            string selected = group.Selected?.Info?.Version;
            if (string.IsNullOrEmpty(selected)) return false;

            return !string.Equals(resident, selected, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The mod ids in <paramref name="groups"/> that a restart is needed for, if any.</summary>
        public static List<string> ModIdsNeedingRestart(IEnumerable<ModVersionGroup> groups)
        {
            List<string> ids = new List<string>();

            try
            {
                if (groups == null) return ids;

                foreach (ModVersionGroup group in groups)
                {
                    if (NeedsRestart(group)) ids.Add(group.ModId);
                }
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not work out whether a restart is needed: " + e.Message);
            }

            return ids;
        }

        /// <summary>
        /// One line for a surface with room for a sentence, or null when nothing needs a restart.
        /// Mod IDs rather than names, because <c>ModInfo.ModID</c> is the normalised ASCII form.
        /// </summary>
        public static string Describe(List<string> modIds)
        {
            if (modIds == null || modIds.Count == 0) return null;

            return modIds.Count == 1
                ? "Restart the game to use the new version of " + modIds[0] + "."
                : "Restart the game to use the new versions of " + modIds.Count + " mods.";
        }
    }
}
