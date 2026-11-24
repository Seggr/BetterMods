using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BetterMods.Updates;
using Newtonsoft.Json;
using Vintagestory.API.Config;
using Vintagestory.Client.NoObf;
using Vintagestory.ModDb;

namespace BetterMods.Profiles
{
    /// <summary>
    /// Owns the profile list and its persistence.
    /// <para>
    /// Persistence is deliberately plain <see cref="File"/> IO + Newtonsoft, NOT the mod-config API:
    /// in the main menu the <c>ICoreAPI</c> is a <c>MainMenuAPI</c>, whose <c>LoadModConfig</c>,
    /// <c>StoreModConfig</c> and <c>GetOrCreateDataPath</c> all throw <see cref="NotImplementedException"/>
    /// (VintagestoryLib, Vintagestory.Client.Gui/MainMenuAPI.cs:483-491,523-531). Everything here must be
    /// callable from both the main menu and in-game, so it touches only static game state.
    /// </para>
    /// <para>
    /// Nothing in this class throws into the GUI: every failure is logged and degrades to defaults.
    /// </para>
    /// </summary>
    public static class ProfileStore
    {
        public const string ConfigFileName = "bettermods.json";

        private static readonly object Lock = new object();

        /// <summary>How long <see cref="EnsureLoaded"/> waits before retrying an unreadable file.</summary>
        private const int RetryIntervalMs = 5000;

        private static BetterModsConfig config = new BetterModsConfig();
        private static bool loaded;
        private static long lastLoadAttempt;

        /// <summary>What the last <see cref="Save"/> wrote, so an unchanged config is not rewritten.</summary>
        private static string lastWrittenJson;

        /// <summary>Set while suspended so the "not writing" error is logged once, not per save.</summary>
        private static bool suspensionLogged;

        /// <summary>
        /// True when the config file is present but unreadable, so everything the player does this
        /// session is in memory only. The GUI may surface this; nothing here throws because of it.
        /// <para>
        /// This is the ONLY thing anything asks about how the last <see cref="Load"/> ended, which is
        /// why it is a flag and not a status enum: "read", "seeded fresh" and "recovered from the
        /// backup" all mean the same thing to every caller — the file is usable and writing is safe.
        /// The three cases still differ INSIDE <see cref="Load"/>, where recovery is what decides
        /// whether the repaired config is written straight back out.
        /// </para>
        /// </summary>
        public static bool PersistenceSuspended { get; private set; }

        /// <summary>Full path of the config file: <c>&lt;data&gt;/ModConfig/bettermods.json</c>.</summary>
        public static string ConfigFilePath => Path.Combine(GamePaths.ModConfig, ConfigFileName);

        /// <summary>The copy <see cref="Save"/> leaves behind, and the first thing a bad read falls back to.</summary>
        public static string BackupFilePath => ConfigFilePath + ".bak";

        /// <summary>All profiles, in creation order. The returned list is a snapshot copy.</summary>
        public static List<ModProfile> Profiles
        {
            get
            {
                lock (Lock)
                {
                    EnsureLoaded();
                    return new List<ModProfile>(config.Profiles);
                }
            }
        }

        /// <summary>
        /// The currently active profile. Never null once <see cref="Load"/> has run — the store
        /// self-heals by activating the first profile, or creating one, if the id is dangling.
        /// </summary>
        public static ModProfile ActiveProfile
        {
            get
            {
                lock (Lock)
                {
                    EnsureLoaded();
                    return ResolveActive();
                }
            }
        }

        // ---------------------------------------------------------------- load / save

        /// <summary>
        /// Reads the config file, or seeds a first-run config from the current
        /// <c>ClientSettings.DisabledMods</c>. Safe to call repeatedly; safe to call before the
        /// game finished starting.
        /// <para>
        /// "The file is not there" and "the file is there and I could not read it" are deliberately
        /// NOT the same case. The first is a first run and is seeded and written. The second is a
        /// lock, an antivirus scan or a permission problem on a file that still holds every profile
        /// the player has: it degrades to an in-memory config, SUSPENDS all writing so nothing can
        /// overwrite it, and is retried by <see cref="EnsureLoaded"/>. A file that is present but
        /// whose CONTENT will not parse is a third case: the original is preserved as
        /// <c>bettermods.json.corrupt</c> and the <c>.bak</c> left by the last successful save is
        /// tried before anything is seeded.
        /// </para>
        /// </summary>
        private static void Load()
        {
            lock (Lock)
            {
                lastLoadAttempt = Environment.TickCount64;

                string path = ConfigFilePath;
                ReadOutcome outcome = ReadConfigFile(path, out BetterModsConfig read);

                if (outcome == ReadOutcome.Unreadable)
                {
                    // The file is presumed intact. Give the GUI something usable, write NOTHING.
                    Suspend();
                    config = new BetterModsConfig();
                    Normalise();
                    SeedIfEmpty();
                    ResolveActive();
                    loaded = false;
                    return;
                }

                bool recovered = false;

                if (outcome == ReadOutcome.Corrupt)
                {
                    if (ReadConfigFile(BackupFilePath, out BetterModsConfig backup) == ReadOutcome.Ok)
                    {
                        BetterModsModSystem.Logger?.Warning(
                            "[BetterMods] " + path + " could not be parsed; recovering from " + BackupFilePath + ".");
                        read = backup;
                    }

                    // Never seed over a file we could not parse — the bytes are the player's only
                    // remaining copy. If it cannot even be moved aside, treat that as unreadable.
                    if (!PreserveCorruptFile(path))
                    {
                        Suspend();
                        config = read ?? new BetterModsConfig();
                        Normalise();
                        SeedIfEmpty();
                        ResolveActive();
                        loaded = false;
                        return;
                    }

                    recovered = true;
                }

                config = read ?? new BetterModsConfig();

                bool migrated = Migrate();
                Normalise();
                bool seeded = SeedIfEmpty();
                ResolveActive();

                // Read, seeded or recovered — all three mean the file is usable again, so a previous
                // suspension is lifted here and writing resumes.
                PersistenceSuspended = false;

                loaded = true;
                suspensionLogged = false;
                lastWrittenJson = null;

                if (recovered || migrated || seeded) Save();
                else lastWrittenJson = Serialize();
            }
        }

        /// <summary>What one attempt at reading a config file produced.</summary>
        private enum ReadOutcome
        {
            Ok,

            /// <summary>No such file. Safe to seed.</summary>
            Missing,

            /// <summary>The bytes could not be obtained. NOT safe to seed.</summary>
            Unreadable,

            /// <summary>The bytes were obtained and are not a config. Safe to seed once preserved.</summary>
            Corrupt
        }

        private static ReadOutcome ReadConfigFile(string path, out BetterModsConfig result)
        {
            result = null;

            string json;
            try
            {
                if (!File.Exists(path)) return ReadOutcome.Missing;
                json = File.ReadAllText(path);
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Error("[BetterMods] Could not read " + path + ": " + e.Message);
                return ReadOutcome.Unreadable;
            }

            // A zero-length file is what an interrupted write leaves behind, not a valid config.
            if (string.IsNullOrWhiteSpace(json)) return ReadOutcome.Corrupt;

            try
            {
                result = JsonConvert.DeserializeObject<BetterModsConfig>(json);
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Error("[BetterMods] " + path + " is not readable as a config: " + e.Message);
                return ReadOutcome.Corrupt;
            }

            return result != null ? ReadOutcome.Ok : ReadOutcome.Corrupt;
        }

        /// <summary>
        /// Moves an unparseable config out of the way so the recovery that follows cannot destroy it.
        /// Returns false when it could not be moved, which means nothing may be written at all.
        /// </summary>
        private static bool PreserveCorruptFile(string path)
        {
            try
            {
                string target = path + ".corrupt";
                if (File.Exists(target))
                {
                    // Keep the first one: it is the closest to whatever went wrong.
                    target = path + "." + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".corrupt";
                }

                File.Move(path, target);
                BetterModsModSystem.Logger?.Error("[BetterMods] Preserved the unreadable config as " + target + ".");
                return true;
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Error(
                    "[BetterMods] Could not preserve the unreadable config " + path + ": " + e.Message);
                return false;
            }
        }

        /// <summary>Enters the "the file is there, we cannot read it, touch nothing" state.</summary>
        private static void Suspend()
        {
            PersistenceSuspended = true;
            lastWrittenJson = null;

            if (suspensionLogged) return;
            suspensionLogged = true;

            BetterModsModSystem.Logger?.Error(
                "[BetterMods] " + ConfigFilePath + " exists but could not be read. Profiles are running from memory " +
                "and NOTHING will be written until it can be read again, so the file cannot be overwritten by defaults.");
        }

        /// <summary>Drops structurally broken entries rather than letting them poison the GUI.</summary>
        private static void Normalise()
        {
            if (config.Profiles == null) config.Profiles = new List<ModProfile>();
            config.Profiles.RemoveAll(p => p == null);

            foreach (ModProfile p in config.Profiles)
            {
                if (string.IsNullOrWhiteSpace(p.Id)) p.Id = Guid.NewGuid().ToString();
                if (p.Name == null) p.Name = "";
                if (p.DisabledMods == null) p.DisabledMods = new List<string>();
            }
        }

        /// <summary>First run: one profile seeded from whatever the player already has disabled.</summary>
        private static bool SeedIfEmpty()
        {
            if (config.Profiles.Count > 0) return false;

            List<string> disabled = ReadClientDisabledMods();

            ModProfile seed = new ModProfile("Profile 1", disabled);
            config.Profiles.Add(seed);
            config.ActiveProfileId = seed.Id;

            // The seed IS the current setting, so this is a baseline we know to be true — which means
            // the very first session can already tell an outside change from one of ours.
            SetBaseline(disabled);
            return true;
        }

        /// <summary>
        /// Brings a config read off disk up to <see cref="BetterModsConfig.CurrentVersion"/>.
        /// Returns true when something actually changed, so <see cref="Load"/> can write it back.
        /// <para>
        /// v1 → v2 added <see cref="BetterModsConfig.SavegameBindings"/>; v2 → v3 added
        /// <see cref="SavegameBinding.ModList"/>; v3 → v4 added
        /// <see cref="BetterModsConfig.LastAppliedClientDisabled"/> and the bare-<c>modid</c> disabled
        /// entry; v4 → v5 added <see cref="BetterModsConfig.TrustedLinkHosts"/>. All are additive: the older file has no such key, so Newtonsoft leaves the field at
        /// its initialiser and there is nothing to convert. The only real work is tolerating an
        /// explicit <c>null</c> and dropping broken entries. Older files therefore keep working
        /// untouched — a v3 file simply has no baseline (so the first reconciliation only records one)
        /// and no bare ids (so every surface behaves exactly as it did).
        /// </para>
        /// <para>
        /// A null <c>ModList</c> is left null on purpose — it is the "never observed" state and is not
        /// the same thing as an observed empty list.
        /// </para>
        /// </summary>
        private static bool Migrate()
        {
            bool changed = false;

            if (config.SavegameBindings == null)
            {
                config.SavegameBindings = new List<SavegameBinding>();
                changed = true;
            }

            int removed = config.SavegameBindings.RemoveAll(b =>
                b == null || string.IsNullOrWhiteSpace(b.ProfileId) ||
                (string.IsNullOrWhiteSpace(b.SavegameId) && string.IsNullOrWhiteSpace(b.SaveFilePath)));
            if (removed > 0) changed = true;

            foreach (SavegameBinding binding in config.SavegameBindings)
            {
                if (binding.ModList == null) continue;
                if (binding.ModList.RemoveAll(string.IsNullOrWhiteSpace) > 0) changed = true;
            }

            // Null is "no baseline" and must survive; an empty-but-present one is a real answer.
            if (config.LastAppliedClientDisabled != null &&
                config.LastAppliedClientDisabled.RemoveAll(string.IsNullOrWhiteSpace) > 0)
            {
                changed = true;
            }

            // v5. Absent (an older file) or null is "trusts nothing"; anything that is not a plain
            // lower-case host name is dropped rather than half-honoured.
            List<string> hosts = CleanTrustedHosts(config.TrustedLinkHosts);
            if (config.TrustedLinkHosts == null || !hosts.SequenceEqual(config.TrustedLinkHosts, StringComparer.Ordinal))
            {
                config.TrustedLinkHosts = hosts;
                changed = true;
            }

            if (config.Version != BetterModsConfig.CurrentVersion)
            {
                config.Version = BetterModsConfig.CurrentVersion;
                changed = true;
            }

            return changed;
        }

        private static List<string> CleanTrustedHosts(IEnumerable<string> hosts)
        {
            List<string> cleaned = new List<string>();
            if (hosts == null) return cleaned;

            foreach (string raw in hosts)
            {
                string host = raw?.Trim().ToLowerInvariant();
                if (!ModUpdateRules.IsTrustableHostName(host) || cleaned.Contains(host)) continue;
                cleaned.Add(host);
            }

            return cleaned;
        }

        // ---------------------------------------------------------------- trusted link hosts

        /// <summary>
        /// True when the player chose "always trust" for <paramref name="host"/> (exact, ordinal match on
        /// the lower-case name). Only <c>ModPageLinks</c> asks, and only for a page address it built and
        /// re-validated itself.
        /// </summary>
        public static bool IsTrustedLinkHost(string host)
        {
            if (string.IsNullOrEmpty(host)) return false;

            lock (Lock)
            {
                EnsureLoaded();
                return config.TrustedLinkHosts != null && config.TrustedLinkHosts.Contains(host, StringComparer.Ordinal);
            }
        }

        /// <summary>
        /// Remembers <paramref name="host"/> as trusted and saves. False (nothing stored) for anything that
        /// is not a plain lower-case DNS name. While persistence is suspended the trust lasts for the
        /// session only, like every other change.
        /// </summary>
        public static bool TrustLinkHost(string host)
        {
            if (!ModUpdateRules.IsTrustableHostName(host)) return false;

            lock (Lock)
            {
                EnsureLoaded();
                config.TrustedLinkHosts ??= new List<string>();
                if (!config.TrustedLinkHosts.Contains(host, StringComparer.Ordinal)) config.TrustedLinkHosts.Add(host);
                Save();
                return true;
            }
        }

        /// <summary>
        /// Writes the config via a flushed temp file + replace, so neither a crash mid-write nor a
        /// power loss can leave a truncated or zero-length config behind. Returns false on failure
        /// (logged, never thrown).
        /// <para>
        /// Three things make this safe rather than merely tidy. The temp file is flushed all the way
        /// THROUGH the OS cache before it is swapped in, so the replace cannot publish a file whose
        /// bytes are still in flight. The replace keeps the outgoing file as <c>.bak</c>, which is
        /// what <see cref="Load"/> recovers from. And the fallback for a filesystem that refuses
        /// <see cref="File.Replace"/> copies before it deletes and restores if the move fails, so
        /// there is no window in which the config simply does not exist.
        /// </para>
        /// <para>
        /// A save whose content is byte-identical to the last one is skipped. Vanilla runs
        /// <c>LoadModCells</c> twice per rebuild of the mods screen and every rebuild re-resolves
        /// every version group, so without this a tab switch is several full rewrites of a file
        /// nothing changed in.
        /// </para>
        /// </summary>
        public static bool Save()
        {
            lock (Lock)
            {
                if (PersistenceSuspended)
                {
                    // Logged once by Suspend(); saying it again per save would drown the log.
                    return false;
                }

                string path = ConfigFilePath;

                try
                {
                    string json = Serialize();
                    if (json == null) return false;
                    if (string.Equals(json, lastWrittenJson, StringComparison.Ordinal)) return true;

                    string dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                    string tmp = path + ".tmp";
                    WriteFlushed(tmp, json);

                    if (File.Exists(path)) Swap(tmp, path);
                    else File.Move(tmp, path);

                    lastWrittenJson = json;
                    return true;
                }
                catch (Exception e)
                {
                    BetterModsModSystem.Logger?.Error("[BetterMods] Could not write " + path + ".");
                    BetterModsModSystem.Logger?.Error(e);
                    lastWrittenJson = null;
                    return false;
                }
            }
        }

        private static string Serialize()
        {
            try
            {
                return JsonConvert.SerializeObject(config, Formatting.Indented);
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Error("[BetterMods] Could not serialise the profile config.");
                BetterModsModSystem.Logger?.Error(e);
                return null;
            }
        }

        /// <summary>
        /// Writes a file and does not return until the bytes are on the DEVICE.
        /// <c>File.WriteAllText</c> only guarantees they reached the OS, which is not enough when the
        /// very next thing we do is publish the file as the config.
        /// </summary>
        private static void WriteFlushed(string path, string json)
        {
            using (FileStream stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                byte[] bytes = new UTF8Encoding(false).GetBytes(json);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }
        }

        /// <summary>
        /// Puts <paramref name="tmp"/> in place of <paramref name="path"/>, keeping the outgoing file
        /// as <see cref="BackupFilePath"/> and never leaving <paramref name="path"/> missing.
        /// </summary>
        private static void Swap(string tmp, string path)
        {
            string backup = BackupFilePath;

            try
            {
                // Atomic on NTFS, and it produces the .bak for free.
                File.Replace(tmp, path, backup, ignoreMetadataErrors: true);
                return;
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning(
                    "[BetterMods] Atomic replace of " + path + " failed (" + e.Message + "); falling back to copy + move.");
            }

            // Copy FIRST: from here on the old config exists in two places, so the delete below can
            // never be the last thing that held it.
            bool haveBackup;
            try
            {
                File.Copy(path, backup, overwrite: true);
                haveBackup = true;
            }
            catch (Exception e)
            {
                haveBackup = false;
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not back up " + path + ": " + e.Message);
            }

            try
            {
                File.Delete(path);
                File.Move(tmp, path);
            }
            catch (Exception)
            {
                if (!File.Exists(path) && haveBackup)
                {
                    // The move failed and the target is gone. Put the old config back rather than
                    // leave the player with nothing.
                    try
                    {
                        File.Copy(backup, path, overwrite: true);
                        BetterModsModSystem.Logger?.Error(
                            "[BetterMods] Could not install the new config; the previous one was restored from " + backup + ".");
                    }
                    catch (Exception restoreFailed)
                    {
                        BetterModsModSystem.Logger?.Error(
                            "[BetterMods] Could not restore " + path + " from " + backup + ": " + restoreFailed.Message);
                    }
                }

                throw;
            }
        }

        // ---------------------------------------------------------------- profile CRUD

        /// <summary>
        /// Creates a profile seeded by cloning another profile's disabled set —
        /// <paramref name="cloneFromId"/> when it names a known profile, the active profile
        /// otherwise. A null/blank <paramref name="name"/> auto-names it "Profile N" for the
        /// lowest free N.
        /// </summary>
        public static ModProfile CreateProfile(string name, string cloneFromId = null)
        {
            lock (Lock)
            {
                EnsureLoaded();

                ModProfile source = Find(cloneFromId) ?? ResolveActive();
                string finalName = string.IsNullOrWhiteSpace(name) ? NextAutoName() : name.Trim();

                ModProfile created = new ModProfile(finalName, source?.DisabledMods);
                config.Profiles.Add(created);
                Save();
                return created;
            }
        }

        /// <summary>
        /// Renames a profile. The name is trimmed; blank names are rejected. Duplicate names are
        /// allowed on purpose — the GUID is the identity.
        /// </summary>
        public static bool RenameProfile(string id, string newName)
        {
            lock (Lock)
            {
                EnsureLoaded();

                if (string.IsNullOrWhiteSpace(newName)) return false;

                ModProfile profile = Find(id);
                if (profile == null) return false;

                profile.Name = newName.Trim();
                Save();
                return true;
            }
        }

        /// <summary>
        /// Deletes a profile. Refuses to delete the last remaining one.
        /// <para>
        /// Deleting the ACTIVE profile promotes the first remaining one AND applies it, because the
        /// alternative is a game that boots the deleted profile's mod set while every surface shows
        /// the promoted one as active. Repointing the id without pushing the new disabled set is the
        /// one state this store must never be left in, and no caller can be relied on to finish the
        /// job — so it is done here rather than left to <see cref="ApplyProfile"/>.
        /// </para>
        /// <para>
        /// Savegame bindings pointing at the deleted profile go with it. They were already inert —
        /// <see cref="GetBoundProfileId"/> reads a dangling binding as unbound — so this only stops
        /// the file growing junk that every lookup then has to scan past.
        /// </para>
        /// </summary>
        public static bool DeleteProfile(string id)
        {
            lock (Lock)
            {
                EnsureLoaded();

                if (config.Profiles.Count <= 1) return false;

                ModProfile profile = Find(id);
                if (profile == null) return false;

                config.Profiles.Remove(profile);

                if (config.SavegameBindings != null)
                {
                    config.SavegameBindings.RemoveAll(
                        b => b != null && string.Equals(b.ProfileId, profile.Id, StringComparison.Ordinal));
                }

                if (config.ActiveProfileId == profile.Id)
                {
                    ApplyInternal(config.Profiles[0]);
                }

                Save();
                return true;
            }
        }

        /// <summary>
        /// Returns the profile with the given id, or null. The returned instance is the live one —
        /// callers must not mutate it; use the methods on this class instead.
        /// </summary>
        public static ModProfile FindProfile(string id)
        {
            lock (Lock)
            {
                EnsureLoaded();
                return Find(id);
            }
        }

        /// <summary>
        /// Snapshot of a profile's disabled set, in <c>"modid@version"</c> form. Returns an empty
        /// list for an unknown id, which reads as "everything enabled" — the safe direction.
        /// </summary>
        public static List<string> GetDisabledMods(string id)
        {
            lock (Lock)
            {
                EnsureLoaded();

                ModProfile profile = Find(id);
                return profile != null ? new List<string>(profile.DisabledMods) : new List<string>();
            }
        }

        // ---------------------------------------------------------------- the core operation

        /// <summary>
        /// Makes the profile active and pushes its disabled set into <c>ClientSettings.DisabledMods</c>,
        /// then saves client settings.
        /// <para>
        /// Blocked mods: vanilla enforces the blocklist at load/display time via
        /// <c>ModDbUtil.IsModBlocked</c> (ModLoader.cs:154, GuiScreenMods.cs:93-95), NOT by persisting
        /// blocked ids into <c>DisabledMods</c> — so in practice there is normally nothing to preserve.
        /// We are defensive anyway: any entry currently in <c>ClientSettings.DisabledMods</c> whose
        /// mod is on the blocklist is unioned into the result, so applying a profile can never
        /// silently un-block a blocked mod.
        /// </para>
        /// <para>
        /// Entries are always written in <c>"modid@version"</c> form. <c>ModLoader.DisableMods</c>
        /// matches bare ids too (ModLoader.cs:325), but the mods screen ON/OFF renderer only tests
        /// <c>"modid@version"</c> (GuiScreenMods.cs:110,131), so a bare id would desync the UI.
        /// </para>
        /// <para>
        /// The "one version on, siblings off" invariant needs nothing here: because entries are
        /// per-version, a profile that obeys it already lists every sibling, and this method copies
        /// the set verbatim. The invariant is established on write — <see cref="SetVersionSelection"/>
        /// for every toggle and version pick, and <c>ModGrouping.Build</c>, which normalises a
        /// legacy or freshly cloned profile the first time either surface displays it.
        /// </para>
        /// </summary>
        public static void ApplyProfile(string id)
        {
            lock (Lock)
            {
                EnsureLoaded();

                ModProfile profile = Find(id);
                if (profile == null) return;

                // Whatever the game changed behind our back belongs to the profile that is active
                // NOW, so it has to be collected before the active id moves.
                ReconcileActiveFromClientSettingsInternal();

                ApplyInternal(profile);
                Save();
            }
        }

        /// <summary>
        /// Makes <paramref name="profile"/> the active one and pushes its disabled set into
        /// <c>ClientSettings</c>. Caller holds the lock and is responsible for the <see cref="Save"/>.
        /// </summary>
        private static void ApplyInternal(ModProfile profile)
        {
            config.ActiveProfileId = profile.Id;

            List<string> current = ReadClientDisabledMods();

            List<string> target = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string entry in profile.DisabledMods)
            {
                if (string.IsNullOrWhiteSpace(entry)) continue;
                string normalized = entry.Trim();
                if (seen.Add(normalized)) target.Add(normalized);
            }

            // Union back anything vanilla has disabled that is actually blocked.
            foreach (string entry in current)
            {
                if (string.IsNullOrWhiteSpace(entry)) continue;
                string normalized = entry.Trim();
                if (seen.Contains(normalized)) continue;
                if (!IsBlockedEntry(normalized)) continue;
                if (seen.Add(normalized)) target.Add(normalized);
            }

            WriteClientDisabledMods(target);
        }

        // ---------------------------------------------------------------- live edits

        /// <summary>
        /// Records which version of one mod a profile uses, maintaining the "one version on, siblings
        /// off" invariant: every entry of <paramref name="allVersionKeys"/> ends up in the profile's
        /// disabled set except <paramref name="enabledKey"/>. A null <paramref name="enabledKey"/>
        /// switches the mod off entirely.
        /// <para>
        /// This is the only toggle primitive. A per-entry one cannot maintain the invariant, because
        /// enabling <c>"mymod@2.0"</c> is only meaningful together with the knowledge that
        /// <c>"mymod@1.0"</c> exists and has to go off — <c>ModLoader</c> cannot load both.
        /// </para>
        /// <para>
        /// Keys are <c>"modid@version"</c>. Does not touch <c>ClientSettings</c>; see
        /// <see cref="ApplyProfile"/> and <see cref="PushGroupToClientSettings"/> for that.
        /// Returns true if the profile actually changed. Unknown ids are ignored.
        /// </para>
        /// <para>
        /// Switching the mod OFF also writes the BARE mod id. That is the only way to be right about
        /// a copy of the mod the caller could not see: <c>allVersionKeys</c> is whatever the calling
        /// surface managed to enumerate - up to 1.22 the in-game scanner could not enumerate a loose
        /// <c>.cs</c> mod at all, and on 1.23 it still misses anything installed since the last
        /// "Reload Mods", or everything when <c>ScreenManager.allMods</c> is unreadable. A per-version disabled set
        /// would leave that copy loading while both surfaces render the mod as off.
        /// <c>ModLoader.DisableMods</c> matches a bare id against every version
        /// (<c>disabledSet.Contains(mod.Info.ModID)</c>, ModLoader.cs:325), so the bare entry covers
        /// the copies we never saw. It is written IN ADDITION to the per-version keys, never instead
        /// of them, so the mods screen's own ON/OFF renderer — which only ever tests
        /// <c>"modid@version"</c> — still gets the answer it needs for every row it drew, and it is
        /// removed again the moment any version is switched back on.
        /// </para>
        /// </summary>
        public static bool SetVersionSelection(string profileId, IEnumerable<string> allVersionKeys, string enabledKey)
        {
            lock (Lock)
            {
                EnsureLoaded();

                ModProfile profile = Find(profileId);
                if (profile == null) return false;

                bool changed = SetVersionSelectionInternal(profile, allVersionKeys, enabledKey);
                if (changed) Save();
                return changed;
            }
        }

        /// <summary>
        /// <see cref="SetVersionSelection"/> for several mods at once, with ONE save at the end.
        /// <para>
        /// <c>ModGrouping.Build</c> normalises every group it resolves, and both surfaces rebuild the
        /// whole list on every tab switch — vanilla even runs <c>LoadModCells</c> twice per rebuild —
        /// so a save per group turns opening the mods screen into a burst of full file rewrites.
        /// </para>
        /// </summary>
        public static bool SetVersionSelections(string profileId, IEnumerable<KeyValuePair<List<string>, string>> selections)
        {
            if (selections == null) return false;

            lock (Lock)
            {
                EnsureLoaded();

                ModProfile profile = Find(profileId);
                if (profile == null) return false;

                bool changed = false;
                foreach (KeyValuePair<List<string>, string> selection in selections)
                {
                    changed |= SetVersionSelectionInternal(profile, selection.Key, selection.Value);
                }

                if (changed) Save();
                return changed;
            }
        }

        /// <summary>The body of <see cref="SetVersionSelection"/>, without the save. Caller holds the lock.</summary>
        private static bool SetVersionSelectionInternal(ModProfile profile, IEnumerable<string> allVersionKeys, string enabledKey)
        {
            if (allVersionKeys == null) return false;

            string enabled = string.IsNullOrWhiteSpace(enabledKey) ? null : enabledKey.Trim();
            bool changed = false;
            string modId = null;

            foreach (string key in allVersionKeys)
            {
                if (string.IsNullOrWhiteSpace(key)) continue;

                string entry = key.Trim();
                if (modId == null)
                {
                    WorldModList.Split(entry, out string parsed, out _);
                    modId = parsed;
                }

                bool shouldBeDisabled = !string.Equals(entry, enabled, StringComparison.OrdinalIgnoreCase);
                changed |= SetEntry(profile, entry, shouldBeDisabled);
            }

            // The catch-all for versions this caller could not enumerate. Only meaningful when the
            // whole mod is off; any enabled version must clear it or the mod would never load again.
            if (!string.IsNullOrEmpty(modId)) changed |= SetEntry(profile, modId, enabled == null);

            return changed;
        }

        /// <summary>Adds or removes one entry from a profile's disabled set. Returns true if it moved.</summary>
        private static bool SetEntry(ModProfile profile, string entry, bool disabled)
        {
            bool isDisabled = profile.DisabledMods.Any(e => string.Equals(e, entry, StringComparison.OrdinalIgnoreCase));
            if (disabled == isDisabled) return false;

            if (disabled) profile.DisabledMods.Add(entry);
            else profile.DisabledMods.RemoveAll(e => string.Equals(e, entry, StringComparison.OrdinalIgnoreCase));

            return true;
        }

        /// <summary>
        /// Mirrors one mod's version selection straight into <c>ClientSettings.DisabledMods</c>,
        /// for when the profile being edited IS the active one and the player would otherwise see the
        /// menu disagree with what the game will load next. Same invariant as
        /// <see cref="SetVersionSelection"/>; a null <paramref name="enabledKey"/> disables all of them.
        /// </summary>
        public static void PushGroupToClientSettings(IEnumerable<string> allVersionKeys, string enabledKey)
        {
            if (allVersionKeys == null) return;

            lock (Lock)
            {
                List<string> disabled = ReadClientDisabledMods();
                ApplyGroupToDisabledList(disabled, allVersionKeys, enabledKey);
                WriteClientDisabledMods(disabled);
            }
        }

        /// <summary>
        /// The body of <see cref="PushGroupToClientSettings"/> against a list in hand: every key off
        /// except <paramref name="enabledKey"/>, plus the bare-id catch-all when the mod is off.
        /// </summary>
        private static void ApplyGroupToDisabledList(List<string> disabled, IEnumerable<string> allVersionKeys, string enabledKey)
        {
            string enabled = string.IsNullOrWhiteSpace(enabledKey) ? null : enabledKey.Trim();
            string modId = null;

            foreach (string key in allVersionKeys)
            {
                if (string.IsNullOrWhiteSpace(key)) continue;

                string entry = key.Trim();
                if (modId == null)
                {
                    WorldModList.Split(entry, out string parsed, out _);
                    modId = parsed;
                }

                disabled.RemoveAll(e => string.Equals(e, entry, StringComparison.OrdinalIgnoreCase));
                if (!string.Equals(entry, enabled, StringComparison.OrdinalIgnoreCase)) disabled.Add(entry);
            }

            // Same bare-id rule as SetVersionSelection, so the live setting and the profile that
            // produced it never disagree about a version neither of them could enumerate.
            if (!string.IsNullOrEmpty(modId))
            {
                string bare = modId;
                disabled.RemoveAll(e => string.Equals(e, bare, StringComparison.OrdinalIgnoreCase));
                if (enabled == null) disabled.Add(bare);
            }
        }

        /// <summary>
        /// Folds freshly installed mod versions into every profile in ONE transaction, so that only the
        /// profile the player was looking at switches to them.
        /// <para>
        /// Profiles store DISABLED sets, so a new <c>modid@version</c> is ON in every profile that does
        /// not list it - and <c>ModGrouping</c>'s normalisation would then snap each of those profiles
        /// onto the new version the next time it was displayed, silently breaking any deliberate pin.
        /// So, per installed version (<c>Key</c> = every key the mod had before, <c>Value</c> = the new
        /// key):
        /// </para>
        /// <list type="bullet">
        /// <item>the viewed profile, if it had the mod ON, selects the new version (the old ones go
        /// off, exactly as picking it from the version menu would);</item>
        /// <item>the viewed profile with the mod OFF, and every other profile, get the new key added to
        /// their disabled set - nothing about them changes, whether they had the mod on at an older
        /// version or off;</item>
        /// <item><c>ClientSettings</c> follows the ACTIVE profile the same way - its selection when the
        /// active profile is the viewed one and has the mod on, else the new key disabled - written
        /// through <see cref="WriteClientDisabledMods"/>, so the baseline moves with it and
        /// <see cref="ReconcileActiveFromClientSettings"/> reads the write as ours, not a foreign
        /// edit.</item>
        /// </list>
        /// <para>
        /// Anything changed behind our back is reconciled into the active profile FIRST, so it is
        /// attributed before our own write moves the baseline. One save at the end. Unknown viewed ids
        /// are fine: every profile is then treated as "other".
        /// </para>
        /// <para>
        /// <paramref name="enableInViewed"/> is the dependency fixer's variant: the viewed profile
        /// selects the new version even when it had the mod OFF or did not have it at all (the whole
        /// point of downloading a missing dependency is that the profile needing it uses it). Every
        /// other profile is still pinned exactly as above.
        /// </para>
        /// </summary>
        public static void AdoptInstalledVersions(string viewedProfileId, IEnumerable<KeyValuePair<List<string>, string>> installs, bool enableInViewed = false)
        {
            if (installs == null) return;

            lock (Lock)
            {
                EnsureLoaded();

                ReconcileActiveFromClientSettingsInternal();

                ModProfile viewed = Find(viewedProfileId);
                ModProfile active = ResolveActive();
                List<string> client = ReadClientDisabledMods();
                bool clientTouched = false;

                foreach (KeyValuePair<List<string>, string> install in installs)
                {
                    if (string.IsNullOrWhiteSpace(install.Value)) continue;

                    string newKey = install.Value.Trim();
                    WorldModList.Split(newKey, out string modId, out string version);
                    if (string.IsNullOrEmpty(modId) || string.IsNullOrEmpty(version)) continue;

                    List<string> existing = new List<string>();
                    foreach (string key in install.Key ?? new List<string>())
                    {
                        if (string.IsNullOrWhiteSpace(key)) continue;
                        if (string.Equals(key.Trim(), newKey, StringComparison.OrdinalIgnoreCase)) continue;
                        existing.Add(key.Trim());
                    }

                    List<string> allKeys = new List<string>(existing) { newKey };

                    bool viewedOn = viewed != null && (enableInViewed || IsGroupOn(viewed, modId, existing));

                    foreach (ModProfile profile in config.Profiles)
                    {
                        if (profile == viewed && viewedOn) SetVersionSelectionInternal(profile, allKeys, newKey);
                        else SetEntry(profile, newKey, disabled: true);
                    }

                    if (active == viewed && viewedOn)
                    {
                        ApplyGroupToDisabledList(client, allKeys, newKey);
                    }
                    else if (!client.Any(e => string.Equals(e?.Trim(), newKey, StringComparison.OrdinalIgnoreCase)))
                    {
                        client.Add(newKey);
                    }

                    clientTouched = true;

                    BetterModsModSystem.Logger?.Notification(
                        "[BetterMods] Installed " + newKey + "; " +
                        (viewedOn ? "profile '" + viewed.Name + "' now uses it" : "no profile was switched to it") +
                        ", every other profile keeps its previous choice.");
                }

                if (clientTouched) WriteClientDisabledMods(client);
                Save();
            }
        }

        /// <summary>
        /// Whether a profile has a mod switched ON: not disabled by bare id, and at least one of its
        /// known versions not in the disabled set. Caller holds the lock.
        /// </summary>
        private static bool IsGroupOn(ModProfile profile, string modId, List<string> versionKeys)
        {
            if (profile.DisabledMods.Any(e => string.Equals(e?.Trim(), modId, StringComparison.OrdinalIgnoreCase))) return false;

            foreach (string key in versionKeys)
            {
                if (!profile.DisabledMods.Any(e => string.Equals(e?.Trim(), key, StringComparison.OrdinalIgnoreCase))) return true;
            }

            return false;
        }

        /// <summary>
        /// Folds changes something ELSE made to <c>ClientSettings.DisabledMods</c> back into the
        /// active profile. Returns true when the profile changed.
        /// <para>
        /// The game writes that setting directly on paths we do not own — <c>ModDbUtil.MaybeEnableMod</c>
        /// when it installs or enables a mod for the player, and vanilla's own
        /// <c>GuiScreenMods.SwitchModStatus</c> for a mod we could not key. Without this the mods
        /// screen would keep rendering such a mod from the profile (our <c>LoadModCells</c> postfix
        /// overwrites <c>cell.Enabled</c>) and the next <see cref="ApplyProfile"/> would undo the
        /// change with no explanation.
        /// </para>
        /// <para>
        /// It is a DELTA against <see cref="BetterModsConfig.LastAppliedClientDisabled"/> — the last
        /// value we ourselves wrote — not a wholesale pull, and that distinction is the whole design.
        /// The active profile is legitimately allowed to differ from <c>ClientSettings</c>: the
        /// in-game dialog edits profiles without touching the live setting, on purpose, because
        /// nothing is being enabled or disabled right now. Copying the setting over the profile would
        /// silently discard exactly those edits. Only entries that MOVED since we last wrote are
        /// attributable to somebody else, so only those are applied — the game's write wins for the
        /// mod it touched, and ours wins everywhere else.
        /// </para>
        /// <para>
        /// With no baseline (a v3 config, or a first run) there is nothing to attribute a difference
        /// to, so one is recorded and nothing is changed.
        /// </para>
        /// </summary>
        public static bool ReconcileActiveFromClientSettings()
        {
            lock (Lock)
            {
                EnsureLoaded();
                return ReconcileActiveFromClientSettingsInternal();
            }
        }

        private static bool ReconcileActiveFromClientSettingsInternal()
        {
            ModProfile active = ResolveActive();
            if (active == null) return false;

            List<string> current = Clean(ReadClientDisabledMods());
            List<string> baseline = config.LastAppliedClientDisabled;

            if (baseline == null)
            {
                SetBaseline(current);
                Save();
                return false;
            }

            HashSet<string> before = new HashSet<string>(baseline, StringComparer.OrdinalIgnoreCase);
            HashSet<string> after = new HashSet<string>(current, StringComparer.OrdinalIgnoreCase);

            bool changed = false;

            // Disabled by somebody else: adopt it.
            foreach (string entry in current)
            {
                if (before.Contains(entry)) continue;
                if (SetEntry(active, entry, disabled: true)) changed = true;
            }

            // Enabled by somebody else: drop it, and drop the bare mod id with it — a mod the game
            // just installed or switched on is wanted, and a lingering bare id would keep every
            // version of it off however many times the player enabled it.
            foreach (string entry in baseline)
            {
                if (after.Contains(entry)) continue;
                if (SetEntry(active, entry, disabled: false)) changed = true;

                WorldModList.Split(entry, out string modId, out string version);
                if (string.IsNullOrEmpty(modId) || string.IsNullOrEmpty(version)) continue;
                if (SetEntry(active, modId, disabled: false)) changed = true;
            }

            if (changed)
            {
                BetterModsModSystem.Logger?.Notification(
                    "[BetterMods] Adopted mod enable/disable changes made outside BetterMods into profile '" + active.Name + "'.");
            }

            SetBaseline(current);
            Save();
            return changed;
        }

        /// <summary>Trims, drops blanks and de-duplicates a disabled-mod list.</summary>
        private static List<string> Clean(IEnumerable<string> entries)
        {
            List<string> cleaned = new List<string>();
            if (entries == null) return cleaned;

            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string entry in entries)
            {
                if (string.IsNullOrWhiteSpace(entry)) continue;
                string normalized = entry.Trim();
                if (seen.Add(normalized)) cleaned.Add(normalized);
            }

            return cleaned;
        }

        /// <summary>
        /// Records what <c>ClientSettings.DisabledMods</c> holds now as the baseline future
        /// reconciliations measure against. Sorted, so a reordering of the same set is not mistaken
        /// for a change and does not rewrite the config file.
        /// </summary>
        private static void SetBaseline(IEnumerable<string> entries)
        {
            List<string> cleaned = Clean(entries);
            cleaned.Sort(StringComparer.OrdinalIgnoreCase);
            config.LastAppliedClientDisabled = cleaned;
        }

        // ---------------------------------------------------------------- savegame bindings

        /// <summary>
        /// Id of the profile a savegame is bound to, or null when it is unbound OR bound to a profile
        /// that has since been deleted. A dangling binding reads as unbound on purpose: the caller
        /// then records the active profile instead of prompting about a profile nobody can pick.
        /// </summary>
        public static string GetBoundProfileId(string savegameId, string saveFilePath)
        {
            lock (Lock)
            {
                EnsureLoaded();

                SavegameBinding binding = FindBindingInternal(savegameId, saveFilePath);
                if (binding == null) return null;

                return Find(binding.ProfileId) != null ? binding.ProfileId : null;
            }
        }

        /// <summary>
        /// Records (or updates) the binding of a savegame to a profile. Blank arguments are treated as
        /// "unknown" and leave the stored value alone, so the in-game recorder — which knows the
        /// savegame identifier but not the file path — cannot erase what the menu-side one wrote.
        /// <para>
        /// When a binding is found by path but under a different identifier, the identifier is
        /// re-keyed rather than a second binding added. That is what stops a legacy save whose
        /// identifier re-mints itself on every probe from accumulating junk entries, and it is the
        /// filename-based fallback for a save we cannot write the identifier back into.
        /// </para>
        /// <para>
        /// COPIED WORLDS. <c>SavegameIdentifier</c> lives inside the <c>.vcdbs</c>, so copying a world
        /// copies its identifier: two files then claim the same primary key. The policy is that the
        /// identifier stops being a key the moment we can SEE it is duplicated — a binding whose
        /// recorded path is a different file that still exists — and the file path takes over. The
        /// copy gets a binding of its own, seeded from nothing; the original's path, profile and
        /// recorded mod list are left exactly as they were. Neither world can then overwrite the
        /// other's data, which is what used to happen: binding the copy moved the original's binding
        /// onto the copy's path and the copy's first load replaced the original's recorded mod list,
        /// so going back to the original prompted against the copy's data.
        /// </para>
        /// </summary>
        public static bool BindSavegame(string savegameId, string saveFilePath, string worldName, string profileId)
        {
            lock (Lock)
            {
                EnsureLoaded();

                if (string.IsNullOrWhiteSpace(profileId)) return false;
                if (Find(profileId) == null) return false;
                if (string.IsNullOrWhiteSpace(savegameId) && string.IsNullOrWhiteSpace(saveFilePath)) return false;

                SavegameBinding binding = FindBindingInternal(savegameId, saveFilePath);
                bool changed = false;

                if (binding != null && IsForeignCopy(binding, savegameId, saveFilePath))
                {
                    BetterModsModSystem.Logger?.Notification(
                        "[BetterMods] Savegame identifier '" + savegameId + "' is claimed by both '" + binding.SaveFilePath +
                        "' and '" + saveFilePath + "' — this world is a copy. Giving it its own binding.");
                    binding = null;
                }

                if (binding == null)
                {
                    // No path and an identifier that is already spoken for: this is the in-game
                    // recorder looking at one of two copies and unable to say which. A third,
                    // pathless binding for the same identifier would only make the ambiguity worse.
                    if (string.IsNullOrWhiteSpace(saveFilePath) && CountBindingsWithId(savegameId) > 0)
                    {
                        BetterModsModSystem.Logger?.Notification(
                            "[BetterMods] Not binding savegame '" + savegameId +
                            "': that identifier is shared by more than one known world and no file path is available here.");
                        return false;
                    }

                    binding = new SavegameBinding();
                    config.SavegameBindings.Add(binding);
                    changed = true;
                }

                changed |= Assign(ref binding.SavegameId, savegameId);
                changed |= Assign(ref binding.SaveFilePath, saveFilePath);
                changed |= Assign(ref binding.WorldName, worldName);
                changed |= Assign(ref binding.ProfileId, profileId);

                if (changed) Save();
                return changed;
            }
        }

        /// <summary>
        /// The mods a savegame was last observed to load with, as <c>"modid@version"</c>, or null when
        /// it has never been observed. The returned list is a snapshot copy; an EMPTY list is a real
        /// answer ("it loaded no non-core mods") and must not be conflated with null.
        /// </summary>
        public static List<string> GetModList(string savegameId, string saveFilePath)
        {
            lock (Lock)
            {
                EnsureLoaded();

                SavegameBinding binding = FindBindingInternal(savegameId, saveFilePath);
                return binding?.ModList == null ? null : new List<string>(binding.ModList);
            }
        }

        /// <summary>
        /// Records the mod list a savegame actually loaded with. The latest successful load wins: the
        /// previous list is replaced outright rather than merged, because the point of the list is to
        /// describe one real load and a merged list describes none.
        /// <para>
        /// Requires a binding to already exist — callers bind first, which is what decides which
        /// profile the world is associated with. Returns true when something changed.
        /// </para>
        /// </summary>
        public static bool RecordModList(string savegameId, string saveFilePath, IEnumerable<string> modList)
        {
            if (modList == null) return false;

            lock (Lock)
            {
                EnsureLoaded();

                SavegameBinding binding = FindBindingInternal(savegameId, saveFilePath);
                if (binding == null) return false;

                List<string> cleaned = new List<string>();
                HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string entry in modList)
                {
                    if (string.IsNullOrWhiteSpace(entry)) continue;
                    string normalized = entry.Trim();
                    if (seen.Add(normalized)) cleaned.Add(normalized);
                }

                cleaned.Sort(StringComparer.OrdinalIgnoreCase);

                if (binding.ModList != null && binding.ModList.SequenceEqual(cleaned, StringComparer.OrdinalIgnoreCase))
                {
                    return false;
                }

                binding.ModList = cleaned;
                Save();
                return true;
            }
        }

        /// <summary>How many stored bindings carry this savegame identifier. More than one is a copy.</summary>
        private static int CountBindingsWithId(string savegameId)
        {
            if (config.SavegameBindings == null || string.IsNullOrWhiteSpace(savegameId)) return 0;

            string id = savegameId.Trim();
            return config.SavegameBindings.Count(
                b => b != null && string.Equals(b.SavegameId, id, StringComparison.Ordinal));
        }

        /// <summary>Writes a non-blank value over a field, reporting whether it actually differed.</summary>
        private static bool Assign(ref string field, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;

            string trimmed = value.Trim();
            if (string.Equals(field, trimmed, StringComparison.Ordinal)) return false;

            field = trimmed;
            return true;
        }

        /// <summary>
        /// The binding for a savegame.
        /// <para>
        /// The PATH is tried first when it is known. That is not a demotion of the identifier: a
        /// world that was renamed or moved keeps its identifier and changes path, so it misses on
        /// path and is found (and re-keyed) by id, exactly as before. What path-first buys is the
        /// copied world, where two files share one identifier and the path is the only thing that
        /// tells them apart.
        /// </para>
        /// <para>
        /// With no path — the in-game recorder, which knows the identifier and nothing else — a
        /// DUPLICATED identifier is unanswerable, so it answers nothing rather than pick a side and
        /// write one world's mod list onto the other's binding. The next start through the menu knows
        /// the path and settles it.
        /// </para>
        /// </summary>
        private static SavegameBinding FindBindingInternal(string savegameId, string saveFilePath)
        {
            if (config.SavegameBindings == null) return null;

            if (!string.IsNullOrWhiteSpace(saveFilePath))
            {
                string path = saveFilePath.Trim();
                SavegameBinding byPath = config.SavegameBindings.FirstOrDefault(
                    b => b != null && !string.IsNullOrWhiteSpace(b.SaveFilePath) &&
                         string.Equals(b.SaveFilePath, path, StringComparison.OrdinalIgnoreCase));
                if (byPath != null) return byPath;
            }

            if (string.IsNullOrWhiteSpace(savegameId)) return null;

            string id = savegameId.Trim();
            List<SavegameBinding> byId = config.SavegameBindings.FindAll(
                b => b != null && string.Equals(b.SavegameId, id, StringComparison.Ordinal));

            if (byId.Count == 1) return byId[0];
            if (byId.Count == 0) return null;

            if (string.IsNullOrWhiteSpace(saveFilePath))
            {
                BetterModsModSystem.Logger?.Notification(
                    "[BetterMods] Savegame identifier '" + id + "' is shared by " + byId.Count +
                    " known worlds and no file path was supplied, so it cannot be told which one this is.");
                return null;
            }

            // The path is known and matched none of them: this is another copy again.
            return null;
        }

        /// <summary>
        /// True when <paramref name="binding"/> was matched on identifier alone but demonstrably
        /// belongs to a DIFFERENT file that is still there — i.e. the caller is looking at a copy of
        /// that world, not at the world itself moved somewhere else.
        /// </summary>
        private static bool IsForeignCopy(SavegameBinding binding, string savegameId, string saveFilePath)
        {
            if (string.IsNullOrWhiteSpace(savegameId) || string.IsNullOrWhiteSpace(saveFilePath)) return false;
            if (string.IsNullOrWhiteSpace(binding.SaveFilePath)) return false;
            if (string.Equals(binding.SaveFilePath, saveFilePath.Trim(), StringComparison.OrdinalIgnoreCase)) return false;

            try
            {
                // A path that no longer exists means the world was moved or renamed, which is the
                // case the identifier is the key FOR. Only a file that is still there is a copy.
                return File.Exists(binding.SaveFilePath);
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning(
                    "[BetterMods] Could not stat '" + binding.SaveFilePath + "': " + e.Message);
                return false;
            }
        }

        // ---------------------------------------------------------------- internals

        /// <summary>
        /// Idempotent <see cref="Load"/>: reads the config exactly once per process, and does nothing
        /// on every call after that. <see cref="Load"/> itself always re-reads from disk, which is why
        /// callers that only need "the store is usable" — every accessor below, and
        /// <c>PatchBootstrap</c>, which runs from both the startup hook and the mod system — go
        /// through here instead.
        /// <para>
        /// A load that failed because the file could not be READ is not treated as done: the cause is
        /// usually transient (an antivirus scan, a cloud-sync lock, a file still being written), so it
        /// is retried, throttled to one attempt per <see cref="RetryIntervalMs"/> so a locked file
        /// cannot turn every property read into a filesystem call. Until it succeeds the store runs
        /// from memory and writes nothing — see <see cref="PersistenceSuspended"/>.
        /// </para>
        /// </summary>
        public static void EnsureLoaded()
        {
            lock (Lock)
            {
                if (loaded) return;

                if (PersistenceSuspended &&
                    Environment.TickCount64 - lastLoadAttempt < RetryIntervalMs)
                {
                    return;
                }

                Load();
            }
        }

        private static ModProfile Find(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            return config.Profiles.FirstOrDefault(p => p.Id == id);
        }

        /// <summary>Returns the active profile, repairing a dangling/blank active id in place.</summary>
        private static ModProfile ResolveActive()
        {
            ModProfile active = Find(config.ActiveProfileId);
            if (active != null) return active;

            if (config.Profiles.Count == 0)
            {
                ModProfile seed = new ModProfile("Profile 1", ReadClientDisabledMods());
                config.Profiles.Add(seed);
            }

            active = config.Profiles[0];
            config.ActiveProfileId = active.Id;
            return active;
        }

        private static string NextAutoName()
        {
            HashSet<int> taken = new HashSet<int>();
            foreach (ModProfile p in config.Profiles)
            {
                if (p.Name == null) continue;
                if (!p.Name.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase)) continue;
                if (int.TryParse(p.Name.Substring("Profile ".Length).Trim(), out int n)) taken.Add(n);
            }

            int candidate = 1;
            while (taken.Contains(candidate)) candidate++;
            return "Profile " + candidate;
        }

        /// <summary>
        /// True if <paramref name="entry"/> ("modid@version" or bare "modid") names a mod on the
        /// downloaded blocklist. Returns false when the blocklist has not been fetched yet:
        /// <c>ModDbUtil.IsModBlocked</c> reports <c>true</c> for EVERY mod while
        /// <c>ModBlockList</c> is null (ModDbUtil.cs:655-659), which would otherwise make us
        /// preserve the entire existing disabled set.
        /// </summary>
        private static bool IsBlockedEntry(string entry)
        {
            try
            {
                if (ModDbUtil.ModBlockList == null || ModDbUtil.ModBlockList.Count == 0) return false;

                string modId = entry;
                string version = null;
                int at = entry.IndexOf('@');
                if (at > 0)
                {
                    modId = entry.Substring(0, at);
                    version = entry.Substring(at + 1);
                }

                return ModDbUtil.IsModBlocked(modId, version, out _);
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] Blocklist check failed for '" + entry + "': " + e.Message);
                return false;
            }
        }

        private static List<string> ReadClientDisabledMods()
        {
            try
            {
                List<string> disabled = ClientSettings.DisabledMods;
                return disabled != null ? new List<string>(disabled) : new List<string>();
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not read ClientSettings.DisabledMods: " + e.Message);
                return new List<string>();
            }
        }

        private static void WriteClientDisabledMods(List<string> entries)
        {
            try
            {
                ClientSettings.DisabledMods = entries;
                ClientSettings.Inst.Save(force: true);

                // Everything from here on is somebody else's edit — see
                // ReconcileActiveFromClientSettings. Recorded even if the caller does not save
                // straight away; the next Save picks it up.
                SetBaseline(entries);
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Error("[BetterMods] Could not write ClientSettings.DisabledMods.");
                BetterModsModSystem.Logger?.Error(e);
            }
        }
    }
}
