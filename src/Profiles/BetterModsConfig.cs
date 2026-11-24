using System.Collections.Generic;

namespace BetterMods.Profiles
{
    /// <summary>
    /// On-disk shape of <c>ModConfig/bettermods.json</c>.
    /// </summary>
    public class BetterModsConfig
    {
        /// <summary>
        /// Schema version the running code writes.
        /// <list type="bullet">
        /// <item><description>1 — profiles + active profile id.</description></item>
        /// <item><description>2 — adds <see cref="SavegameBindings"/>.</description></item>
        /// <item><description>3 — adds <see cref="SavegameBinding.ModList"/>.</description></item>
        /// <item><description>4 — adds <see cref="LastAppliedClientDisabled"/>, and allows a BARE
        /// <c>modid</c> (no <c>@version</c>) in <see cref="ModProfile.DisabledMods"/> as
        /// "every installed copy of this mod is off" — see
        /// <c>ProfileStore.SetVersionSelection</c>.</description></item>
        /// <item><description>5 — adds <see cref="TrustedLinkHosts"/>.</description></item>
        /// </list>
        /// Every bump so far has been purely additive, so an older file simply has no key for the new
        /// field and Newtonsoft leaves it at its initialiser; <c>ProfileStore.Migrate</c> only has to
        /// normalise nulls and stamp the version. Note that <c>ModList</c>'s initialiser is
        /// deliberately <c>null</c> rather than an empty list — see <see cref="SavegameBinding"/>.
        /// <para>
        /// A v3 file has no bare ids and none are invented for it, so it keeps behaving exactly as it
        /// did; a v4 file read by older code simply pushes the bare id through to
        /// <c>ClientSettings.DisabledMods</c>, where <c>ModLoader.DisableMods</c> understands it
        /// (ModLoader.cs:325). Both directions therefore keep working.
        /// </para>
        /// </summary>
        public const int CurrentVersion = 5;

        /// <summary>Schema version, bumped when the on-disk shape changes.</summary>
        public int Version = CurrentVersion;

        public List<ModProfile> Profiles = new List<ModProfile>();

        /// <summary><see cref="ModProfile.Id"/> of the currently active profile.</summary>
        public string ActiveProfileId = "";

        /// <summary>Savegame-to-profile bindings, keyed by savegame identifier. Added in v2.</summary>
        public List<SavegameBinding> SavegameBindings = new List<SavegameBinding>();

        /// <summary>
        /// The last <c>ClientSettings.DisabledMods</c> BetterMods itself wrote, as the baseline for
        /// detecting changes made by anything else — <c>ModDbUtil.MaybeEnableMod</c> above all, which
        /// enables a mod for the player by editing <c>ClientSettings</c> directly. Added in v4.
        /// <para>
        /// <c>null</c> means "no baseline yet" and is NOT the empty list: with no baseline there is
        /// nothing to attribute a difference to, so <c>ProfileStore.ReconcileActiveFromClientSettings</c>
        /// records one and changes nothing. It has to be persisted rather than kept per session,
        /// because an external edit can happen before the first reconciliation of a session — or in a
        /// session that never reconciled at all.
        /// </para>
        /// </summary>
        public List<string> LastAppliedClientDisabled;

        /// <summary>
        /// Hosts the player chose to "always trust" for a card's "Open mod page" button - lower-case
        /// DNS names, exact match, e.g. <c>mods.vintagestory.at</c>. A mod page on a listed host opens
        /// in the browser without a confirmation. Consulted ONLY for mod page addresses BetterMods
        /// built itself (<c>ModPageLinks</c>), never for any other link. Removing a host from this list
        /// (or the whole key) brings the confirmation back. Added in v5.
        /// </summary>
        public List<string> TrustedLinkHosts = new List<string>();
    }
}
