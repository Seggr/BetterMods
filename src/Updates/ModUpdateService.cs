using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterMods.Profiles;
using Newtonsoft.Json;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;
using Vintagestory.Common;
using Vintagestory.ModDb;

namespace BetterMods.Updates
{
    /// <summary>Where the session's update check stands.</summary>
    public enum UpdateCheckState
    {
        NotStarted,
        Checking,
        Done,
        Failed
    }

    /// <summary>What one row should say about updates, for the group it was built from.</summary>
    public sealed class UpdateOffer
    {
        /// <summary>The release to install, or null when there is nothing newer to offer.</summary>
        public string TargetVersion;

        /// <summary>
        /// Null for a tier-1 offer (a release tagged for the running game version). Otherwise the
        /// older stable game version <see cref="TargetVersion"/> is tagged for: a tier-2 offer, which is
        /// untested on this game version, never part of "Update all", and installed only after an
        /// explicit confirmation.
        /// </summary>
        public string OlderGameVersion;

        public bool IsOlderGameVersionOffer => TargetVersion != null && OlderGameVersion != null;

        /// <summary>The newest installed release was retracted on the mod DB.</summary>
        public bool Retracted;

        /// <summary>The DB's reason for the retraction, ASCII-safe, or empty.</summary>
        public string RetractionReason;

        /// <summary>An install of this mod is running right now.</summary>
        public bool Installing;

        /// <summary>Why the last install of this mod failed, or null.</summary>
        public string InstallError;
    }

    /// <summary>One release to download for the dependency fixer.</summary>
    public sealed class DependencyRelease
    {
        public string ModId;
        public string Version;

        /// <summary>Every installed key of the mod before the download (possibly none).</summary>
        public List<string> ExistingKeys = new List<string>();
    }

    /// <summary>How one dependency download went.</summary>
    public sealed class DependencyInstallResult
    {
        public string ModId;
        public string Version;

        /// <summary><c>modid@version</c> as installed, or null when it failed.</summary>
        public string InstalledKey;

        /// <summary>ASCII reason, or null on success.</summary>
        public string Error;
    }

    /// <summary>
    /// The session's mod update check, its cached answers, and the installer.
    /// <para>
    /// <b>Check.</b> One batched check per session, started by whichever surface is opened first,
    /// re-run on demand and after "Reload Mods" - never polled. The installed set is snapshotted on the
    /// main thread into plain values (<c>loadMods()</c> rebuilds <c>allMods</c>), the requests run on
    /// the thread pool, and the answer is posted back through <c>ScreenManager.EnqueueMainThreadTask</c>,
    /// which is pumped every frame in the menu and in a world alike. Every check carries a generation
    /// number; an answer from a superseded or cancelled check is dropped.
    /// </para>
    /// <para>
    /// Two passes: <c>modid@installed</c> first (the DB answers with <c>recommendedUpgrade</c> when a
    /// newer release for this game version exists, and flags a retracted installed release), then the
    /// bare id for everything the DB did not know at that version or that has a version the DB cannot
    /// parse (the bare answer's <c>recommendedUpgrade</c> is the newest release for this game version).
    /// The answer is cached as "what the DB recommends", and whether that is an update is decided at
    /// display time against what is installed THEN - so installing the update retires the offer with
    /// no second check.
    /// </para>
    /// <para>
    /// <b>Tier 2.</b> For every mod still without a tier-1 upgrade (retracted installs included), one
    /// more batched bare-id pass at the newest STABLE game version older than the running one, read
    /// from <c>api/gameversions</c>. Same <see cref="ModUpdateRules.DecideUpdate"/> rules and the
    /// blocklist decide whether it is an offer; such an offer is marked
    /// (<see cref="UpdateOffer.OlderGameVersion"/>), left out of "Update all", and installed only after
    /// a per-mod confirmation.
    /// </para>
    /// <para>
    /// <b>Install.</b> Only from an explicit button. Downloads to a dot-prefixed temp file in the data
    /// mods folder (<c>ModLoader.CollectMods</c> skips dot-files), verifies the zip's own
    /// <c>modinfo.json</c> against what was asked for, and moves it into place without ever
    /// overwriting anything. The old version is never touched. Each installed file is folded into the
    /// profiles (<see cref="ProfileStore.AdoptInstalledVersions"/>) on the very next frame, so only
    /// the profile that was on screen switches to it.
    /// </para>
    /// <para>All state is main-thread only, except what the background tasks write into their own jobs.</para>
    /// </summary>
    public static class ModUpdateService
    {
        private sealed class ModDbRecord
        {
            public string QueriedVersion;
            public string Recommended;
            public bool Retracted;
            public string RetractionReason;

            /// <summary>
            /// The DB answered for this mod without a "not found": a versioned spec it had a release
            /// for (retracted or not), or a bare spec it had a release for at this game version. A
            /// 4041 on both is "not on the DB" as far as anything here can tell - the API cannot tell
            /// that apart from "nothing tagged for this game version" (NOTES, install-information).
            /// </summary>
            public bool KnownToDb;

            /// <summary>
            /// Tier 2: the newest stable release tagged for <see cref="Tier2GameVersion"/> (the newest
            /// stable game version older than the running one), asked only for mods with no tier-1
            /// upgrade. Null when not asked or nothing is tagged.
            /// </summary>
            public string Tier2Recommended;

            public string Tier2GameVersion;
        }

        private sealed class CheckOutcome
        {
            public readonly Dictionary<string, ModDbRecord> Records = new Dictionary<string, ModDbRecord>(StringComparer.OrdinalIgnoreCase);
            public int Requests;
            public int FailedRequests;
            public string FirstError;
            public string OlderGameVersion;
        }

        private sealed class InstallJob
        {
            public string ModId;
            public string TargetVersion;
            public List<string> ExistingKeys;

            /// <summary>
            /// A dependency download: the viewed profile switches the new version ON even when it had
            /// the mod off (or did not have it at all). An update only follows a mod that was on.
            /// </summary>
            public bool EnableInViewed;

            // Written by the background task, read on the main thread after it posts.
            public string InstalledKey;
            public string Error;
        }

        /// <summary>
        /// Raised on the main thread whenever anything a surface shows may have changed. Handlers are
        /// isolated from each other; a throwing handler is logged and skipped.
        /// </summary>
        public static event Action Changed;

        /// <summary>How many downloads run at once (1.23's <c>MaxParallelModDownloads</c> default; 1.22 has no such setting).</summary>
        private const int DefaultParallelDownloads = 3;

        private static readonly Dictionary<string, ModDbRecord> Records = new Dictionary<string, ModDbRecord>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> Installing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> InstallErrors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private static int generation;
        private static CancellationTokenSource checkCancel;
        private static int installFinished;

        public static UpdateCheckState State { get; private set; } = UpdateCheckState.NotStarted;

        /// <summary>Some, but not all, of the last check's requests failed.</summary>
        public static bool CheckIncomplete { get; private set; }

        /// <summary>ASCII reason the last check failed, or null.</summary>
        public static string CheckError { get; private set; }

        public static bool IsInstalling => Installing.Count > 0;

        public static int InstallTotal { get; private set; }

        public static int InstallFinished => Volatile.Read(ref installFinished);

        /// <summary>One line about the last install run, or null. Cleared by the next check or install.</summary>
        public static string InstallSummary { get; private set; }

        public static bool InstallSummaryIsError { get; private set; }

        /// <summary>What the running install is, for the status line: "Updating mods" or "Installing dependencies".</summary>
        public static string InstallLabel { get; private set; } = "Updating mods";

        /// <summary>
        /// The game version tier 2 last asked about (the newest stable one older than
        /// <see cref="GameVersionChecked"/>), or null before a check has found one.
        /// </summary>
        public static string OlderGameVersion { get; private set; }

        /// <summary>
        /// The game version the DB is asked about - what the <c>game</c> core mod reports, i.e. the
        /// RUNNING game's <c>GameVersion.ShortGameVersion</c> (on the 1.23 preview <c>1.23.0-pre.1</c>,
        /// which has almost nothing tagged for it yet). That is a <c>const</c>: naming it in code bakes
        /// in the version this build was compiled against (a build against <c>1.23.0-pre.1</c> would
        /// keep saying so on every later 1.23 release; the 1.22 build compiles against the oldest 1.22.x
        /// it supports), so the value is read from the loaded VintagestoryAPI's metadata instead.
        /// Every BetterMods use of "the game version" goes through here.
        /// </summary>
        public static string GameVersionChecked { get; } = ReadRunningGameVersion();

        private static string ReadRunningGameVersion()
        {
            try
            {
                if (typeof(GameVersion).GetField(nameof(GameVersion.ShortGameVersion))?.GetRawConstantValue() is string running
                    && !string.IsNullOrEmpty(running))
                {
                    return running;
                }
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Error("[BetterMods] Could not read the running game version; using the compile-time one: " + e.Message);
            }
            return GameVersion.ShortGameVersion;
        }

        private static bool menuReloadPending;

        // ---------------------------------------------------------------- check

        /// <summary>Starts the session's check if none has run yet. Never throws.</summary>
        public static void EnsureChecked()
        {
            if (State != UpdateCheckState.NotStarted) return;
            StartCheck(notify: false);
        }

        /// <summary>Starts a fresh check, superseding any running one. Never throws.</summary>
        public static void Recheck()
        {
            InstallSummary = null;
            StartCheck(notify: true);
        }

        /// <summary>
        /// Abandons a running check (world exit). Its answer will be dropped, and the next surface
        /// to open starts a new one. Installs are not affected - they only ever start from a button.
        /// </summary>
        public static void CancelCheck()
        {
            try
            {
                if (State != UpdateCheckState.Checking) return;

                generation++;
                checkCancel?.Cancel();
                State = UpdateCheckState.NotStarted;
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not cancel the mod update check: " + e.Message);
            }
        }

        private static void StartCheck(bool notify)
        {
            try
            {
                List<InstalledModSnapshot> snapshot = Snapshot(out bool reliable);
                if (!reliable)
                {
                    State = UpdateCheckState.Failed;
                    CheckError = "the installed mod list could not be read";
                    if (notify) RaiseChangedNextFrame();
                    return;
                }

                checkCancel?.Cancel();
                CancellationTokenSource cancel = new CancellationTokenSource();
                checkCancel = cancel;

                int gen = ++generation;
                State = UpdateCheckState.Checking;
                CheckIncomplete = false;
                CheckError = null;

                // Read on the main thread: ClientSettings is not ours to touch from the pool.
                string modDbUrl = ClientSettings.ModDbUrl;
                string gameVersion = GameVersionChecked;

                Task.Run(() => RunCheckAsync(snapshot, modDbUrl, gameVersion, cancel.Token))
                    .ContinueWith(task => Post(() => FinishCheck(gen, task)), TaskScheduler.Default);

                if (notify) RaiseChangedNextFrame();
            }
            catch (Exception e)
            {
                State = UpdateCheckState.Failed;
                CheckError = "the check could not be started";
                BetterModsModSystem.Logger?.Error("[BetterMods] Could not start the mod update check.");
                BetterModsModSystem.Logger?.Error(e);
            }
        }

        /// <summary>
        /// Every installed mod worth asking about, one per mod id with its newest comparable version.
        /// Leaves out core mods and the bundled <c>game</c>/<c>creative</c>/<c>survival</c> (vanilla
        /// never offers those either), BetterMods itself, containers whose <c>modinfo.json</c> failed,
        /// and anything that lives under the game install, which is never ours to change.
        /// </summary>
        private static List<InstalledModSnapshot> Snapshot(out bool reliable)
        {
            List<ModContainer> mods = InstalledModScanner.MenuMods(ClientProgram.screenManager, out reliable);
            List<InstalledModSnapshot> snapshot = new List<InstalledModSnapshot>();
            if (!reliable) return snapshot;

            string install = FullPathOrNull(GamePaths.Binaries);
            Dictionary<string, List<string>> versionsById = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            List<string> order = new List<string>();

            foreach (ModContainer mod in mods)
            {
                ModInfo info = mod?.Info;
                if (info == null || info.CoreMod) continue;

                string modId = InstalledModScanner.ModIdOf(mod);
                if (string.IsNullOrEmpty(modId) || ModUpdateRules.IsBundledModId(modId)) continue;
                if (string.Equals(modId, BetterModsModSystem.ModId, StringComparison.OrdinalIgnoreCase)) continue;
                if (IsUnder(mod.SourcePath, install)) continue;

                if (!versionsById.TryGetValue(modId, out List<string> versions))
                {
                    versions = new List<string>();
                    versionsById[modId] = versions;
                    order.Add(modId);
                }

                versions.Add(info.Version);
            }

            foreach (string modId in order)
            {
                string newest = ModUpdateRules.NewestComparable(versionsById[modId]);
                if (newest != null) snapshot.Add(new InstalledModSnapshot(modId, newest));
            }

            return snapshot;
        }

        private static async Task<CheckOutcome> RunCheckAsync(
            List<InstalledModSnapshot> snapshot, string modDbUrl, string gameVersion, CancellationToken cancel)
        {
            CheckOutcome outcome = new CheckOutcome();

            Dictionary<string, ModDbEntry> versioned = await QueryAllAsync(
                outcome, modDbUrl, ModUpdateRules.BuildSpecs(snapshot, versioned: true), gameVersion, cancel).ConfigureAwait(false);

            List<InstalledModSnapshot> needBare = new List<InstalledModSnapshot>();
            foreach (InstalledModSnapshot mod in snapshot)
            {
                ModDbRecord record = new ModDbRecord { QueriedVersion = mod.NewestVersion };

                if (!ModUpdateRules.IsDbVersion(mod.NewestVersion))
                {
                    needBare.Add(mod);
                }
                else if (versioned.TryGetValue(mod.ModId, out ModDbEntry entry))
                {
                    if (entry.IsRetracted)
                    {
                        record.Retracted = true;

                        // Arrives as HTML ("<p>Retracted by moderation ...</p>", observed live).
                        record.RetractionReason = ModUpdateRules.AsciiLine(ModUpdateRules.StripHtml(entry.RetractionReason), 160);
                    }

                    record.KnownToDb = entry.ErrorCode == 0 || entry.IsRetracted;

                    if (entry.ErrorCode == 0 || entry.IsRetracted) record.Recommended = entry.RecommendedUpgrade;

                    // The DB does not know this exact release (a dev build, a hand-edited version), or
                    // knows it but gave no upgrade alongside a retraction: ask what is newest instead.
                    if (entry.ErrorCode == ModUpdateRules.ErrorSpecNotFound || (entry.IsRetracted && record.Recommended == null))
                    {
                        needBare.Add(mod);
                    }
                }
                else
                {
                    // Its request failed; already counted. Nothing is known about it.
                    continue;
                }

                outcome.Records[mod.ModId] = record;
            }

            if (needBare.Count > 0)
            {
                Dictionary<string, ModDbEntry> bare = await QueryAllAsync(
                    outcome, modDbUrl, ModUpdateRules.BuildSpecs(needBare, versioned: false), gameVersion, cancel).ConfigureAwait(false);

                foreach (InstalledModSnapshot mod in needBare)
                {
                    if (!bare.TryGetValue(mod.ModId, out ModDbEntry entry) || entry.ErrorCode != 0) continue;
                    if (!outcome.Records.TryGetValue(mod.ModId, out ModDbRecord record))
                    {
                        record = new ModDbRecord { QueriedVersion = mod.NewestVersion };
                        outcome.Records[mod.ModId] = record;
                    }

                    // Observed live: a bare spec answers with recommendedUpgrade set to the newest
                    // release tagged for the game version - exactly the comparison we need.
                    record.Recommended ??= entry.RecommendedUpgrade;
                    record.KnownToDb = true;
                }
            }

            await RunTier2Async(outcome, snapshot, modDbUrl, gameVersion, cancel).ConfigureAwait(false);

            return outcome;
        }

        /// <summary>
        /// Tier 2: for every mod with no tier-1 upgrade (nothing newer tagged for the running game
        /// version - which on a preview build is most of them, retracted installs included), ONE more
        /// batched bare-id query at the newest stable game version older than the running one. The
        /// answer is only a candidate: whether it is an offer is decided at display time by the same
        /// <see cref="ModUpdateRules.DecideUpdate"/> rules (strictly newer, no stable-onto-prerelease)
        /// plus the blocklist.
        /// <para>
        /// The older version comes from <c>api/gameversions</c>. If that list cannot be had, tier 2 is
        /// simply skipped (logged) - tier 1 already answered and must not be marked as failed for it.
        /// </para>
        /// </summary>
        private static async Task RunTier2Async(
            CheckOutcome outcome, List<InstalledModSnapshot> snapshot, string modDbUrl, string gameVersion, CancellationToken cancel)
        {
            List<InstalledModSnapshot> candidates = new List<InstalledModSnapshot>();
            foreach (InstalledModSnapshot mod in snapshot)
            {
                if (outcome.Records.TryGetValue(mod.ModId, out ModDbRecord record)
                    && ModUpdateRules.DecideUpdate(mod.NewestVersion, record.Recommended) != null) continue;

                candidates.Add(mod);
            }

            if (candidates.Count == 0) return;

            string older;
            try
            {
                older = await FetchOlderGameVersionAsync(modDbUrl, gameVersion, cancel).ConfigureAwait(false);
            }
            catch (ModDbException e)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not read the mod DB's game version list; skipping releases for older game versions: " + e.Message);
                return;
            }

            if (older == null) return;
            outcome.OlderGameVersion = older;

            Dictionary<string, ModDbEntry> answer = await QueryAllAsync(
                outcome, modDbUrl, ModUpdateRules.BuildSpecs(candidates, versioned: false), older, cancel).ConfigureAwait(false);

            foreach (InstalledModSnapshot mod in candidates)
            {
                if (!answer.TryGetValue(mod.ModId, out ModDbEntry entry) || entry.ErrorCode != 0 || string.IsNullOrWhiteSpace(entry.RecommendedUpgrade)) continue;

                if (!outcome.Records.TryGetValue(mod.ModId, out ModDbRecord record))
                {
                    record = new ModDbRecord { QueriedVersion = mod.NewestVersion };
                    outcome.Records[mod.ModId] = record;
                }

                record.Tier2Recommended = entry.RecommendedUpgrade.Trim();
                record.Tier2GameVersion = older;
                record.KnownToDb = true;
            }
        }

        /// <summary>
        /// The newest stable game version older than <paramref name="runningVersion"/>, from the DB's
        /// <c>api/gameversions</c>. Null when the list has none. Throws <see cref="ModDbException"/>.
        /// </summary>
        internal static async Task<string> FetchOlderGameVersionAsync(string modDbUrl, string runningVersion, CancellationToken cancel)
        {
            string body = await ModDbClient.FetchGameVersionsAsync(modDbUrl, cancel).ConfigureAwait(false);
            List<string> versions = ModUpdateRules.ParseGameVersions(body) ?? throw new ModDbException("the mod DB's game version list could not be read");
            return ModUpdateRules.OlderStableGameVersion(versions, runningVersion);
        }

        /// <summary>
        /// Runs <paramref name="specs"/> in chunks and merges the answers. A failed chunk is counted and
        /// skipped, so one bad request costs only the mods in it.
        /// </summary>
        private static async Task<Dictionary<string, ModDbEntry>> QueryAllAsync(
            CheckOutcome outcome, string modDbUrl, List<string> specs, string gameVersion, CancellationToken cancel)
        {
            Dictionary<string, ModDbEntry> merged = new Dictionary<string, ModDbEntry>(StringComparer.OrdinalIgnoreCase);

            foreach (List<string> chunk in ModUpdateRules.Chunk(specs, ModUpdateRules.SpecsPerRequest))
            {
                cancel.ThrowIfCancellationRequested();
                outcome.Requests++;

                try
                {
                    Dictionary<string, ModDbEntry> answer = await ModDbClient.QueryAsync(modDbUrl, chunk, gameVersion, cancel).ConfigureAwait(false);
                    foreach (KeyValuePair<string, ModDbEntry> entry in answer)
                    {
                        if (merged.TryGetValue(entry.Key, out ModDbEntry existing) && existing.ErrorCode == 0 && entry.Value.ErrorCode != 0) continue;
                        merged[entry.Key] = entry.Value;
                    }
                }
                catch (ModDbException e)
                {
                    outcome.FailedRequests++;
                    outcome.FirstError ??= e.Message;
                }
            }

            return merged;
        }

        private static void FinishCheck(int gen, Task<CheckOutcome> task)
        {
            if (gen != generation) return;

            if (task.IsCanceled || task.Exception?.GetBaseException() is OperationCanceledException)
            {
                State = UpdateCheckState.NotStarted;
                return;
            }

            if (task.IsFaulted)
            {
                State = UpdateCheckState.Failed;
                CheckError = "unexpected error";
                BetterModsModSystem.Logger?.Error("[BetterMods] The mod update check failed.");
                BetterModsModSystem.Logger?.Error(task.Exception?.GetBaseException());
                RaiseChanged();
                return;
            }

            CheckOutcome outcome = task.Result;
            if (outcome.Requests > 0 && outcome.FailedRequests == outcome.Requests)
            {
                State = UpdateCheckState.Failed;
                CheckError = outcome.FirstError ?? "the mod DB could not be reached";
                BetterModsModSystem.Logger?.Warning("[BetterMods] Mod update check failed: " + CheckError);
                RaiseChanged();
                return;
            }

            Records.Clear();
            foreach (KeyValuePair<string, ModDbRecord> record in outcome.Records) Records[record.Key] = record.Value;

            State = UpdateCheckState.Done;
            CheckIncomplete = outcome.FailedRequests > 0;
            CheckError = CheckIncomplete ? outcome.FirstError : null;
            if (outcome.OlderGameVersion != null) OlderGameVersion = outcome.OlderGameVersion;

            int tier2 = 0;
            foreach (ModDbRecord record in outcome.Records.Values)
            {
                if (record.Tier2Recommended != null) tier2++;
            }

            BetterModsModSystem.Logger?.Notification(
                "[BetterMods] Mod update check for game version " + GameVersionChecked + ": " + outcome.Records.Count +
                " mod(s) known to the mod DB, " + outcome.Requests + " request(s)" +
                (outcome.OlderGameVersion != null ? ", " + tier2 + " release(s) found for " + outcome.OlderGameVersion : "") +
                (CheckIncomplete ? ", " + outcome.FailedRequests + " failed (" + CheckError + ")" : "") + ".");

            RaiseChanged();
        }

        // ---------------------------------------------------------------- what to show

        /// <summary>
        /// What a row built from <paramref name="group"/> should show, or null for nothing. Decided
        /// against what is installed NOW, so an offer disappears as soon as its version is installed.
        /// </summary>
        public static UpdateOffer OfferFor(ModVersionGroup group)
        {
            try
            {
                if (string.IsNullOrEmpty(group?.ModId)) return null;

                string modId = group.ModId;
                Records.TryGetValue(modId, out ModDbRecord record);
                InstallErrors.TryGetValue(modId, out string error);
                bool installing = Installing.Contains(modId);

                string newest = ModUpdateRules.NewestComparable(group.Versions.Select(v => v?.Info?.Version));
                string target = record != null ? ModUpdateRules.DecideUpdate(newest, record.Recommended) : null;
                if (target != null && IsBlocked(modId, target)) target = null;

                // Tier 2 only where tier 1 has nothing, under the very same rules.
                string olderGameVersion = null;
                if (target == null && record?.Tier2Recommended != null && record.Tier2GameVersion != null)
                {
                    string older = ModUpdateRules.DecideUpdate(newest, record.Tier2Recommended);
                    if (older != null && !IsBlocked(modId, older))
                    {
                        target = older;
                        olderGameVersion = record.Tier2GameVersion;
                    }
                }

                bool retracted = record != null && record.Retracted && newest != null
                                 && string.Equals(record.QueriedVersion, newest, StringComparison.OrdinalIgnoreCase);

                if (target == null && !retracted && !installing) return null;

                return new UpdateOffer
                {
                    TargetVersion = target,
                    OlderGameVersion = olderGameVersion,
                    Retracted = retracted,
                    RetractionReason = retracted ? record.RetractionReason ?? "" : "",
                    Installing = installing,
                    InstallError = error
                };
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not work out the update state of a mod: " + e.Message);
                return null;
            }
        }

        /// <summary>
        /// True when the last completed check found <paramref name="modId"/> on the mod DB - the
        /// condition for offering its mod page. False before any check has answered, for mods the
        /// check never asks about (core mods, the bundled ones, anything under the game install,
        /// BetterMods itself), and for an id the DB answered "not found" for.
        /// </summary>
        public static bool IsKnownToModDb(string modId)
        {
            if (string.IsNullOrEmpty(modId)) return false;

            return Records.TryGetValue(modId, out ModDbRecord record) && record != null && record.KnownToDb;
        }

        /// <summary>
        /// How many of <paramref name="groups"/> have a tier-1 update (a release tagged for the running
        /// game version) to offer right now - what "Update all" installs.
        /// </summary>
        public static int CountUpdates(IEnumerable<ModVersionGroup> groups)
        {
            int count = 0;
            if (groups == null) return 0;

            foreach (ModVersionGroup group in groups)
            {
                UpdateOffer offer = OfferFor(group);
                if (offer?.TargetVersion != null && !offer.IsOlderGameVersionOffer) count++;
            }

            return count;
        }

        /// <summary>How many of <paramref name="groups"/> only have a tier-2 offer (a release for an older game version).</summary>
        public static int CountOlderGameVersionOffers(IEnumerable<ModVersionGroup> groups)
        {
            int count = 0;
            if (groups == null) return 0;

            foreach (ModVersionGroup group in groups)
            {
                if (OfferFor(group)?.IsOlderGameVersionOffer == true) count++;
            }

            return count;
        }

        /// <summary>
        /// One line for the main-menu status slot (and the in-game notice) about something other than
        /// an update run - the dependency fixer reports here. Cleared by the next check or install.
        /// Main thread. Never throws.
        /// </summary>
        public static void ReportSummary(string text, bool isError)
        {
            InstallSummary = string.IsNullOrWhiteSpace(text) ? null : ModUpdateRules.AsciiLine(text, 300);
            InstallSummaryIsError = isError;
            RaiseChangedNextFrame();
        }

        /// <summary>
        /// A blocked release is never offered. Only consulted once the blocklist is in: while it is
        /// null <c>IsModBlocked</c> answers true for everything (NOTES, "Mod enable/disable
        /// persistence"), and the installer re-checks with the list loaded before downloading.
        /// </summary>
        private static bool IsBlocked(string modId, string version)
        {
            try
            {
                if (ModDbUtil.ModBlockList == null) return false;
                return ModDbUtil.IsModBlocked(modId, version, out _);
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ---------------------------------------------------------------- install

        /// <summary>
        /// Installs the offered update for every group in <paramref name="groups"/> that has one, and
        /// makes <paramref name="viewedProfileId"/> - and only it - use the new versions. Returns how
        /// many installs were started; 0 when another install is still running or nothing is offered.
        /// <para>
        /// Tier-2 offers (a release tagged only for an older game version) are skipped unless
        /// <paramref name="includeOlderGameVersion"/> - which only the per-mod confirmation passes.
        /// "Update all" never installs one.
        /// </para>
        /// Main thread only. Never throws.
        /// </summary>
        public static int StartInstall(IEnumerable<ModVersionGroup> groups, string viewedProfileId, bool includeOlderGameVersion = false)
        {
            try
            {
                if (IsInstalling || groups == null) return 0;

                // The profile transaction is what keeps a new file from switching on in every profile.
                // With the config unreadable it would only live in memory, and after a restart every
                // profile that does not disable the mod outright would snap to the new version.
                if (ProfileStore.PersistenceSuspended)
                {
                    InstallSummary = "Not installing updates - profile config unreadable";
                    InstallSummaryIsError = true;
                    RaiseChangedNextFrame();
                    return 0;
                }

                List<InstallJob> jobs = new List<InstallJob>();
                HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (ModVersionGroup group in groups)
                {
                    UpdateOffer offer = OfferFor(group);
                    if (offer?.TargetVersion == null || !seen.Add(group.ModId)) continue;
                    if (offer.IsOlderGameVersionOffer && !includeOlderGameVersion) continue;

                    jobs.Add(new InstallJob
                    {
                        ModId = group.ModId,
                        TargetVersion = offer.TargetVersion,
                        ExistingKeys = new List<string>(group.Keys)
                    });
                }

                if (jobs.Count == 0) return 0;

                foreach (InstallJob job in jobs) InstallErrors.Remove(job.ModId);

                StartJobs(jobs, viewedProfileId, "Updating mods", onFinished: null);

                BetterModsModSystem.Logger?.Notification("[BetterMods] Installing " + jobs.Count + " mod update(s): " +
                    string.Join(", ", jobs.Select(j => j.ModId + "@" + j.TargetVersion)));

                return jobs.Count;
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Error("[BetterMods] Could not start installing mod updates.");
                BetterModsModSystem.Logger?.Error(e);
                return 0;
            }
        }

        /// <summary>
        /// Downloads dependency releases with the same hardened installer as updates (blocklist,
        /// validated file name, allowed hosts, archive identity, never overwriting) and folds each
        /// into the profiles with <see cref="ProfileStore.AdoptInstalledVersions"/> - the viewed profile
        /// switching it ON, every other profile pinned so nothing changes for it.
        /// <para>
        /// <paramref name="onFinished"/> runs on the main thread once every download has finished and
        /// the menu's mod list has been reloaded, with one result per release. False when nothing was
        /// started (another install running, the config unreadable, nothing to do, or in a world - the
        /// menu's mod list cannot be refreshed there). Main thread only. Never throws.
        /// </para>
        /// </summary>
        public static bool StartDependencyInstall(IEnumerable<DependencyRelease> releases, string viewedProfileId, Action<List<DependencyInstallResult>> onFinished)
        {
            try
            {
                if (IsInstalling || releases == null || ProfileStore.PersistenceSuspended) return false;
                if (BetterModsModSystem.ClientApi != null) return false;

                List<InstallJob> jobs = new List<InstallJob>();
                HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (DependencyRelease release in releases)
                {
                    if (release == null || string.IsNullOrEmpty(release.ModId) || string.IsNullOrEmpty(release.Version)) continue;
                    if (!seen.Add(release.ModId)) continue;

                    jobs.Add(new InstallJob
                    {
                        ModId = release.ModId,
                        TargetVersion = release.Version,
                        ExistingKeys = new List<string>(release.ExistingKeys ?? new List<string>()),
                        EnableInViewed = true
                    });
                }

                if (jobs.Count == 0) return false;

                StartJobs(jobs, viewedProfileId, "Installing dependencies", jobList =>
                {
                    List<DependencyInstallResult> results = jobList.Select(j => new DependencyInstallResult
                    {
                        ModId = j.ModId,
                        Version = j.TargetVersion,
                        InstalledKey = j.InstalledKey,
                        Error = j.InstalledKey == null ? j.Error ?? "unexpected error" : null
                    }).ToList();

                    onFinished?.Invoke(results);
                });

                BetterModsModSystem.Logger?.Notification("[BetterMods] Installing " + jobs.Count + " dependenc" + (jobs.Count == 1 ? "y" : "ies") + ": " +
                    string.Join(", ", jobs.Select(j => j.ModId + "@" + j.TargetVersion)));

                return true;
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Error("[BetterMods] Could not start installing dependencies.");
                BetterModsModSystem.Logger?.Error(e);
                return false;
            }
        }

        /// <summary>The shared start of an install run. Caller has checked <see cref="IsInstalling"/> and built the jobs.</summary>
        private static void StartJobs(List<InstallJob> jobs, string viewedProfileId, string label, Action<List<InstallJob>> onFinished)
        {
            foreach (InstallJob job in jobs) Installing.Add(job.ModId);

            InstallTotal = jobs.Count;
            InstallLabel = label;
            Volatile.Write(ref installFinished, 0);
            InstallSummary = null;
            InstallSummaryIsError = false;

            string modDbUrl = ClientSettings.ModDbUrl;
            string modsFolder = GamePaths.DataPathMods;
            // 1.22 has no ClientSettings.MaxParallelModDownloads; 3 is that setting's default on 1.23.
            int parallel = DefaultParallelDownloads;

            Task.Run(() => RunInstallAsync(jobs, modDbUrl, modsFolder, parallel, viewedProfileId))
                .ContinueWith(task => Post(() => FinishInstall(jobs, task, onFinished)), TaskScheduler.Default);

            RaiseChangedNextFrame();
        }

        private static async Task RunInstallAsync(List<InstallJob> jobs, string modDbUrl, string modsFolder, int parallel, string viewedProfileId)
        {
            SweepStaleTempFiles(modsFolder);

            // IsModBlocked answers true for EVERYTHING until the list is in; wait for it rather than
            // either refusing every install or installing past it.
            if (ModDbUtil.ModBlockList == null)
            {
                await Task.Run(() => ModDbUtil.ModBlockListMRE.WaitOne(TimeSpan.FromSeconds(15))).ConfigureAwait(false);
            }

            if (ModDbUtil.ModBlockList == null)
            {
                FailAll(jobs, "the mod blocklist has not loaded yet - try again shortly");
                return;
            }

            // Fresh file details for exactly the release being installed. The check only learned its
            // version; a versioned spec answers with that release's own fileName/fileUrl.
            Dictionary<string, ModDbEntry> files = new Dictionary<string, ModDbEntry>(StringComparer.OrdinalIgnoreCase);
            List<string> specs = jobs.Select(j => ModUpdateRules.ReleaseSpec(j.ModId, j.TargetVersion)).Where(s => s != null).ToList();
            foreach (List<string> chunk in ModUpdateRules.Chunk(specs, ModUpdateRules.SpecsPerRequest))
            {
                try
                {
                    Dictionary<string, ModDbEntry> answer = await ModDbClient.QueryAsync(modDbUrl, chunk, GameVersionChecked, CancellationToken.None).ConfigureAwait(false);
                    foreach (KeyValuePair<string, ModDbEntry> entry in answer)
                    {
                        if (files.TryGetValue(entry.Key, out ModDbEntry existing) && existing.ErrorCode == 0 && entry.Value.ErrorCode != 0) continue;
                        files[entry.Key] = entry.Value;
                    }
                }
                catch (ModDbException e)
                {
                    foreach (string spec in chunk)
                    {
                        InstallJob job = jobs.FirstOrDefault(j => string.Equals(ModUpdateRules.ReleaseSpec(j.ModId, j.TargetVersion), spec, StringComparison.OrdinalIgnoreCase));
                        if (job != null) job.Error = e.Message;
                    }
                }
            }

            using SemaphoreSlim gate = new SemaphoreSlim(parallel, parallel);
            List<Task> running = new List<Task>();
            foreach (InstallJob job in jobs)
            {
                if (job.Error != null)
                {
                    MarkFinished();
                    continue;
                }

                await gate.WaitAsync().ConfigureAwait(false);
                running.Add(Task.Run(async () =>
                {
                    try
                    {
                        files.TryGetValue(job.ModId, out ModDbEntry entry);
                        await InstallOneAsync(job, entry, modDbUrl, modsFolder).ConfigureAwait(false);
                    }
                    catch (Exception e)
                    {
                        job.Error ??= "unexpected error (" + ModUpdateRules.AsciiLine(e.Message, 80) + ")";
                        BetterModsModSystem.Logger?.Error("[BetterMods] Installing " + job.ModId + "@" + job.TargetVersion + " failed.");
                        BetterModsModSystem.Logger?.Error(e);
                    }
                    finally
                    {
                        gate.Release();
                        MarkFinished();
                    }

                    // The file is in place: fold it into the profiles on the very next frame, not at
                    // the end of the whole run - until this lands, the new version is ON in every
                    // profile that does not disable the mod outright.
                    if (job.InstalledKey != null)
                    {
                        string viewed = viewedProfileId;
                        Post(() => Adopt(job, viewed));
                    }
                }));
            }

            await Task.WhenAll(running).ConfigureAwait(false);
        }

        private static void MarkFinished()
        {
            Interlocked.Increment(ref installFinished);
            Post(RaiseChanged);
        }

        private static void FailAll(List<InstallJob> jobs, string reason)
        {
            foreach (InstallJob job in jobs)
            {
                job.Error ??= reason;
                MarkFinished();
            }
        }

        /// <summary>
        /// One mod: validate what the DB told us, download, verify, move into place. Sets
        /// <see cref="InstallJob.InstalledKey"/> on success and <see cref="InstallJob.Error"/> on
        /// failure; the temp file never outlives this method.
        /// </summary>
        private static async Task InstallOneAsync(InstallJob job, ModDbEntry entry, string modDbUrl, string modsFolder)
        {
            if (entry == null)
            {
                job.Error = "the mod DB did not answer for this release";
                return;
            }

            if (entry.IsRetracted)
            {
                job.Error = "that release has been retracted";
                return;
            }

            if (entry.ErrorCode != 0)
            {
                job.Error = "the mod DB does not offer that release (code " + entry.ErrorCode + ")";
                return;
            }

            if (ModDbUtil.IsModBlocked(job.ModId, job.TargetVersion, out string blockReason))
            {
                job.Error = "that release is on the mod blocklist (" + ModUpdateRules.AsciiLine(blockReason, 80) + ")";
                return;
            }

            string fileName = ModUpdateRules.ValidateFileName(entry.FileName, out string reason);
            if (fileName == null)
            {
                job.Error = reason;
                return;
            }

            Uri uri = ModUpdateRules.ResolveDownloadUri(modDbUrl, entry.FileUrl, out reason);
            if (uri == null)
            {
                job.Error = reason;
                return;
            }

            Directory.CreateDirectory(modsFolder);
            string destination = Path.Combine(modsFolder, fileName);

            // Some authors reuse one file name across releases. Never overwrite: the file there may be
            // the very version a profile is pinned to.
            if (File.Exists(destination) || Directory.Exists(destination))
            {
                job.Error = "a file named " + ModUpdateRules.AsciiLine(fileName, 80) + " is already in the Mods folder";
                return;
            }

            string temp = Path.Combine(modsFolder, TempPrefix + Guid.NewGuid().ToString("N") + TempSuffix);

            try
            {
                await ModDbClient.DownloadAsync(uri, temp, CancellationToken.None).ConfigureAwait(false);

                string installedKey = VerifyArchive(temp, job.ModId, job.TargetVersion, out reason);
                if (installedKey == null)
                {
                    job.Error = reason;
                    return;
                }

                // overwrite: false throws if the name was taken while we downloaded.
                File.Move(temp, destination, overwrite: false);
                job.InstalledKey = installedKey;
            }
            catch (ModDbException e)
            {
                job.Error = e.Message;
            }
            catch (IOException e)
            {
                job.Error = "the file could not be moved into the Mods folder (" + ModUpdateRules.AsciiLine(e.Message, 80) + ")";
            }
            catch (UnauthorizedAccessException)
            {
                job.Error = "the Mods folder is not writable";
            }
            finally
            {
                TryDelete(temp);
            }
        }

        private const string TempPrefix = ".bettermods-";
        private const string TempSuffix = ".part";

        /// <summary>
        /// Opens the download as a zip and reads its <c>modinfo.json</c> exactly as
        /// <c>ModContainer.LoadModInfo</c> does for a zip mod (root entry, <c>JsonConvert</c> into
        /// <see cref="ModInfo"/>). The mod id and version must be the ones requested. Returns the
        /// <c>modid@version</c> key the game will address it by, or null with a reason.
        /// </summary>
        private static string VerifyArchive(string path, string modId, string version, out string reason)
        {
            reason = null;

            try
            {
                using ZipArchive zip = ZipFile.OpenRead(path);
                ZipArchiveEntry entry = zip.GetEntry("modinfo.json");
                if (entry == null || entry.Length > 1024 * 1024)
                {
                    reason = "the download has no usable modinfo.json";
                    return null;
                }

                string json;
                using (StreamReader reader = new StreamReader(entry.Open()))
                {
                    json = reader.ReadToEnd();
                }

                ModInfo info = JsonConvert.DeserializeObject<ModInfo>(json);
                if (info == null || string.IsNullOrEmpty(info.ModID) || string.IsNullOrEmpty(info.Version))
                {
                    reason = "the download's modinfo.json is incomplete";
                    return null;
                }

                if (!string.Equals(info.ModID, modId, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(info.Version.Trim(), version, StringComparison.OrdinalIgnoreCase))
                {
                    reason = "the download is " + ModUpdateRules.AsciiLine(info.ModID + "@" + info.Version, 60) + ", not the release that was asked for";
                    return null;
                }

                return info.ModID + "@" + info.Version;
            }
            catch (InvalidDataException)
            {
                reason = "the download is not a valid zip";
                return null;
            }
            catch (JsonException)
            {
                reason = "the download's modinfo.json could not be read";
                return null;
            }
            catch (Exception e)
            {
                reason = "the download could not be checked (" + ModUpdateRules.AsciiLine(e.Message, 80) + ")";
                return null;
            }
        }

        /// <summary>
        /// Main thread, the frame after a file landed: the profile transaction for that one mod. Runs
        /// whatever the player is doing, screen open or not, because the file is already installed and
        /// the profiles must not be left treating it as on everywhere.
        /// </summary>
        private static void Adopt(InstallJob job, string viewedProfileId)
        {
            ProfileStore.AdoptInstalledVersions(viewedProfileId,
                new[] { new KeyValuePair<List<string>, string>(job.ExistingKeys, job.InstalledKey) },
                enableInViewed: job.EnableInViewed);

            // A profile that has the mod OFF still "switches": its row now shows the new version and
            // switching it on enables that one.
            ModGrouping.RememberSelection(viewedProfileId, job.ModId, job.InstalledKey);
        }

        private static void FinishInstall(List<InstallJob> jobs, Task task, Action<List<InstallJob>> onFinished)
        {
            if (task.IsFaulted)
            {
                BetterModsModSystem.Logger?.Error("[BetterMods] Installing mods failed.");
                BetterModsModSystem.Logger?.Error(task.Exception?.GetBaseException());
            }

            int installed = 0;
            List<InstallJob> failed = new List<InstallJob>();
            foreach (InstallJob job in jobs)
            {
                Installing.Remove(job.ModId);

                if (job.InstalledKey != null)
                {
                    installed++;
                    continue;
                }

                job.Error ??= "unexpected error";
                failed.Add(job);
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not install " + job.ModId + "@" + job.TargetVersion + ": " + job.Error);

                // The card's "last update attempt failed" line belongs to update runs; a dependency
                // run reports through its own summary.
                if (onFinished == null) InstallErrors[job.ModId] = job.Error;
            }

            if (onFinished == null)
            {
                if (failed.Count == 0)
                {
                    InstallSummary = installed == 1 ? "Updated 1 mod" : "Updated " + installed + " mods";
                    InstallSummaryIsError = false;
                }
                else
                {
                    InstallSummary = (installed > 0 ? "Updated " + installed + ", " : "") +
                                     (failed.Count == 1
                                         ? "could not update " + failed[0].ModId + ": " + failed[0].Error
                                         : failed.Count + " failed (" + failed[0].ModId + ": " + failed[0].Error + ")");
                    InstallSummaryIsError = true;
                }
            }

            // The new versions are only listed once the menu's mod list is rebuilt. Doing that while a
            // world runs is vanilla-safe but pointless, and the menu list is what both surfaces read,
            // so in that case it is left to the next time the mods screen opens.
            if (installed > 0)
            {
                if (BetterModsModSystem.ClientApi == null)
                {
                    try
                    {
                        ClientProgram.screenManager?.loadMods();
                    }
                    catch (Exception e)
                    {
                        menuReloadPending = true;
                        BetterModsModSystem.Logger?.Error("[BetterMods] Could not reload the mod list after installing updates.");
                        BetterModsModSystem.Logger?.Error(e);
                    }
                }
                else
                {
                    menuReloadPending = true;
                }
            }

            // After the reload, so the callback re-reads a mod list that has the new files in it.
            if (onFinished != null)
            {
                try
                {
                    onFinished(jobs);
                }
                catch (Exception e)
                {
                    BetterModsModSystem.Logger?.Error("[BetterMods] Finishing an install run failed.");
                    BetterModsModSystem.Logger?.Error(e);
                }
            }

            RaiseChanged();
        }

        /// <summary>True once (and then false) when an install finished while the menu list could not be reloaded.</summary>
        public static bool ConsumeMenuReload()
        {
            bool pending = menuReloadPending;
            menuReloadPending = false;
            return pending;
        }

        /// <summary>
        /// Removes temp files a previous run left behind (a crash or a closed game mid-download). Only
        /// ours, and only old ones, in case another client shares this data folder and is mid-install.
        /// </summary>
        private static void SweepStaleTempFiles(string modsFolder)
        {
            try
            {
                if (!Directory.Exists(modsFolder)) return;

                foreach (string file in Directory.EnumerateFiles(modsFolder, TempPrefix + "*" + TempSuffix))
                {
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > TimeSpan.FromHours(1)) TryDelete(file);
                }
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not clear old update downloads: " + e.Message);
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not delete '" + path + "': " + e.Message);
            }
        }

        // ---------------------------------------------------------------- plumbing

        private static string FullPathOrNull(string path)
        {
            try
            {
                return string.IsNullOrWhiteSpace(path) ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool IsUnder(string path, string folder)
        {
            if (folder == null || string.IsNullOrWhiteSpace(path)) return false;

            string full = FullPathOrNull(path);
            if (full == null) return false;

            return string.Equals(full, folder, StringComparison.OrdinalIgnoreCase)
                   || full.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                   || full.StartsWith(folder + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Runs <paramref name="action"/> on the main thread, the next time the frame loop drains its
        /// queue. That queue invokes actions bare, so a throw would take the game down - hence the
        /// wrapper.
        /// </summary>
        private static void Post(Action action)
        {
            ScreenManager.EnqueueMainThreadTask(() =>
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    BetterModsModSystem.Logger?.Error("[BetterMods] A mod update task failed on the main thread.");
                    BetterModsModSystem.Logger?.Error(e);
                }
            });
        }

        /// <summary>
        /// For changes made from inside a click handler or a compose: the surfaces react by rebuilding,
        /// which must not happen in the middle of either.
        /// </summary>
        private static void RaiseChangedNextFrame()
        {
            Post(RaiseChanged);
        }

        private static void RaiseChanged()
        {
            Action handlers = Changed;
            if (handlers == null) return;

            foreach (Delegate handler in handlers.GetInvocationList())
            {
                try
                {
                    ((Action)handler)();
                }
                catch (Exception e)
                {
                    BetterModsModSystem.Logger?.Error("[BetterMods] A mod update listener failed.");
                    BetterModsModSystem.Logger?.Error(e);
                }
            }
        }
    }
}
