using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.Client;
using Vintagestory.Common;

namespace BetterMods.Profiles
{
    /// <summary>The profile offered as a better fit for the world being started, and why.</summary>
    public class ProfileMatch
    {
        public ModProfile Profile;

        /// <summary>True when this profile's enabled set is identical to the world's recorded list.</summary>
        public bool IsExact;

        /// <summary>True when the world is bound to this profile.</summary>
        public bool IsBound;

        /// <summary>How many profiles matched the world's list exactly. 0 when none did.</summary>
        public int ExactCount;
    }

    /// <summary>
    /// Answers "which mods would this profile actually enable?" from the main menu, and finds the
    /// profile whose answer matches a world's recorded mod list exactly.
    /// <para>
    /// The installed mod set comes from <c>ScreenManager.allMods</c>, through
    /// <see cref="InstalledModScanner.MenuMods"/>. That is the list vanilla's own mods screen is built
    /// from: it is populated by <c>loadMods()</c> during startup, refreshed by "Reload Mods", and
    /// includes loose <c>.cs</c> mods (whose <c>ModInfo</c> 1.23 reads by parsing the source, not by
    /// compiling it).
    /// </para>
    /// <para>
    /// This is a PREDICTION and is documented as such. It applies the profile's disabled set and the
    /// "one version on, siblings off" rule, but it does not simulate dependency resolution or a mod
    /// that fails to compile — which only the real load knows about. That asymmetry can only ever
    /// cost an exact match (the prediction lists a mod the real load dropped, which counts as
    /// "extra"), and extras are never warned about — PROVIDED the installed list is complete. It is
    /// complete when <c>allMods</c> could be read, and there is no fallback any more: an unreadable
    /// list is reported as unreliable. Under-reporting inverts the whole guarantee: fewer installed
    /// mods means more mods reported Missing, and Missing is the only thing that warns. Hence
    /// <c>reliable</c>, which every warning path must honour.
    /// </para>
    /// <para>Nothing here throws; a failure reads as "no installed mods", i.e. no opinion.</para>
    /// </summary>
    public static class ProfileMatcher
    {
        /// <summary>
        /// Every installed mod as the main menu sees it. Never null. See
        /// <see cref="InstalledModScanner.MenuMods"/> for what <paramref name="reliable"/> promises.
        /// </summary>
        public static List<ModContainer> InstalledMods(ScreenManager screenManager, out bool reliable)
        {
            return InstalledModScanner.MenuMods(screenManager, out reliable);
        }

        /// <summary>
        /// A world's recorded list with every mod id the client cannot load removed - see
        /// <see cref="InstalledModScanner.LoadsClientSide"/>. Null in, null out.
        /// <para>
        /// A capture taken on 1.23 never contains a server-only mod, but one recorded on 1.22 does
        /// (the client's loader still listed them then), and <see cref="EnabledKeys"/> no longer
        /// predicts them - so without this, the first 1.23 start of such a world reports each one as
        /// Missing, which is the warning case. Only ids that ARE installed and are server-only in
        /// every copy are dropped; an id that is not installed at all is genuinely missing and stays.
        /// </para>
        /// </summary>
        public static List<string> ClientLoadable(List<string> worldList, IEnumerable<ModContainer> installed)
        {
            if (worldList == null) return null;

            HashSet<string> serverOnly;
            try
            {
                serverOnly = InstalledModScanner.ServerOnlyModIds(installed);
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not work out which installed mods are server-only: " + e.Message);
                return worldList;
            }

            return ClientLoadable(worldList, serverOnly);
        }

        /// <summary>
        /// <see cref="ClientLoadable(List{string}, IEnumerable{ModContainer})"/> with the server-only
        /// set already worked out, for a caller that filters many lists against one installed set.
        /// </summary>
        public static List<string> ClientLoadable(List<string> worldList, HashSet<string> serverOnly)
        {
            if (worldList == null) return null;

            try
            {
                if (serverOnly == null || serverOnly.Count == 0) return worldList;

                List<string> kept = new List<string>(worldList.Count);
                foreach (string entry in worldList)
                {
                    WorldModList.Split(entry, out string modId, out string _);
                    if (modId != null && serverOnly.Contains(modId)) continue;

                    kept.Add(entry);
                }

                return kept;
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not filter server-only mods out of a world's mod list: " + e.Message);
                return worldList;
            }
        }

        /// <summary>
        /// The <c>"modid@version"</c> keys a profile would leave enabled, core mods excluded, at most
        /// one version per mod id.
        /// <para>
        /// Deliberately read-only: unlike <c>ModGrouping.Build</c>, which normalises a profile that
        /// has several versions of one mod enabled and SAVES the result, this only reports. Evaluating
        /// every profile in order to answer a question must not rewrite them all.
        /// </para>
        /// <para>
        /// Where a profile does have several versions of one mod enabled, the newest wins — the same
        /// choice <c>ModLoader.CheckDuplicateModIDMods</c> makes at load time ("Will only load the
        /// highest version one").
        /// </para>
        /// <para>
        /// Returns NULL, never a partial list, when it could not work the answer out. A half-built
        /// list is indistinguishable from "this profile enables almost nothing", and a caller reading
        /// it that way reports every mod the world uses as missing — the most alarming prompt this
        /// mod can produce, off the back of an exception nobody saw.
        /// </para>
        /// </summary>
        public static List<string> EnabledKeys(IEnumerable<ModContainer> installed, string profileId)
        {
            try
            {
                if (installed == null) return null;

                List<string> keys = new List<string>();
                HashSet<string> disabled = new HashSet<string>(ProfileStore.GetDisabledMods(profileId), StringComparer.OrdinalIgnoreCase);

                // modid -> the newest enabled version seen so far.
                Dictionary<string, string> best = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                List<string> order = new List<string>();

                foreach (ModContainer mod in installed)
                {
                    ModInfo info = mod?.Info;
                    if (info == null || info.CoreMod) continue;

                    // A blocked mod cannot load, and ModGrouping.Resolve already refuses to count one
                    // as enabled. The two have to agree or a blocked mod renders OFF on the mods
                    // screen while this counts it as part of what the profile would load.
                    if (mod.Error == ModError.Blocked) continue;

                    // 1.23's client loader switches off a mod whose Side excludes the client, so it
                    // can never be part of what this profile loads client-side, nor of a capture.
                    if (!InstalledModScanner.LoadsClientSide(info)) continue;

                    string key = InstalledModScanner.EntryKey(info);
                    if (key == null) continue;
                    if (disabled.Contains(key)) continue;

                    WorldModList.Split(key, out string modId, out string version);
                    if (string.IsNullOrEmpty(modId)) continue;

                    // A bare mod id in the disabled set switches off every installed copy — see
                    // ProfileStore.SetVersionSelection.
                    if (disabled.Contains(modId)) continue;

                    if (!best.TryGetValue(modId, out string incumbent))
                    {
                        best[modId] = version;
                        order.Add(modId);
                        continue;
                    }

                    if (ModGrouping.CompareNewestFirst(version, incumbent) < 0) best[modId] = version;
                }

                foreach (string modId in order) keys.Add(WorldModList.Key(modId, best[modId]));
                keys.Sort(StringComparer.OrdinalIgnoreCase);
                return keys;
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Error("[BetterMods] Could not work out which mods profile '" + profileId + "' would enable.");
                BetterModsModSystem.Logger?.Error(e);
                return null;
            }
        }

        /// <summary>
        /// The profile to offer for a world whose recorded mod list is <paramref name="worldList"/>,
        /// or null when nothing beats what is already active.
        /// <para>
        /// An EXACT match wins: same set of <c>"modid@version"</c>, core mods excluded, nothing
        /// missing and nothing extra. If several profiles match exactly, the one the save is bound to
        /// is preferred; otherwise the first in creation order, with the count reported so the prompt
        /// can say how many there were. If nothing matches exactly, the bound profile is offered on
        /// the strength of the binding alone, which is the pre-existing behaviour.
        /// </para>
        /// </summary>
        public static ProfileMatch Suggest(
            IEnumerable<ModContainer> installed,
            IEnumerable<string> worldList,
            string activeProfileId,
            string boundProfileId)
        {
            try
            {
                List<ModProfile> exact = new List<ModProfile>();

                if (worldList != null)
                {
                    // ProfileStore.Profiles is a snapshot in creation order, which is what makes the
                    // tie-break deterministic across sessions.
                    foreach (ModProfile profile in ProfileStore.Profiles)
                    {
                        if (profile == null) continue;

                        List<string> predicted = EnabledKeys(installed, profile.Id);

                        // No answer for this profile. Skipping it costs at most a suggestion;
                        // comparing against a list we do not have would invent one.
                        if (predicted == null) continue;

                        if (WorldModList.Compare(worldList, predicted).IsExactMatch) exact.Add(profile);
                    }
                }

                if (exact.Count > 0)
                {
                    ModProfile chosen = null;
                    foreach (ModProfile profile in exact)
                    {
                        if (string.Equals(profile.Id, boundProfileId, StringComparison.Ordinal)) chosen = profile;
                    }

                    chosen = chosen ?? exact[0];

                    // Already on it: nothing to offer.
                    if (string.Equals(chosen.Id, activeProfileId, StringComparison.Ordinal)) return null;

                    return new ProfileMatch
                    {
                        Profile = chosen,
                        IsExact = true,
                        IsBound = string.Equals(chosen.Id, boundProfileId, StringComparison.Ordinal),
                        ExactCount = exact.Count
                    };
                }

                if (string.IsNullOrEmpty(boundProfileId)) return null;
                if (string.Equals(boundProfileId, activeProfileId, StringComparison.Ordinal)) return null;

                ModProfile bound = ProfileStore.FindProfile(boundProfileId);
                if (bound == null) return null;

                return new ProfileMatch { Profile = bound, IsExact = false, IsBound = true, ExactCount = 0 };
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Error("[BetterMods] Could not look for a profile matching this world.");
                BetterModsModSystem.Logger?.Error(e);
                return null;
            }
        }

    }
}
