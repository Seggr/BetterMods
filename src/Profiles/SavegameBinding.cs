using System.Collections.Generic;

namespace BetterMods.Profiles
{
    /// <summary>
    /// What we know about one savegame: which profile it wants, and which mods it actually loaded
    /// with. Persisted in <c>bettermods.json</c> (schema v3) and mirrored into the savegame's own
    /// <c>ModData</c> by <see cref="SavegameFile"/>.
    /// <para>
    /// The two signals answer different questions. <see cref="ProfileId"/> is what the player CHOSE;
    /// <see cref="ModList"/> is what the world actually NEEDS, observed rather than declared, and is
    /// therefore the stronger of the two.
    /// </para>
    /// </summary>
    public class SavegameBinding
    {
        /// <summary>
        /// <c>SaveGame.SavegameIdentifier</c> — a GUID minted when the world is created and stored
        /// inside the <c>.vcdbs</c>, so it survives a rename or a move. This is the primary key.
        /// </summary>
        public string SavegameId = "";

        /// <summary><see cref="ModProfile.Id"/> of the profile this world should be played with.</summary>
        public string ProfileId = "";

        /// <summary>
        /// Full path of the <c>.vcdbs</c> the binding was last seen at. Secondary key only: it is
        /// what rescues a legacy save whose stored identifier is null and therefore re-mints itself
        /// on every probe (see <see cref="SavegameFile"/>). Never authoritative — a copied world
        /// keeps the identifier and gets a new path.
        /// </summary>
        public string SaveFilePath = "";

        /// <summary>
        /// Last known world name. Never used for matching, never read back by any code path: it is
        /// there so that a human opening <c>bettermods.json</c> can tell the bindings apart, which is
        /// otherwise a list of GUIDs and absolute paths. Every log line that names a world uses the
        /// name the probe just read (<c>SavegameFileInfo.WorldName</c>), which cannot be stale.
        /// <para>
        /// It is still LOAD-BEARING for persistence: <c>ProfileStore.BindSavegame</c> folds it into
        /// the same "did anything change" flag as the other fields, so renaming a world is what makes
        /// the binding dirty and gets the new name written.
        /// </para>
        /// </summary>
        public string WorldName = "";

        /// <summary>
        /// The mods this world was last seen to load with, as <c>"modid@version"</c>, core mods
        /// excluded. Added in schema v3.
        /// <para>
        /// <c>null</c> and empty are DIFFERENT and both meaningful: null is "never observed" (a v2
        /// file, or a world that has not been played since this feature landed), empty is "observed,
        /// and it loaded no non-core mods". Nothing may normalise null to empty — that would make
        /// every unobserved world look like an exact match for an empty profile.
        /// </para>
        /// <para>
        /// Written by the in-game recorder once the world is actually up; the latest successful load
        /// wins outright. See <see cref="SavegameProfileGate.OnWorldStarted"/>.
        /// </para>
        /// </summary>
        public List<string> ModList;
    }
}
