using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace BetterMods.Updates
{
    /// <summary>
    /// Every release the mod DB lists for a mod, for the version picker's not-installed rows.
    /// <para>
    /// Lazy: one v1 <c>api/mod/&lt;modid&gt;</c> request (the endpoint <see cref="ModPageLinks"/> also
    /// uses) the first time a mod's picker is opened, never at startup and never for a mod the update
    /// check did not find on the DB (<see cref="ModUpdateService.IsKnownToModDb"/> - which already
    /// leaves out core mods, the bundled ones, anything under the game install and BetterMods
    /// itself). The answer is cached for the session; a failed request is retried on a later open,
    /// at most every <see cref="RetryAfter"/>. "Check again" on the mods screen forgets the cache.
    /// </para>
    /// <para>
    /// All state is main-thread only; the request runs on the pool and posts back. Every completed
    /// request bumps <see cref="Stamp"/>, which an open picker polls to redraw itself.
    /// </para>
    /// </summary>
    public static class ModReleaseCatalog
    {
        private sealed class Entry
        {
            public ReleaseListState State;
            public List<ModRelease> Releases;
            public DateTime FailedAtUtc;
            public int Generation;
        }

        private static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(30);

        private static readonly Dictionary<string, Entry> Entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        private static int generation;

        /// <summary>Changes whenever any release list arrives, fails, or the cache is cleared.</summary>
        public static int Stamp { get; private set; }

        /// <summary>
        /// Whether the picker may list DB releases for <paramref name="modId"/> at all - the same rule
        /// the update check and the "Open mod page" button use.
        /// </summary>
        public static bool IsEligible(string modId)
        {
            return !string.IsNullOrEmpty(modId) && ModUpdateService.IsKnownToModDb(modId);
        }

        public static ReleaseListState StateOf(string modId)
        {
            if (string.IsNullOrEmpty(modId)) return ReleaseListState.NotRequested;
            return Entries.TryGetValue(modId, out Entry entry) ? entry.State : ReleaseListState.NotRequested;
        }

        /// <summary>The loaded release list, or null when it is not (yet) loaded.</summary>
        public static IReadOnlyList<ModRelease> ReleasesOf(string modId)
        {
            if (string.IsNullOrEmpty(modId)) return null;
            return Entries.TryGetValue(modId, out Entry entry) && entry.State == ReleaseListState.Loaded ? entry.Releases : null;
        }

        /// <summary>
        /// Starts fetching <paramref name="modId"/>'s release list unless it is loaded, loading, known
        /// not to be on the DB, failed very recently, or the mod is not eligible. Main thread only.
        /// Never throws.
        /// </summary>
        public static void Request(string modId)
        {
            try
            {
                if (!IsEligible(modId)) return;

                if (Entries.TryGetValue(modId, out Entry existing))
                {
                    if (existing.State != ReleaseListState.Failed) return;
                    if (DateTime.UtcNow - existing.FailedAtUtc < RetryAfter) return;
                }

                Entry entry = new Entry { State = ReleaseListState.Loading, Generation = generation };
                Entries[modId] = entry;
                Stamp++;

                // Read on the main thread: ClientSettings is not ours to touch from the pool.
                string modDbUrl = ClientSettings.ModDbUrl;

                Task.Run(() => ModDbClient.FetchModAsync(modDbUrl, modId, CancellationToken.None))
                    .ContinueWith(task => Post(() => Finish(modId, entry, task)), TaskScheduler.Default);
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not ask the mod DB for the versions of " + modId + ": " + e.Message);
            }
        }

        /// <summary>Forgets every cached list ("Check again"). A request still in flight is dropped when it lands. Main thread.</summary>
        public static void Clear()
        {
            Entries.Clear();
            generation++;
            Stamp++;
        }

        private static void Finish(string modId, Entry entry, Task<string> task)
        {
            // Superseded by Clear() or a retry: someone else owns the slot now.
            if (entry.Generation != generation || !Entries.TryGetValue(modId, out Entry current) || current != entry) return;

            Stamp++;

            if (task.IsFaulted || task.IsCanceled)
            {
                string reason = task.Exception?.GetBaseException() is ModDbException known ? known.Message : "unexpected error";
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not read the mod DB versions of " + modId + ": " + reason);
                entry.State = ReleaseListState.Failed;
                entry.FailedAtUtc = DateTime.UtcNow;
                return;
            }

            // FetchModAsync answers null for an HTTP 404; the DB itself answers 200 {"statuscode":"404"}.
            List<ModRelease> releases = ModReleaseRules.ParseReleases(task.Result, out bool notOnDb);
            if (task.Result == null || notOnDb)
            {
                entry.State = ReleaseListState.NotOnDb;
                return;
            }

            if (releases == null)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] The mod DB's version list for " + modId + " could not be read.");
                entry.State = ReleaseListState.Failed;
                entry.FailedAtUtc = DateTime.UtcNow;
                return;
            }

            entry.Releases = releases;
            entry.State = ReleaseListState.Loaded;
        }

        /// <summary>
        /// Next frame, main thread. The queue invokes actions bare, so a throw would take the game
        /// down - hence the wrapper.
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
                    BetterModsModSystem.Logger?.Error("[BetterMods] Reading a mod's DB versions failed.");
                    BetterModsModSystem.Logger?.Error(e);
                }
            });
        }
    }
}
