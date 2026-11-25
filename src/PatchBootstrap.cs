using System;
using System.Collections.Generic;
using System.IO;
using BetterMods.Profiles;
using HarmonyLib;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace BetterMods
{
    /// <summary>
    /// The single entry point that brings BetterMods' patches up, whoever asks for it.
    /// <para>
    /// Two callers, deliberately taking the identical path:
    /// </para>
    /// <list type="bullet">
    /// <item><see cref="BetterModsModSystem.StartClientSide"/>, i.e. the first world load of the
    /// session — the only route the game itself offers, because it executes no mod code before a
    /// world loads.</item>
    /// <item>the optional <c>BetterModsHook</c> .NET startup hook, reflectively, while the game is
    /// still starting — which is what makes the main-menu tabs appear on a cold launch.</item>
    /// </list>
    /// <para>
    /// It is therefore idempotent and safe to call twice, in either order, and safe to call from a
    /// caller that has no <c>ICoreClientAPI</c>: it touches only static state.
    /// </para>
    /// <para>
    /// NOTE: there is deliberately no counterpart that unpatches. <c>ModLoader.Dispose()</c> runs on
    /// every world exit (SystemModHandler.cs:237-242), so an <c>UnpatchAll</c> there would tear the
    /// main-menu patches down again every time the player returns to the menu — the exact thing this
    /// class exists to make available. The patches are process-lifetime by design, and the startup
    /// hook makes that rule matter more, not less: with it, the patches are installed once before the
    /// first world and must survive every world after it.
    /// </para>
    /// <para>
    /// Third parties do not always keep that rule. <c>Conquest VS Edition</c>'s <c>CTMMod.dll</c>
    /// calls <c>harmony.UnpatchAll(null)</c> from its own <c>Dispose()</c>, and Harmony's owner check
    /// treats a null id as "matches everything", so that one line removes the patches of EVERY mod in
    /// the process — ours included — on every return to the main menu. <see cref="ScheduleRearm"/>
    /// exists for exactly that: BetterMods cannot stop another mod unpatching it, so it notices and
    /// puts itself back.
    /// </para>
    /// </summary>
    public static class PatchBootstrap
    {
        private static readonly object Lock = new object();

        /// <summary>So the "you switched BetterMods off" line is written once per session, not per call.</summary>
        private static bool disabledNoticeLogged;

        /// <summary>
        /// Loads the profile store if it is not loaded yet, then installs the Harmony patches if they
        /// are not installed yet. Never throws for a profile-store problem; a Harmony failure is left
        /// to propagate to the caller, both of which wrap it.
        /// <para>
        /// Does nothing at all when the player has switched BetterMods off in the mod manager. That
        /// check lives here rather than in the startup hook because this is the one place with the
        /// game's own parsed <c>ClientSettings</c> in hand: the hook would have to re-derive the data
        /// path, re-parse clientsettings.json and re-implement the id matching, and three of those
        /// four steps are things it could get subtly wrong. Here there is exactly one authority, and
        /// it covers both callers.
        /// </para>
        /// </summary>
        public static void ApplyPatches()
        {
            lock (Lock)
            {
                if (IsDisabledInSettings())
                {
                    if (!disabledNoticeLogged)
                    {
                        disabledNoticeLogged = true;
                        BetterModsModSystem.Logger?.Notification(
                            "[BetterMods] Switched off in the mod manager; not installing any patches. " +
                            "(The startup hook honours this too - it does not need to be unset.)");
                    }
                    return;
                }

                EnsureProfileStore();

                // Keeps ModConfig\BetterMods\BetterModsHook.dll identical to the hook embedded in this
                // assembly - from either caller, once per session, never throwing. See
                // StartupHookInstaller for why that path and how a running hook is replaced.
                Startup.StartupHookInstaller.EnsureInstalledOnce();

                // Cheap re-entry guard that also covers the case where the patches were installed by
                // the other caller, before this class' own state existed (they share the id).
                if (Harmony.HasAnyPatches(BetterModsModSystem.HarmonyId)) return;

                // The Harmony instance is deliberately not kept: nothing may ever unpatch (see above),
                // and HasAnyPatches is the only state this class needs to consult.
                PatchEachClassSeparately(new Harmony(BetterModsModSystem.HarmonyId));
            }
        }

        /// <summary>
        /// What <c>Harmony.PatchAll(assembly)</c> does, one patch class at a time, with each class'
        /// failure caught.
        /// <para>
        /// The <c>[HarmonyPrepare]</c> guards only cover METHOD-level drift: a renamed method is
        /// looked up with <c>AccessTools</c> and the patch is skipped. They cannot cover TYPE-level
        /// drift, because BetterMods compiles against the <c>VintagestoryLib</c> types directly - a
        /// renamed or moved type throws <c>TypeLoadException</c> when Harmony reads the
        /// <c>[HarmonyPatch(typeof(...))]</c> attribute, before any <c>Prepare()</c> can run.
        /// <c>PatchAll</c> would abandon the whole set at that point, so a rename in one game type
        /// would take down the patches on every other one as well. Per-class isolation makes the
        /// blast radius one patch class, which is what the guards were supposed to buy.
        /// </para>
        /// <para>
        /// <c>Harmony.CreateClassProcessor(Type)</c>, <c>PatchClassProcessor.Patch()</c> and
        /// <c>AccessTools.GetTypesFromAssembly(Assembly)</c> are all public in the 2.4.2 that the game
        /// ships, and this is the same pair of calls <c>PatchAll</c> makes.
        /// </para>
        /// <para>
        /// Every type in the assembly is offered, unfiltered. Harmony's patch CATEGORIES are
        /// deliberately not used: <c>CreateClassProcessor</c> ignores them, and the only APIs that
        /// honour them - <c>PatchCategory</c> and <c>PatchAllUncategorized</c> - are single-call
        /// variants of <c>PatchAll</c> and would bring back exactly the all-or-nothing failure this
        /// loop exists to avoid. The patch classes therefore carry no
        /// <c>[HarmonyPatchCategory]</c>; <see cref="BetterModsModSystem.HarmonyId"/> is the OWNER
        /// id, which is what <c>Harmony.HasAnyPatches</c> and any unpatch would key on.
        /// </para>
        /// </summary>
        private static void PatchEachClassSeparately(Harmony harmony)
        {
            // Deliberately outside the loop's try: if the assembly's types cannot be enumerated at
            // all there is nothing to isolate, and the caller should see it.
            Type[] types = AccessTools.GetTypesFromAssembly(typeof(PatchBootstrap).Assembly);

            foreach (Type type in types)
            {
                try
                {
                    harmony.CreateClassProcessor(type).Patch();
                }
                catch (Exception e)
                {
                    BetterModsModSystem.Logger?.Error(
                        "[BetterMods] Patch class " + type.FullName + " could not be applied on this game " +
                        "version; the feature it provides stays vanilla and the other patches are unaffected.");
                    BetterModsModSystem.Logger?.Error(e);
                }
            }
        }

        // ---------------------------------------------------------------- re-arm

        /// <summary>
        /// Queues a "are our patches still installed?" check onto the next rendered frame.
        /// <para>
        /// Called from <see cref="BetterModsModSystem.Dispose"/>, i.e. from inside
        /// <c>ModLoader.Dispose()</c> on world exit, which is the only moment another mod's
        /// <c>Dispose()</c> can run an <c>UnpatchAll</c>. It must not do the check inline: mod systems
        /// are disposed in an order this mod does not control, so the offender may not have run yet.
        /// </para>
        /// <para>
        /// <c>ScreenManager.MainThreadTasks</c> is drained at the top of
        /// <c>ScreenManager.OnNewFrame</c>, before <c>Render(dt)</c> (ScreenManager.cs:641,672-679),
        /// and <c>OnNewFrame</c> is the root frame handler (ClientPlatformWindows.cs:487) - so it runs
        /// in the main menu, which is exactly where the check is needed and where nothing else of this
        /// mod is executing. Deferring by one frame also guarantees <c>ModLoader.Dispose()</c> has
        /// fully returned, so every other mod's <c>Dispose()</c> is already behind us regardless of
        /// order. <c>EnqueueMainThreadTask</c> is public and static, so no API object is needed.
        /// </para>
        /// </summary>
        public static void ScheduleRearm()
        {
            try
            {
                ScreenManager.EnqueueMainThreadTask(RearmIfUnpatched);
            }
            catch (Exception e)
            {
                // Worst case the player sees the pre-fix behaviour: patches gone until the next world.
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not schedule the patch check: " + e.Message);
            }
        }

        /// <summary>
        /// Runs on the game's main thread, one frame after a world was left. Nothing here may throw:
        /// <c>ScreenManager.OnNewFrame</c> invokes queued tasks with no guard of its own.
        /// </summary>
        private static void RearmIfUnpatched()
        {
            try
            {
                // A world came up again in the meantime, so StartClientSide already owns this and a
                // transpile while the world is loading is not something to do for no reason.
                if (BetterModsModSystem.ClientApi != null) return;

                if (Harmony.HasAnyPatches(BetterModsModSystem.HarmonyId)) return;

                BetterModsModSystem.Logger?.Notification(
                    "[BetterMods] Another mod removed every Harmony patch in the process (an UnpatchAll " +
                    "with no owner id, which matches all owners). Re-installing BetterMods' patches.");

                ApplyPatches();
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Error("[BetterMods] Could not re-install the patches after a world exit.");
                BetterModsModSystem.Logger?.Error(e);
            }
        }

        // ---------------------------------------------------------------- disabled check

        /// <summary>
        /// True if the player has switched THIS copy of BetterMods off. <c>ClientSettings.DisabledMods</c>
        /// holds <c>"modid@version"</c> entries (ClientSettings.cs:1396-1406), and
        /// <c>ModLoader.DisableMods</c> also honours a bare <c>modid</c> (ModLoader.cs:325). Only the
        /// bare id and the running copy's own <c>bettermods@&lt;version&gt;</c> count: with two
        /// versions installed and the older one switched off, the entry for the older one must not
        /// stop the newer one patching. <see cref="SelfDisableRule"/> holds the decision.
        /// <para>
        /// Fails open. A settings read that goes wrong must not silently turn the mod off - that is
        /// the failure mode this whole check exists to remove.
        /// </para>
        /// </summary>
        private static bool IsDisabledInSettings()
        {
            try
            {
                List<string> disabled = ClientSettings.DisabledMods;
                if (disabled == null || disabled.Count == 0) return false;

                return SelfDisableRule.IsSwitchedOff(disabled, BetterModsModSystem.ModId, RunningVersion());
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning(
                    "[BetterMods] Could not read the disabled-mods setting, carrying on as enabled: " + e.Message);
            }

            return false;
        }

        private static bool runningVersionResolved;
        private static string runningVersion;

        /// <summary>
        /// The <c>version</c> of the running copy, from the <c>modinfo.json</c> beside this assembly,
        /// or null if it cannot be read. Resolved once per process; never throws.
        /// <para>
        /// Both load paths put that file there: the game (<c>ModAssemblyLoader.LoadFrom</c>) and the
        /// startup hook both <c>Assembly.UnsafeLoadFrom</c> the dll out of the mod's folder - the
        /// mod folder itself, or for a zip the <c>Cache\unpack\...</c> folder the zip was extracted
        /// to, which holds the archive's own <c>modinfo.json</c>. <c>Location</c> is empty only for
        /// an assembly loaded from bytes, which neither path does (only compiled <c>.cs</c> mods
        /// are); that case is handled anyway, as "unknown".
        /// </para>
        /// </summary>
        private static string RunningVersion()
        {
            if (runningVersionResolved) return runningVersion;
            runningVersionResolved = true;

            try
            {
                string location = typeof(PatchBootstrap).Assembly.Location;
                string directory = string.IsNullOrEmpty(location) ? null : Path.GetDirectoryName(location);
                string modInfoPath = directory == null ? null : Path.Combine(directory, "modinfo.json");

                if (modInfoPath != null && File.Exists(modInfoPath))
                {
                    runningVersion = SelfDisableRule.ReadVersion(File.ReadAllText(modInfoPath));
                }

                if (runningVersion == null)
                {
                    BetterModsModSystem.Logger?.Warning(
                        "[BetterMods] Could not determine this copy's version (no readable modinfo.json beside '" +
                        location + "'); only a bare '" + BetterModsModSystem.ModId +
                        "' entry in the disabled mods will switch BetterMods off this session.");
                }
            }
            catch (Exception e)
            {
                runningVersion = null;
                BetterModsModSystem.Logger?.Warning(
                    "[BetterMods] Could not determine this copy's version (" + e.Message + "); only a bare '" +
                    BetterModsModSystem.ModId + "' entry in the disabled mods will switch BetterMods off this session.");
            }

            return runningVersion;
        }

        private static void EnsureProfileStore()
        {
            try
            {
                ProfileStore.EnsureLoaded();
            }
            catch (Exception e)
            {
                // ProfileStore already swallows its own failures; this is belt-and-braces so a store
                // problem can never stop the patches from being installed.
                BetterModsModSystem.Logger?.Error("[BetterMods] Failed to load the profile store.");
                BetterModsModSystem.Logger?.Error(e);
            }
        }
    }
}
