using System;
using System.Collections.Generic;

namespace BetterMods.Profiles
{
    /// <summary>
    /// A named set of mod enable/disable state.
    /// <para>
    /// We store the DISABLED set rather than the enabled set so that it maps 1:1 onto
    /// <c>ClientSettings.DisabledMods</c>, and so a newly installed mod defaults to enabled
    /// in every profile — exactly matching vanilla behaviour.
    /// </para>
    /// </summary>
    public class ModProfile
    {
        /// <summary>Stable identity of the profile. A GUID string; names are free to collide.</summary>
        public string Id = Guid.NewGuid().ToString();

        /// <summary>Display name. Not unique.</summary>
        public string Name = "";

        /// <summary>
        /// Disabled mods in <c>"modid@version"</c> form, mirroring <c>ClientSettings.DisabledMods</c>.
        /// <para>
        /// Since schema v4 a BARE <c>modid</c> may also appear, alongside — never instead of — the
        /// per-version entries, and means "every installed copy of this mod is off, including any this
        /// machine could not enumerate". <c>ModLoader.DisableMods</c> honours it (ModLoader.cs:325).
        /// See <c>ProfileStore.SetVersionSelection</c> for why it exists.
        /// </para>
        /// </summary>
        public List<string> DisabledMods = new List<string>();

        public ModProfile()
        {
        }

        public ModProfile(string name, IEnumerable<string> disabledMods = null)
        {
            Id = Guid.NewGuid().ToString();
            Name = name ?? "";
            DisabledMods = disabledMods != null ? new List<string>(disabledMods) : new List<string>();
        }
    }
}
