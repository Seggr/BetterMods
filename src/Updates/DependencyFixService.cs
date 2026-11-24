using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterMods.Profiles;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;
using Vintagestory.Common;
using Vintagestory.ModDb;

namespace BetterMods.Updates
{
    /// <summary>
    /// The unmet dependencies of every row a surface is about to draw, computed once per rebuild from
    /// that rebuild's groups (the viewed profile's state). Read-only.
    /// </summary>
    public sealed class DependencyView
    {
        private readonly Dictionary<string, DepMod> mods;

        internal DependencyView(Dictionary<string, DepMod> mods)
        {
            this.mods = mods ?? new Dictionary<string, DepMod>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>The unmet direct dependencies of <paramref name="group"/>, empty when it is off or has none. Never throws.</summary>
        public List<DependencyIssue> IssuesFor(ModVersionGroup group)
        {
            if (string.IsNullOrEmpty(group?.ModId) || !mods.TryGetValue(group.ModId, out DepMod mod)) return new List<DependencyIssue>();
            return DependencyRules.Issues(mod, mods, GameVersion.ShortGameVersion);
        }
    }

    /// <summary>What a surface lends the dependency fixer for one run.</summary>
    public sealed class DependencyFixSurface
    {
        /// <summary>Fresh groups for a profile - re-read after every download round. Required.</summary>
        public System.Func<string, List<ModVersionGroup>> Groups;

        /// <summary>
        /// Runs after the fixer has switched a group on (or to another version) in the profile - the
        /// main menu mirrors that into <c>ClientSettings</c> when the profile is the active one,
        /// exactly as its own toggle does. Optional.
        /// </summary>
        public Action<string, ModVersionGroup> AfterProfileEdit;

        /// <summary>Downloads are allowed (main menu only).</summary>
        public bool CanDownload;

        /// <summary>
        /// Asks the player (title, VTML body, confirm label, answer). Required for anything that needs a
        /// confirmation; without it such a step is declined.
        /// </summary>
        public Action<string, string, string, Action<bool>> Confirm;

        /// <summary>The one-line outcome (and progress), with whether it is a problem. Required.</summary>
        public Action<string, bool> Report;
    }

    /// <summary>
    /// "Fix dependencies": makes one mod's declared dependencies load in the VIEWED profile, and only
    /// there.
    /// <para>
    /// <b>Algorithm.</b> Rounds of plan / act / re-read. Each round re-reads the profile's groups,
    /// asks <see cref="DependencyRules.Plan"/> what is missing (transitively, cycle-guarded, depth
    /// capped), switches on the newest satisfying installed versions with the existing selection logic
    /// (<see cref="ModGrouping.SelectVersion"/> + <see cref="ModGrouping.SetEnabled"/>), and - main
    /// menu only - downloads what is not installed with the update installer
    /// (<see cref="ModUpdateService.StartDependencyInstall"/>), whose profile transaction switches the
    /// viewed profile to each new file and pins every other profile. A downloaded mod's own
    /// dependencies are only known once it is on disk, so the next round picks them up. A mod is
    /// downloaded at most once per run, and at most <see cref="MaxRounds"/> download rounds run.
    /// </para>
    /// <para>
    /// <b>Which release.</b> The same tiers as updates: the newest stable release tagged for the
    /// running game version if it satisfies the requirement; otherwise the newest tagged for the newest
    /// older stable game version, which is only installed after an explicit confirmation. Never a
    /// blocked release.
    /// </para>
    /// <para>
    /// <b>Refusals.</b> Read-only profile config; a mod that is off; another install running;
    /// game-provided requirements (the <c>game</c> mod, core mods - reported, never touched); a cycle
    /// that would need a different version of the mod being fixed; nothing on the DB; nothing
    /// published that is new enough; a blocked release; a declined confirmation; a download that
    /// fails. Everything refused is listed in the outcome - nothing is half-applied silently.
    /// </para>
    /// <para>Main thread only, except the DB lookup, which posts back like the update check.</para>
    /// </summary>
    public static class DependencyFixService
    {
        /// <summary>Most download rounds one run makes before giving up on deeper chains.</summary>
        public const int MaxRounds = 4;

        private sealed class FixRun
        {
            public string RootModId;
            public string ProfileId;
            public DependencyFixSurface Surface;
            public int Rounds;

            public readonly List<string> Enabled = new List<string>();
            public readonly List<string> Installed = new List<string>();
            public readonly Dictionary<string, string> Failed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<string> Attempted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly List<string> Notes = new List<string>();
        }

        private sealed class Resolution
        {
            public DependencyDownload Need;
            public string Version;
            public string OlderGameVersion;
            public string Error;
        }

        private static FixRun running;

        /// <summary>The last outcome per root mod id, for its card's tooltip. Session only.</summary>
        private static readonly Dictionary<string, List<string>> LastOutcomes = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        public static bool IsRunning => running != null;

        /// <summary>The mod whose dependencies are being fixed right now, or null.</summary>
        public static string RunningModId => running?.RootModId;

        /// <summary>What the last fix of <paramref name="modId"/> could not do, as ASCII lines; empty when it all worked or never ran.</summary>
        public static IReadOnlyList<string> LastProblemsFor(string modId)
        {
            if (string.IsNullOrEmpty(modId) || !LastOutcomes.TryGetValue(modId, out List<string> lines)) return Array.Empty<string>();
            return lines;
        }

        // ---------------------------------------------------------------- view

        /// <summary>The dependency state of <paramref name="groups"/> (one profile's), for the cards. Never throws.</summary>
        public static DependencyView BuildView(IEnumerable<ModVersionGroup> groups)
        {
            try
            {
                return new DependencyView(Snapshot(groups));
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not work out mod dependencies: " + e.Message);
                return new DependencyView(null);
            }
        }

        /// <summary>The plain-value model <see cref="DependencyRules"/> works on.</summary>
        private static Dictionary<string, DepMod> Snapshot(IEnumerable<ModVersionGroup> groups)
        {
            Dictionary<string, DepMod> mods = new Dictionary<string, DepMod>(StringComparer.OrdinalIgnoreCase);
            if (groups == null) return mods;

            foreach (ModVersionGroup group in groups)
            {
                if (string.IsNullOrEmpty(group?.ModId) || mods.ContainsKey(group.ModId)) continue;

                List<DepVersion> versions = new List<DepVersion>();
                bool coreMod = false;
                for (int i = 0; i < group.Versions.Count; i++)
                {
                    ModContainer mod = group.Versions[i];
                    ModInfo info = mod?.Info;
                    coreMod |= info?.CoreMod == true;

                    versions.Add(new DepVersion(
                        i < group.Keys.Count ? group.Keys[i] : null,
                        info?.Version,
                        mod?.Error == ModError.Blocked,
                        DependenciesOf(info)));
                }

                mods[group.ModId] = new DepMod(group.ModId, coreMod, group.Enabled, group.SelectedIndex, versions);
            }

            return mods;
        }

        private static List<KeyValuePair<string, string>> DependenciesOf(ModInfo info)
        {
            List<KeyValuePair<string, string>> dependencies = new List<KeyValuePair<string, string>>();
            if (info?.Dependencies == null) return dependencies;

            foreach (ModDependency dependency in info.Dependencies)
            {
                if (dependency == null || string.IsNullOrWhiteSpace(dependency.ModID)) continue;
                dependencies.Add(new KeyValuePair<string, string>(dependency.ModID, dependency.Version ?? ""));
            }

            return dependencies;
        }

        // ---------------------------------------------------------------- run

        /// <summary>
        /// Starts fixing <paramref name="rootModId"/>'s dependencies in <paramref name="profileId"/>.
        /// The first round (everything installed) runs inline - call it from a click handler only if
        /// the surface defers its rebuild, which both do. Returns false when nothing was started (the
        /// reason has been reported). Never throws.
        /// </summary>
        public static bool Start(string rootModId, string profileId, DependencyFixSurface surface)
        {
            if (surface?.Groups == null || surface.Report == null || string.IsNullOrEmpty(rootModId)) return false;

            try
            {
                if (running != null)
                {
                    surface.Report("Already fixing the dependencies of " + running.RootModId + " - wait for it to finish", true);
                    return false;
                }

                if (ProfileStore.PersistenceSuspended)
                {
                    surface.Report("Not fixing dependencies - profile config unreadable", true);
                    return false;
                }

                if (surface.CanDownload && ModUpdateService.IsInstalling)
                {
                    surface.Report("Not fixing dependencies while mods are being installed - try again when it finishes", true);
                    return false;
                }

                running = new FixRun { RootModId = rootModId, ProfileId = profileId, Surface = surface };
                BetterModsModSystem.Logger?.Notification("[BetterMods] Fixing the dependencies of " + rootModId + " in profile " + profileId + ".");

                Round(running);
                return true;
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Error("[BetterMods] Fixing dependencies failed.");
                BetterModsModSystem.Logger?.Error(e);
                running = null;
                SafeReport(surface, "Fixing dependencies failed (see the log)", true);
                return false;
            }
        }

        /// <summary>One plan / act step. Ends the run, or hands off to the DB lookup.</summary>
        private static void Round(FixRun run)
        {
            List<ModVersionGroup> groups = run.Surface.Groups(run.ProfileId) ?? new List<ModVersionGroup>();
            Dictionary<string, DepMod> mods = Snapshot(groups);

            if (!mods.TryGetValue(run.RootModId, out DepMod root) || !root.Enabled)
            {
                if (run.Enabled.Count == 0 && run.Installed.Count == 0)
                {
                    run.Failed[run.RootModId] = "it is not switched on in this profile";
                }

                Finish(run);
                return;
            }

            DependencyPlan plan = DependencyRules.Plan(run.RootModId, mods, GameVersion.ShortGameVersion);

            foreach (string note in plan.Notes)
            {
                if (!run.Notes.Contains(note)) run.Notes.Add(note);
            }

            foreach (KeyValuePair<string, string> unfixable in plan.Unfixable)
            {
                run.Failed[unfixable.Key] = unfixable.Value;
            }

            foreach (KeyValuePair<string, string> enable in plan.Enable)
            {
                ApplyEnable(run, groups, enable.Key, enable.Value);
            }

            List<DependencyDownload> downloads = new List<DependencyDownload>();
            foreach (DependencyDownload download in plan.Download)
            {
                if (run.Failed.ContainsKey(download.ModId)) continue;

                if (run.Attempted.Contains(download.ModId))
                {
                    // Installed this run and still not enough (or its install failed): never loop.
                    run.Failed[download.ModId] = "still not satisfied after installing it (needs " + DependencyRules.Display(download.Required) + ")";
                    continue;
                }

                downloads.Add(download);
            }

            if (downloads.Count == 0)
            {
                Finish(run);
                return;
            }

            if (!run.Surface.CanDownload)
            {
                foreach (DependencyDownload download in downloads)
                {
                    run.Failed[download.ModId] = (download.ExistingKeys.Count == 0 ? "not installed" : "no installed version is new enough (needs " + DependencyRules.Display(download.Required) + ")")
                                                 + " - download it with Fix dependencies on the Mods screen in the main menu";
                }

                Finish(run);
                return;
            }

            if (run.Rounds >= MaxRounds)
            {
                foreach (DependencyDownload download in downloads)
                {
                    run.Failed[download.ModId] = "the dependency chain is too long to fix in one go - press Fix dependencies again";
                }

                Finish(run);
                return;
            }

            run.Rounds++;
            run.Surface.Report("Fixing dependencies of " + run.RootModId + ": looking up " + string.Join(", ", downloads.Select(d => d.ModId)) + " on the mod DB...", false);

            string modDbUrl = ClientSettings.ModDbUrl;
            string gameVersion = ModUpdateService.GameVersionChecked;
            string older = ModUpdateService.OlderGameVersion;

            Task.Run(() => ResolveAsync(downloads, modDbUrl, gameVersion, older))
                .ContinueWith(task => Post(() => OnResolved(run, task)), TaskScheduler.Default);
        }

        /// <summary>Switches one dependency on in the profile, at the planned version.</summary>
        private static void ApplyEnable(FixRun run, List<ModVersionGroup> groups, string modId, string key)
        {
            ModVersionGroup group = groups.FirstOrDefault(g => string.Equals(g?.ModId, modId, StringComparison.OrdinalIgnoreCase));
            int index = group == null ? -1 : group.Keys.FindIndex(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                run.Failed[modId] = "its installed version could not be selected";
                return;
            }

            ModGrouping.SelectVersion(run.ProfileId, group, index);
            ModGrouping.SetEnabled(run.ProfileId, group, true);

            try
            {
                run.Surface.AfterProfileEdit?.Invoke(run.ProfileId, group);
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not mirror a dependency fix into the client settings: " + e.Message);
            }

            if (!run.Enabled.Contains(key)) run.Enabled.Add(key);
        }

        // ---------------------------------------------------------------- the mod DB

        /// <summary>
        /// Background: picks a release per needed dependency. Tier 1 is one batched bare-id query at the
        /// running game version; tier 2, for whatever tier 1 could not satisfy, one more at the newest
        /// older stable game version. Never throws for a DB problem - those become per-mod errors.
        /// </summary>
        private static async Task<List<Resolution>> ResolveAsync(List<DependencyDownload> needs, string modDbUrl, string gameVersion, string knownOlder)
        {
            List<Resolution> resolutions = needs.Select(n => new Resolution { Need = n }).ToList();

            Dictionary<string, ModDbEntry> tier1 = await QueryAsync(resolutions, modDbUrl, gameVersion).ConfigureAwait(false);
            Dictionary<string, string> newestSeen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (Resolution resolution in resolutions)
            {
                if (resolution.Error != null) continue;
                string version = Candidate(tier1, resolution.Need.ModId);
                if (version == null) continue;

                newestSeen[resolution.Need.ModId] = version + " for " + gameVersion;
                if (DependencyRules.Satisfies(resolution.Need.Required, version)) resolution.Version = version;
            }

            List<Resolution> rest = resolutions.Where(r => r.Error == null && r.Version == null).ToList();
            string older = knownOlder;
            if (rest.Count > 0 && older == null)
            {
                try
                {
                    older = await ModUpdateService.FetchOlderGameVersionAsync(modDbUrl, gameVersion, CancellationToken.None).ConfigureAwait(false);
                }
                catch (ModDbException e)
                {
                    BetterModsModSystem.Logger?.Warning("[BetterMods] Could not read the mod DB's game version list: " + e.Message);
                }
            }

            if (rest.Count > 0 && older != null)
            {
                Dictionary<string, ModDbEntry> tier2 = await QueryAsync(rest, modDbUrl, older).ConfigureAwait(false);
                foreach (Resolution resolution in rest)
                {
                    if (resolution.Error != null) continue;
                    string version = Candidate(tier2, resolution.Need.ModId);
                    if (version == null) continue;

                    if (!newestSeen.ContainsKey(resolution.Need.ModId)) newestSeen[resolution.Need.ModId] = version + " for " + older;
                    if (DependencyRules.Satisfies(resolution.Need.Required, version))
                    {
                        resolution.Version = version;
                        resolution.OlderGameVersion = older;
                    }
                }
            }

            bool blocklistLoaded = ModDbUtil.ModBlockList != null;
            foreach (Resolution resolution in resolutions)
            {
                if (resolution.Error != null) continue;

                if (resolution.Version == null)
                {
                    resolution.Error = newestSeen.TryGetValue(resolution.Need.ModId, out string seen)
                        ? "needs " + DependencyRules.Display(resolution.Need.Required) + ", but the newest published release is " + seen
                        : "not found on the mod DB for " + gameVersion + (older != null ? " or " + older : "");
                    continue;
                }

                if (resolution.Need.ExistingKeys.Any(k => string.Equals(k, resolution.Need.ModId + "@" + resolution.Version, StringComparison.OrdinalIgnoreCase)))
                {
                    resolution.Error = "release " + resolution.Version + " is installed but cannot be used";
                    resolution.Version = null;
                    continue;
                }

                try
                {
                    if (blocklistLoaded && ModDbUtil.IsModBlocked(resolution.Need.ModId, resolution.Version, out string reason))
                    {
                        resolution.Error = "release " + resolution.Version + " is on the mod blocklist (" + ModUpdateRules.AsciiLine(reason, 80) + ")";
                        resolution.Version = null;
                    }
                }
                catch (Exception)
                {
                    // The installer checks the blocklist again, with the list loaded, before writing.
                }
            }

            return resolutions;
        }

        /// <summary>The release a bare-id answer names, or null for "nothing" (not found, retracted, unparseable).</summary>
        private static string Candidate(Dictionary<string, ModDbEntry> answer, string modId)
        {
            if (answer == null || !answer.TryGetValue(modId, out ModDbEntry entry) || entry.ErrorCode != 0) return null;

            string version = entry.RecommendedUpgrade?.Trim();
            return ModUpdateRules.IsDbVersion(version) ? version : null;
        }

        /// <summary>Bare-id queries for <paramref name="resolutions"/>, chunked; a failed chunk marks its mods with the error.</summary>
        private static async Task<Dictionary<string, ModDbEntry>> QueryAsync(List<Resolution> resolutions, string modDbUrl, string gameVersion)
        {
            Dictionary<string, ModDbEntry> merged = new Dictionary<string, ModDbEntry>(StringComparer.OrdinalIgnoreCase);
            List<string> specs = ModUpdateRules.BuildSpecs(resolutions.Select(r => new InstalledModSnapshot(r.Need.ModId, "")), versioned: false);

            foreach (Resolution resolution in resolutions)
            {
                if (!specs.Contains(resolution.Need.ModId, StringComparer.OrdinalIgnoreCase)) resolution.Error ??= "its mod id cannot be looked up on the mod DB";
            }

            foreach (List<string> chunk in ModUpdateRules.Chunk(specs, ModUpdateRules.SpecsPerRequest))
            {
                try
                {
                    Dictionary<string, ModDbEntry> answer = await ModDbClient.QueryAsync(modDbUrl, chunk, gameVersion, CancellationToken.None).ConfigureAwait(false);
                    foreach (KeyValuePair<string, ModDbEntry> entry in answer)
                    {
                        if (merged.TryGetValue(entry.Key, out ModDbEntry existing) && existing.ErrorCode == 0 && entry.Value.ErrorCode != 0) continue;
                        merged[entry.Key] = entry.Value;
                    }
                }
                catch (ModDbException e)
                {
                    foreach (Resolution resolution in resolutions)
                    {
                        if (chunk.Contains(resolution.Need.ModId, StringComparer.OrdinalIgnoreCase)) resolution.Error ??= e.Message;
                    }
                }
            }

            return merged;
        }

        /// <summary>Main thread: the lookup answered. Confirms tier-2 picks, then installs.</summary>
        private static void OnResolved(FixRun run, Task<List<Resolution>> task)
        {
            if (run != running) return;

            if (task.IsFaulted || task.IsCanceled)
            {
                BetterModsModSystem.Logger?.Error("[BetterMods] Looking up dependencies on the mod DB failed.");
                if (task.Exception != null) BetterModsModSystem.Logger?.Error(task.Exception.GetBaseException());
                run.Failed["(mod DB)"] = "the lookup failed unexpectedly";
                Finish(run);
                return;
            }

            List<Resolution> current = new List<Resolution>();
            List<Resolution> older = new List<Resolution>();
            foreach (Resolution resolution in task.Result)
            {
                if (resolution.Error != null) run.Failed[resolution.Need.ModId] = resolution.Error;
                else if (resolution.OlderGameVersion != null) older.Add(resolution);
                else current.Add(resolution);
            }

            if (older.Count == 0)
            {
                Install(run, current);
                return;
            }

            if (run.Surface.Confirm == null)
            {
                foreach (Resolution resolution in older) run.Failed[resolution.Need.ModId] = OlderNotInstalled(resolution, "it needs a confirmation on the Mods screen");
                Install(run, current);
                return;
            }

            string gameVersion = ModUpdateService.GameVersionChecked;
            string olderVersion = older[0].OlderGameVersion;

            System.Text.StringBuilder body = new System.Text.StringBuilder();
            body.Append("<strong>").Append(Vtml(run.RootModId)).Append("</strong> needs ")
                .Append(older.Count == 1 ? "a dependency" : older.Count + " dependencies")
                .Append(" that ").Append(older.Count == 1 ? "has" : "have").Append(" no suitable release for game version ")
                .Append(Vtml(gameVersion)).Append(". The newest releases for ").Append(Vtml(olderVersion)).Append(" would work:\n\n");

            foreach (Resolution resolution in older)
            {
                body.Append("  - ").Append(Vtml(resolution.Need.ModId)).Append(' ').Append(Vtml(resolution.Version))
                    .Append(" (needs ").Append(Vtml(DependencyRules.Display(resolution.Need.Required))).Append(")\n");
            }

            if (current.Count > 0)
            {
                body.Append("\nAlso installed either way (made for ").Append(Vtml(gameVersion)).Append("): ")
                    .Append(Vtml(string.Join(", ", current.Select(r => r.Need.ModId + " " + r.Version)))).Append('\n');
            }

            body.Append("\nThey are tagged for ").Append(Vtml(olderVersion)).Append(" and untested on ").Append(Vtml(gameVersion))
                .Append(". Install them anyway? Only this profile will use them.");

            try
            {
                run.Surface.Confirm(
                    "Install releases made for " + olderVersion + "?",
                    body.ToString(),
                    "Install",
                    confirmed =>
                    {
                        try
                        {
                            if (run != running) return;

                            if (!confirmed)
                            {
                                foreach (Resolution resolution in older) run.Failed[resolution.Need.ModId] = OlderNotInstalled(resolution, "declined");
                            }
                            else
                            {
                                current.AddRange(older);
                            }

                            Install(run, current);
                        }
                        catch (Exception e)
                        {
                            Abort(run, e);
                        }
                    });
            }
            catch (Exception e)
            {
                Abort(run, e);
            }
        }

        private static string OlderNotInstalled(Resolution resolution, string why)
        {
            return "only release " + resolution.Version + " for " + resolution.OlderGameVersion + " would work, not installed (" + why + ")";
        }

        private static void Install(FixRun run, List<Resolution> picks)
        {
            if (picks.Count == 0)
            {
                Finish(run);
                return;
            }

            List<DependencyRelease> releases = picks.Select(r => new DependencyRelease
            {
                ModId = r.Need.ModId,
                Version = r.Version,
                ExistingKeys = new List<string>(r.Need.ExistingKeys)
            }).ToList();

            foreach (DependencyRelease release in releases) run.Attempted.Add(release.ModId);

            run.Surface.Report("Fixing dependencies of " + run.RootModId + ": installing " + string.Join(", ", releases.Select(r => r.ModId + " " + r.Version)) + "...", false);

            bool started = ModUpdateService.StartDependencyInstall(releases, run.ProfileId, results =>
            {
                try
                {
                    if (run != running) return;

                    foreach (DependencyInstallResult result in results)
                    {
                        if (result.InstalledKey != null) run.Installed.Add(result.InstalledKey);
                        else run.Failed[result.ModId] = "the download of " + result.Version + " failed: " + result.Error;
                    }

                    // Re-read and re-plan: the new files' own dependencies are known now.
                    Round(run);
                }
                catch (Exception e)
                {
                    Abort(run, e);
                }
            });

            if (!started)
            {
                foreach (DependencyRelease release in releases)
                {
                    run.Failed[release.ModId] = "it could not be downloaded right now (another install is running, or the profile config is unreadable) - try again";
                }

                Finish(run);
            }
        }

        // ---------------------------------------------------------------- outcome

        private static void Finish(FixRun run)
        {
            if (run != running) return;
            running = null;

            List<string> problems = new List<string>();
            foreach (KeyValuePair<string, string> failure in run.Failed) problems.Add(failure.Key + ": " + failure.Value);
            foreach (string note in run.Notes) problems.Add("game-provided, not fixable here: " + note);

            LastOutcomes[run.RootModId] = problems;

            List<string> done = new List<string>();
            if (run.Enabled.Count > 0) done.Add("switched on " + string.Join(", ", run.Enabled));
            if (run.Installed.Count > 0) done.Add("installed " + string.Join(", ", run.Installed));
            string doneText = string.Join("; ", done);

            string summary;
            bool isError = problems.Count > 0;
            if (!isError)
            {
                summary = done.Count == 0
                    ? "Dependencies of " + run.RootModId + " are already satisfied"
                    : "Fixed dependencies of " + run.RootModId + ": " + doneText;
            }
            else
            {
                summary = "Could not fix " + (problems.Count == 1 ? problems[0] : problems.Count + " dependencies of " + run.RootModId + " (" + problems[0] + ", ...)")
                          + (done.Count > 0 ? "; " + doneText : "");
            }

            BetterModsModSystem.Logger?.Notification("[BetterMods] Dependency fix for " + run.RootModId + ": " +
                (done.Count > 0 ? doneText : "nothing changed") + ".");
            foreach (string problem in problems)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods]   not fixed - " + problem);
            }

            SafeReport(run.Surface, summary, isError);
        }

        private static void Abort(FixRun run, Exception e)
        {
            BetterModsModSystem.Logger?.Error("[BetterMods] Fixing dependencies failed.");
            BetterModsModSystem.Logger?.Error(e);
            run.Failed["(error)"] = "unexpected error, see the log";
            Finish(run);
        }

        private static void SafeReport(DependencyFixSurface surface, string text, bool isError)
        {
            try
            {
                surface?.Report?.Invoke(text, isError);
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not report a dependency fix: " + e.Message);
            }
        }

        private static string Vtml(string text)
        {
            return ModUpdateRules.AsciiLine(text, 120).Replace("<", "&lt;").Replace(">", "&gt;");
        }

        /// <summary>Main thread, next frame, wrapped - the queue invokes actions bare.</summary>
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
                    BetterModsModSystem.Logger?.Error("[BetterMods] A dependency fix task failed on the main thread.");
                    BetterModsModSystem.Logger?.Error(e);
                    running = null;
                }
            });
        }
    }
}
