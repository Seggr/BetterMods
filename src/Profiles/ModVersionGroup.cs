using System.Collections.Generic;
using Vintagestory.Common;

namespace BetterMods.Profiles
{
    /// <summary>
    /// Every installed copy of one <c>ModInfo.ModID</c>, collapsed into the single row the GUI shows
    /// for it, plus which of those copies the viewed profile uses.
    /// <para>
    /// <c>ModLoader</c> cannot sanely load two versions of one ModID at once, so exactly one version
    /// is "selected". <see cref="Enabled"/> is the row's on/off switch and is independent of the
    /// selection: a group can be switched off while still remembering which version it would use.
    /// </para>
    /// </summary>
    public class ModVersionGroup
    {
        /// <summary>The <c>ModInfo.ModID</c> every entry in <see cref="Versions"/> shares.</summary>
        public string ModId;

        /// <summary>Installed copies, newest version first. Never empty for a group the GUI shows.</summary>
        public readonly List<ModContainer> Versions = new List<ModContainer>();

        /// <summary><c>"modid@version"</c> for each entry of <see cref="Versions"/>, same order.</summary>
        public readonly List<string> Keys = new List<string>();

        /// <summary>
        /// Further installed copies whose <c>"modid@version"</c> is one already in <see cref="Keys"/>
        /// — the same mod at the same version present twice on disk. They are ONE choice, so they get
        /// no entry of their own here, but the surfaces still have to know the group owns them:
        /// without that the mods screen keeps the duplicate's row on its "not one of ours" branch and
        /// the player sees two rows for one mod, one showing profile state and one showing
        /// <c>ClientSettings</c>.
        /// </summary>
        public readonly List<ModContainer> Duplicates = new List<ModContainer>();

        /// <summary>Index into <see cref="Versions"/> of the version this profile uses.</summary>
        public int SelectedIndex;

        /// <summary>False when every version of this mod is in the profile's disabled set.</summary>
        public bool Enabled;

        public ModContainer Selected => SelectedIndex >= 0 && SelectedIndex < Versions.Count ? Versions[SelectedIndex] : null;

        public string SelectedKey => SelectedIndex >= 0 && SelectedIndex < Keys.Count ? Keys[SelectedIndex] : null;

        /// <summary>True when there is actually something to choose between.</summary>
        public bool HasVersionChoice => Versions.Count > 1;

        /// <summary>
        /// What goes in the row's version slot - the selected version and nothing else, exactly as
        /// vanilla writes it.
        /// <para>
        /// The "this can be clicked" cue for a multi-version mod is NOT in this string: the trigger
        /// box and its arrow are drawn onto the cell surface by <c>ModCellLayout</c>, which needs the
        /// label to measure as the bare version so the box can be sized around it.
        /// </para>
        /// </summary>
        public string VersionLabel
        {
            get
            {
                string version = Selected?.Info?.Version;
                return string.IsNullOrEmpty(version) ? "--" : version;
            }
        }

        /// <summary>The entries of the version picker, in <see cref="Versions"/> order.</summary>
        public string[] VersionNames()
        {
            string[] names = new string[Versions.Count];
            for (int i = 0; i < Versions.Count; i++)
            {
                string version = Versions[i]?.Info?.Version;
                names[i] = string.IsNullOrEmpty(version) ? "--" : version;
            }

            return names;
        }
    }
}
