using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.Common;

namespace BetterMods.Profiles
{
    /// <summary>What one probe of a <c>.vcdbs</c> told us. Never throws; check <see cref="Ok"/>.</summary>
    public class SavegameFileInfo
    {
        /// <summary>True only when a savegame was actually read out of the file.</summary>
        public bool Ok;

        /// <summary>The savegame's own GUID, or null.</summary>
        public string SavegameId;

        /// <summary>World name as stored, for logging.</summary>
        public string WorldName;

        /// <summary>Profile id read from the in-savegame mirror, or null.</summary>
        public string MirroredProfileId;

        /// <summary>Profile name read from the in-savegame mirror, or null.</summary>
        public string MirroredProfileName;

        /// <summary>
        /// Mod list read from the in-savegame mirror, or null when the savegame carries none. This is
        /// what lets a world copied from another machine still say what it loaded with, where the
        /// local <c>bettermods.json</c> has never seen it.
        /// </summary>
        public List<string> MirroredModList;

        /// <summary>True when the file could not be opened for writing — never mirror into it then.</summary>
        public bool IsReadOnly = true;

        /// <summary>
        /// True when the savegame is on an older database version than this build of the game, i.e.
        /// the world is about to be upgraded. Never write into such a file: vanilla is about to
        /// rewrite it, and the player has just been asked to confirm a one-way conversion.
        /// </summary>
        public bool NeedsUpgrade;
    }

    /// <summary>
    /// Reads and writes the profile mark inside a savegame, from the main menu, without loading the
    /// world. Vanilla does exactly this round trip in <c>GuiScreenSingleplayerModify.OnSave</c>
    /// (VintagestoryLib, Vintagestory.Client/GuiScreenSingleplayerModify.cs:271-313) and probes every
    /// save the same way when it builds the world list (<c>GuiScreenSingleplayer.LoadSaveGames</c>,
    /// :372-403), so the shape here is the game's own.
    /// <para>
    /// The mark is stored as raw UTF-8 JSON through <c>SaveGame.StoreData(string, byte[])</c> rather
    /// than the generic <c>StoreData&lt;T&gt;</c>, which would push a bare string through protobuf-net
    /// as a root type. It carries the profile NAME as well as its id so a world copied to another
    /// machine can still say what it wants even though the id means nothing there, and the observed
    /// mod list, which means something everywhere.
    /// </para>
    /// <para>
    /// Everything here is best effort: a failed read means "no opinion" and a failed write is logged
    /// and forgotten. A savegame must never fail to start because of this.
    /// </para>
    /// </summary>
    public static class SavegameFile
    {
        /// <summary>Key the mark lives under in <c>SaveGame.ModData</c>.</summary>
        public const string ModDataKey = "bettermods:profile";

        private class ProfileMark
        {
            public string ProfileId;
            public string ProfileName;

            /// <summary>
            /// The mods the world was observed to load with, <c>"modid@version"</c>, core mods
            /// excluded. Absent in a mark written before schema v3; null therefore means "unknown",
            /// never "none".
            /// </summary>
            public List<string> ModList;
        }

        /// <summary>
        /// Probes a savegame for its identifier and our mark. Returns a result with
        /// <see cref="SavegameFileInfo.Ok"/> false for a path that does not exist yet (a world about
        /// to be created), for a corrupt file, or for one we could not open at all.
        /// </summary>
        public static SavegameFileInfo Read(string saveFilePath)
        {
            SavegameFileInfo info = new SavegameFileInfo();

            if (string.IsNullOrWhiteSpace(saveFilePath)) return info;

            try
            {
                // Never probe a file that is not there: ProbeOpenConnection CREATES the database when
                // the path does not exist (GameDatabase.cs:82-88), which would drop an empty .vcdbs
                // in front of the world that is about to be generated.
                if (!File.Exists(saveFilePath)) return info;
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not stat '" + saveFilePath + "': " + e.Message);
                return info;
            }

            ILogger logger = BetterModsModSystem.Logger;
            if (logger == null) return info;

            GameDatabase db = null;
            try
            {
                db = new GameDatabase(logger);

                // Write access first, which is exactly what vanilla asks for when it probes every save
                // to build the world list, and is therefore the path the game already exercises on
                // this file. It throws for a locked or unwritable save, so retry read-only: that still
                // answers "which profile does this world want" for a world open somewhere else.
                SaveGame savegame = Probe(db, saveFilePath, requireWrite: true, write: false, out bool isReadOnly, out int foundVersion);
                if (savegame == null)
                {
                    db.Dispose();
                    db = new GameDatabase(logger);
                    savegame = Probe(db, saveFilePath, requireWrite: false, write: false, out isReadOnly, out foundVersion);
                    if (savegame != null) isReadOnly = true;
                }

                info.IsReadOnly = isReadOnly;
                info.NeedsUpgrade = savegame != null && foundVersion > 0 && foundVersion < GameVersion.DatabaseVersion;
                if (savegame == null) return info;

                info.Ok = true;
                info.SavegameId = savegame.SavegameIdentifier;
                info.WorldName = savegame.WorldName;

                ProfileMark mark = ReadMark(savegame);
                if (mark != null)
                {
                    info.MirroredProfileId = string.IsNullOrWhiteSpace(mark.ProfileId) ? null : mark.ProfileId.Trim();
                    info.MirroredProfileName = mark.ProfileName;
                    info.MirroredModList = mark.ModList;
                }
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not read savegame '" + saveFilePath + "': " + e.Message);
            }
            finally
            {
                Close(db);
            }

            return info;
        }

        /// <summary>
        /// Writes the mark into the savegame so the binding and the observed mod list travel with a
        /// copied world.
        /// <para>
        /// A null <paramref name="modList"/> means "I have nothing to say about it" and leaves
        /// whatever the savegame already carries alone — the list is only ever observed in-game,
        /// where the world is open and this file cannot be written, so the menu-side mirror is often
        /// writing a profile change while knowing nothing new about the mods.
        /// </para>
        /// <para>
        /// This also persists the savegame identifier as a side effect, which is the fix for legacy
        /// saves: <c>SaveGame.afterDeserialization</c> (SaveGame.cs:346,:370-373) mints a FRESH random
        /// GUID whenever the stored field is null and never writes it back, so such a save appears to
        /// change identity on every probe. Storing the savegame here nails whatever GUID this probe
        /// produced into the file, and it is stable from then on.
        /// </para>
        /// <para>
        /// Refuses to write a world whose database version is older than this build's: that world is
        /// about to be converted by vanilla and the player has just confirmed a one-way upgrade.
        /// Corrupt worlds are refused for free — a probe that cannot read a savegame returns null.
        /// </para>
        /// <para>
        /// Deliberately does NOT call the (internal) <c>SaveGame.WillSave</c>: that would restamp
        /// <c>LastPlayed</c> and <c>LastSavedGameVersion</c> and re-encode <c>WorldConfigBytes</c>
        /// from the runtime tree. We never call <c>LoadWorldConfig</c> either, so the world config
        /// bytes round-trip untouched and version-gated migrations still see the original version.
        /// </para>
        /// </summary>
        public static bool WriteMark(string saveFilePath, string profileId, string profileName, List<string> modList = null)
        {
            if (string.IsNullOrWhiteSpace(saveFilePath) || string.IsNullOrWhiteSpace(profileId)) return false;

            ILogger logger = BetterModsModSystem.Logger;
            if (logger == null) return false;

            GameDatabase db = null;
            try
            {
                if (!File.Exists(saveFilePath)) return false;

                db = new GameDatabase(logger);

                SaveGame savegame = Probe(db, saveFilePath, requireWrite: true, write: true, out bool isReadOnly, out int foundVersion);
                if (savegame == null || isReadOnly) return false;

                // An older database version means vanilla is about to convert this world. Do not
                // write into a file that is mid-migration for the sake of a convenience field.
                if (foundVersion > 0 && foundVersion < GameVersion.DatabaseVersion)
                {
                    BetterModsModSystem.Logger?.Notification(
                        "[BetterMods] Not mirroring the profile into '" + saveFilePath + "': it is still on database version " +
                        foundVersion + " and the game is about to upgrade it.");
                    return false;
                }

                ProfileMark mark = new ProfileMark
                {
                    ProfileId = profileId,
                    ProfileName = profileName ?? "",
                    ModList = modList ?? ReadMark(savegame)?.ModList
                };

                string json = JsonConvert.SerializeObject(mark);
                savegame.StoreData(ModDataKey, Encoding.UTF8.GetBytes(json));
                db.StoreSaveGame(savegame);
                return true;
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not mirror the profile binding into '" + saveFilePath + "': " + e.Message);
                return false;
            }
            finally
            {
                Close(db);
            }
        }

        /// <summary>
        /// Opens the savegame.
        /// <para>
        /// <paramref name="write"/> decides the journalling mode, and it matters. With it false the
        /// connection is opened the way vanilla probes a save it is only going to read
        /// (<c>corruptionProtection: false</c>, i.e. <c>PRAGMA journal_mode=Off</c>) — fine, because
        /// nothing is written. With it true the connection is opened the way the SERVER opens a save
        /// it is going to write (<c>corruptionProtection: true</c>, i.e. WAL + synchronous=Normal;
        /// ServerSystemLoadAndSaveGame.cs:84,96,146). Writing our mark with journalling off is a
        /// crash away from a damaged world for the sake of a convenience field.
        /// </para>
        /// </summary>
        private static SaveGame Probe(
            GameDatabase db, string saveFilePath, bool requireWrite, bool write, out bool isReadOnly, out int foundVersion)
        {
            isReadOnly = true;
            foundVersion = 0;

            try
            {
                return db.ProbeOpenConnection(
                    saveFilePath, corruptionProtection: write, out foundVersion, out string _, out isReadOnly, requireWrite);
            }
            catch (Exception)
            {
                // A locked or unwritable file throws straight out of SQLiteDBConnection.OpenOrCreate
                // (VintagestoryAPI, Vintagestory.API.Common/SQLiteDBConnection.cs:38-45). Vanilla
                // swallows it too (GuiScreenSingleplayer.cs:382-390).
                return null;
            }
        }

        private static ProfileMark ReadMark(SaveGame savegame)
        {
            try
            {
                byte[] raw = savegame.GetData(ModDataKey);
                if (raw == null || raw.Length == 0) return null;

                return JsonConvert.DeserializeObject<ProfileMark>(Encoding.UTF8.GetString(raw));
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] Ignoring an unreadable profile mark in a savegame: " + e.Message);
                return null;
            }
        }

        private static void Close(GameDatabase db)
        {
            try
            {
                db?.Dispose();
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not close a savegame connection: " + e.Message);
            }
        }
    }
}
