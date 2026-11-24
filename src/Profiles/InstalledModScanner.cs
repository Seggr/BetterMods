using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.Client;
using Vintagestory.Common;

namespace BetterMods.Profiles
{
    /// <summary>
    /// Produces the list of INSTALLED mods, including the ones that are currently disabled, for both
    /// surfaces.
    /// <para>
    /// The source is the game's own menu-side list, <c>ScreenManager.allMods</c> (internal, so reached
    /// with a field ref). It is what the vanilla mods screen is built from: <c>loadMods()</c> fills it
    /// during <c>DoGameInitStage4</c>, "Reload Mods" and the server-mod download screen refill it, and
    /// it holds every recognised entry under every mod path - <c>--addModPath</c> included, disabled
    /// mods included, loose <c>.cs</c> mods included. The <c>ScreenManager</c> outlives every world
    /// (<c>ClientProgram.screenManager</c> is a public static), so the in-game dialog reads the very
    /// same list.
    /// </para>
    /// <para>
    /// Up to 1.22 BetterMods also walked the mod folders itself and wrapped each entry in a
    /// <see cref="ModContainer"/>. 1.23 made that constructor private (the factory is now
    /// <c>ModContainer.CreateIfSourceTypeIsUnderstood</c>), and the scan was only ever a fallback for
    /// a field that is still there - so it is gone, and with it the per-scan <c>BitmapExternal</c>
    /// icons nothing disposed. The tradeoff: a mod copied into a mod folder while the game is running
    /// is not listed until "Reload Mods", exactly as on the vanilla mods screen.
    /// </para>
    /// <para>
    /// Nothing here throws into the GUI. When <c>allMods</c> cannot be read, the in-game dialog
    /// degrades to "just the loaded mods" and every main-menu caller is told the list is unreliable,
    /// which makes them say nothing rather than warn on an under-reported set.
    /// </para>
    /// </summary>
    public static class InstalledModScanner
    {
        private static readonly AccessTools.FieldRef<ScreenManager, List<ModContainer>> AllModsRef =
            GameFields.Ref<ScreenManager, List<ModContainer>>("allMods",
                "installed mods cannot be listed; world mod-list checks stay silent and the in-game dialog shows loaded mods only");

        /// <summary>
        /// All installed mods for the in-game dialog: the loaded (enabled) ones from
        /// <c>capi.ModLoader.Mods</c>, plus every other installed mod from <c>ScreenManager.allMods</c>.
        /// Sorted by display name. Never null, never throws.
        /// <para>
        /// The loaded containers win a tie on <c>"modid@version"</c>: they are the ones this world is
        /// actually running.
        /// </para>
        /// <para>
        /// Deliberately does NOT report whether the list is complete, unlike <see cref="MenuMods"/>.
        /// The completeness flag exists so that a caller which would WARN about a mod being missing
        /// can refuse to speak on an under-reported list. This is the in-game dialog's row source: it
        /// renders what it found and offers to toggle it, and a mod it cannot see is a row the player
        /// does not get - never a warning about a world. If anything here ever grows into a
        /// missing-mod warning, it has to take the flag with it.
        /// </para>
        /// </summary>
        public static List<ModContainer> Scan(ICoreClientAPI capi)
        {
            List<ModContainer> result = new List<ModContainer>();
            HashSet<string> seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (ModContainer loaded in LoadedMods(capi))
            {
                if (!Claim(loaded, seenIds)) continue;
                result.Add(loaded);
            }

            // Discarded on purpose: see the remarks above.
            foreach (ModContainer installed in MenuMods(ClientProgram.screenManager, out bool _))
            {
                if (!Claim(installed, seenIds)) continue;
                result.Add(installed);
            }

            result.Sort((a, b) => string.Compare(DisplayName(a), DisplayName(b), StringComparison.OrdinalIgnoreCase));
            return result;
        }

        /// <summary>
        /// Every installed mod as the main menu sees it - a copy of <c>ScreenManager.allMods</c>.
        /// Never null, never throws.
        /// <para>
        /// <paramref name="reliable"/> is true only when the list was read and is non-empty. The
        /// bundled core mods are always installed, so an empty list means <c>loadMods()</c> has not
        /// run, not that there is nothing to list. A prediction built on an incomplete list may report
        /// a mod as missing that is installed and will load perfectly well, so a caller that would
        /// warn the player must degrade to saying nothing rather than say something confident and
        /// wrong.
        /// </para>
        /// </summary>
        public static List<ModContainer> MenuMods(ScreenManager screenManager, out bool reliable)
        {
            reliable = false;

            try
            {
                if (screenManager == null || AllModsRef == null) return new List<ModContainer>();

                List<ModContainer> allMods = AllModsRef(screenManager);
                if (allMods == null) return new List<ModContainer>();

                List<ModContainer> copy = new List<ModContainer>(allMods);
                reliable = copy.Count > 0;
                return copy;
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not read ScreenManager.allMods: " + e.Message);
                return new List<ModContainer>();
            }
        }

        /// <summary>
        /// Whether the client's mod loader will load this mod at all.
        /// <para>
        /// On 1.22 that is every mod with a <c>ModInfo</c>: 1.22's <c>ModLoader.LoadMods</c> has no
        /// side filter (verified on the 1.22.7 dll), so a <c>"side": "server"</c> mod stays enabled in
        /// the client's <c>IModLoader.Mods</c> - only its systems skip <c>StartClientSide</c> - and a
        /// world's recorded mod list contains it. The prediction must include it too, or a profile
        /// holding a server-only mod never matches a world exactly.
        /// </para>
        /// <para>
        /// 1.23 switches such mods off at load (<c>"Mod {0} is disabled because it is not compatible
        /// with the current side"</c>); the 1.23 build of BetterMods tests <c>ModInfo.Side</c> here.
        /// With this returning true for every mod, <see cref="ServerOnlyModIds"/> is always empty and
        /// <c>ProfileMatcher.ClientLoadable</c> passes lists through unchanged.
        /// </para>
        /// </summary>
        public static bool LoadsClientSide(ModInfo info)
        {
            return info != null;
        }

        /// <summary>
        /// The mod ids for which EVERY installed copy is server-only, i.e. ids the client can never
        /// load and a capture can never record. A mod id that is not installed at all is not in here -
        /// that one is genuinely missing.
        /// </summary>
        public static HashSet<string> ServerOnlyModIds(IEnumerable<ModContainer> installed)
        {
            HashSet<string> serverOnly = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (installed == null) return serverOnly;

            HashSet<string> clientLoadable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ModContainer mod in installed)
            {
                ModInfo info = mod?.Info;
                if (info == null) continue;

                string modId = ModId(info);
                if (string.IsNullOrEmpty(modId)) continue;

                if (LoadsClientSide(info)) clientLoadable.Add(modId);
                else serverOnly.Add(modId);
            }

            serverOnly.ExceptWith(clientLoadable);
            return serverOnly;
        }

        /// <summary>The <c>"modid@version"</c> key a mod is addressed by in a profile, or null.</summary>
        public static string EntryKey(ModContainer mod)
        {
            return EntryKey(mod?.Info);
        }

        /// <summary>
        /// The <c>"modid@version"</c> key for a bare <see cref="ModInfo"/>, or null. The
        /// <see cref="ModContainer"/>-less overload exists because the in-game recorder reads
        /// <c>capi.ModLoader.Mods</c>, which is typed as the abstract <c>Mod</c>.
        /// </summary>
        public static string EntryKey(ModInfo info)
        {
            if (info == null) return null;

            string modId = ModId(info);
            return string.IsNullOrEmpty(modId) ? null : modId + "@" + info.Version;
        }

        /// <summary>
        /// The mod id every installed copy of a mod shares, or null when it cannot be determined.
        /// This is the key <see cref="ModGrouping"/> collapses duplicate versions on.
        /// </summary>
        public static string ModIdOf(ModContainer mod)
        {
            ModInfo info = mod?.Info;
            return info == null ? null : ModId(info);
        }

        private static string DisplayName(ModContainer mod)
        {
            string name = mod?.Info?.Name;
            return string.IsNullOrWhiteSpace(name) ? (mod?.FileName ?? "") : name;
        }

        /// <summary>
        /// <c>ModInfo.ModID</c> is optional in modinfo.json and falls back to the name run through
        /// <c>ModInfo.ToModID</c>. That helper throws for names starting with a digit, hence the
        /// guard.
        /// </summary>
        private static string ModId(ModInfo info)
        {
            if (!string.IsNullOrEmpty(info.ModID)) return info.ModID;

            try
            {
                return ModInfo.ToModID(info.Name);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// First copy of a <c>"modid@version"</c> wins. On 1.23 a container whose
        /// <c>modinfo.json</c> failed to load has a null <c>Info</c>; it has no key and is never
        /// claimed, which is also how vanilla's own <c>"ModInfo failed to load!"</c> row ends up
        /// outside every profile.
        /// </summary>
        private static bool Claim(ModContainer mod, HashSet<string> seenIds)
        {
            if (mod == null) return false;

            string key = EntryKey(mod);
            if (key == null) return false;

            return seenIds.Add(key);
        }

        private static IEnumerable<ModContainer> LoadedMods(ICoreClientAPI capi)
        {
            IModLoader loader = capi?.ModLoader;
            if (loader?.Mods == null) return new List<ModContainer>();

            try
            {
                return loader.Mods.OfType<ModContainer>().ToList();
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Error("[BetterMods] Could not read the loaded mod list.");
                BetterModsModSystem.Logger?.Error(e);
                return new List<ModContainer>();
            }
        }
    }
}
