using System;
using System.Collections.Generic;
using BetterMods.Gui;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;
using Vintagestory.Common;

namespace BetterMods.Profiles
{
    /// <summary>
    /// Binds a mod profile to a savegame, records the mods that savegame actually loaded with, and
    /// decides, on every singleplayer start, whether the player needs to be asked about a mismatch.
    /// <para>
    /// <c>ScreenManager.ConnectToSingleplayer(StartServerArgs)</c> (ScreenManager.cs:1023) is the one
    /// choke point every singleplayer start goes through — normal start, corrupt-save repair, database
    /// upgrade, both routes out of <c>GuiScreenSingleplayerModify</c>, new world creation and the
    /// command-line auto-start — which is why the gate lives there rather than on the private
    /// <c>GuiScreenSingleplayer.OnClickCellLeft</c>.
    /// </para>
    /// <para>
    /// Two signals are kept per savegame and they answer different questions. The BINDING is what the
    /// player chose. The MOD LIST is what the world was observed to need, and is therefore the stronger
    /// of the two: it is captured from a load that actually finished, it survives the player renaming
    /// or deleting profiles, and it travels with a copied world through the savegame mirror.
    /// </para>
    /// <para>
    /// Every failure path here proceeds with vanilla behaviour. Nothing in this class may stop a world
    /// from loading.
    /// </para>
    /// </summary>
    public static class SavegameProfileGate
    {
        /// <summary>
        /// The one place a profile is applied on the way into a world, and the reason it has to be
        /// here. <c>ClientSettings.DisabledMods</c>'s getter hands out the LIVE list out of the
        /// settings dictionary rather than a copy (<c>SettingsClass&lt;T&gt;</c> indexer, verified on
        /// the 1.22.5 dll; unchanged in the 1.23.0 decompile), and every caller of <c>ConnectToSingleplayer</c> assigns that very
        /// reference to <c>StartServerArgs.DisabledMods</c>. <c>ProfileStore.ApplyProfile</c> writes a
        /// NEW list into the setting, so anything captured earlier is left pointing at the old one:
        /// the client would load the new set (<c>SystemModHandler.SinglePlayerStart</c> re-reads
        /// <c>ClientSettings.DisabledMods</c> at world load) while the integrated server loaded the
        /// stale one (<c>ServerConfig.WorldConfig = serverargs.Clone()</c> is a MemberwiseClone and
        /// shares the list). Re-pointing the field is what keeps the two sides identical.
        /// </summary>
        private static readonly AccessTools.FieldRef<StartServerArgs, List<string>> DisabledModsRef =
            GameFields.Ref<StartServerArgs, List<string>>("DisabledMods");

        /// <summary>
        /// <c>StartServerArgs.RepairMode</c> is internal (StartServerArgs.cs:46) and is the only thing
        /// that distinguishes the corrupt-save repair start (GuiScreenSingleplayer.cs:203) from a
        /// normal one. A world the player has just been told is damaged must not be written to by us
        /// before vanilla has repaired it. A miss here degrades to "assume not repairing", which is
        /// the pre-existing behaviour.
        /// </summary>
        private static readonly AccessTools.FieldRef<StartServerArgs, bool> RepairModeRef =
            GameFields.Ref<StartServerArgs, bool>("RepairMode");

        /// <summary>
        /// <c>ScreenManager.CurrentScreen</c> is internal, and we only want it as the prompt's parent
        /// screen (for event bubbling). A null parent is harmless, so a miss here is not fatal.
        /// </summary>
        private static readonly AccessTools.FieldRef<ScreenManager, GuiScreen> CurrentScreenRef =
            GameFields.Ref<ScreenManager, GuiScreen>("CurrentScreen");

        /// <summary>
        /// Carries the player's answer across the re-entry into <c>ConnectToSingleplayer</c>. The
        /// profile is applied on the second pass, inside the prefix, so it lands before the original
        /// body runs and before <c>serverargs</c> is re-pointed at the new disabled-mod list.
        /// </summary>
        private sealed class PendingDecision
        {
            public StartServerArgs Args;
            public string ApplyProfileId;
            public SavegameFileInfo Info;
            public string SaveFilePath;

            /// <summary>Whether the start that produced this decision was a corrupt-save repair.</summary>
            public bool RepairMode;
        }

        /// <summary>What the menu-side check concluded about one world start.</summary>
        private sealed class GateDecision
        {
            public ProfileMismatchPrompt Prompt;
            public string SuggestedProfileId;
        }

        private static PendingDecision pending;

        /// <summary>
        /// Identifier of the world the menu-side gate last let through. The in-game side reads it to
        /// know the player has already been shown, and has already answered, whatever it was about to
        /// mention in chat. Only ever set on a start that reached the prefix, i.e. never on the first
        /// world of an unhooked cold launch — which is exactly the case the chat notice exists for.
        /// </summary>
        private static string gatedSavegameId;

        /// <summary>How long after the world is up the cold-launch notice waits, in milliseconds.</summary>
        private const int NoticeDelayMs = 1500;

        /// <summary>How many mod ids the chat notice names before it summarises the rest.</summary>
        private const int NoticeListLimit = 6;

        // ---------------------------------------------------------------- menu side

        /// <summary>
        /// Returns false to swallow the start and show the mismatch prompt instead; true to let
        /// vanilla proceed.
        /// </summary>
        public static bool OnConnectToSingleplayer(ScreenManager screenManager, StartServerArgs serverargs)
        {
            // FIRST, before any prompt or re-entry: a menu-only custom playstyle code must never reach
            // the server (unknown code = NullReferenceException in SaveGame.SetNewWorldConfig). Swaps
            // in the base playstyle; the preset itself travels in WorldConfiguration and MapSizeY.
            // Idempotent, so the re-entry after a mismatch prompt is a no-op.
            try
            {
                Playstyles.CustomPlaystyles.RemapForWorldStart(screenManager, serverargs);
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Error("[BetterMods] Custom playstyle remap failed.");
                BetterModsModSystem.Logger?.Error(e);
            }

            // Always consumed, so a decision that somehow never got used cannot leak into a later start.
            PendingDecision decision = pending;
            pending = null;

            try
            {
                if (serverargs == null) return true;

                if (decision != null && ReferenceEquals(decision.Args, serverargs))
                {
                    Proceed(decision.ApplyProfileId, decision.Info, decision.SaveFilePath, serverargs, decision.RepairMode);
                    return true;
                }

                // Anything the game enabled or disabled behind our back belongs to the active profile
                // before that profile is compared against a world or pushed into a world start.
                ProfileStore.ReconcileActiveFromClientSettings();

                bool repairMode = RepairModeRef != null && RepairModeRef(serverargs);

                string path = serverargs.SaveFileLocation;
                SavegameFileInfo info = SavegameFile.Read(path);

                // Nothing readable: a world about to be created, or a file we could not open. There is
                // nothing to compare against, and the in-game recorder binds it once it is up.
                if (!info.Ok || string.IsNullOrWhiteSpace(info.SavegameId))
                {
                    SyncServerArgs(serverargs);
                    return true;
                }

                ModProfile active = ProfileStore.ActiveProfile;
                if (active == null)
                {
                    Proceed(null, info, path, serverargs, repairMode);
                    return true;
                }

                GateDecision gate = Evaluate(screenManager, info, path, active);
                if (gate == null)
                {
                    Proceed(null, info, path, serverargs, repairMode);
                    return true;
                }

                GuiScreenProfileMismatch prompt = new GuiScreenProfileMismatch(
                    gate.Prompt,
                    choice => OnChoice(screenManager, serverargs, info, path, gate.SuggestedProfileId, repairMode, choice),
                    screenManager,
                    CurrentScreenRef != null ? CurrentScreenRef(screenManager) : null);

                screenManager.LoadScreen(prompt);
                return false;
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Error("[BetterMods] Profile binding check failed; starting the world as vanilla would.");
                BetterModsModSystem.Logger?.Error(e);

                try
                {
                    SyncServerArgs(serverargs);
                }
                catch (Exception)
                {
                    // Nothing left to do; vanilla's own capture is still in place.
                }

                return true;
            }
        }

        /// <summary>
        /// Decides whether this start is worth asking about, and if so what the prompt should say.
        /// Returns null for "just start it".
        /// <para>
        /// Three conditions can each raise the prompt, and they overlap constantly:
        /// </para>
        /// <list type="bullet">
        /// <item>mods the world loaded with will not be enabled — the only real warning;</item>
        /// <item>another profile enables exactly the world's recorded list;</item>
        /// <item>the world is bound to a profile other than the active one (the original behaviour,
        /// and all that is available for a world with no recorded list yet).</item>
        /// </list>
        /// <para>
        /// Version differences and extra mods deliberately raise nothing: a different version of the
        /// same mod usually loads fine, and extra mods are the normal state of a world you have since
        /// added things to. Both are still reported on a prompt raised for another reason.
        /// </para>
        /// <para>
        /// An active profile that is already an exact match short-circuits everything, including a
        /// stale binding: the observed mod list is the stronger signal and it says this is right.
        /// </para>
        /// </summary>
        private static GateDecision Evaluate(ScreenManager screenManager, SavegameFileInfo info, string path, ModProfile active)
        {
            string boundId = ProfileStore.GetBoundProfileId(info.SavegameId, path) ?? ValidatedMirror(info);

            // The local record first; a world copied here from elsewhere only has the savegame mirror,
            // which is raw deserialised JSON and may hold blanks.
            List<string> worldList = Clean(ProfileStore.GetModList(info.SavegameId, path) ?? info.MirroredModList);

            ModListComparison comparison = null;
            ProfileMatch suggestion = null;

            // An installed list that is empty or known-incomplete means the lookup failed, not that
            // the player has no mods. Every mod the world
            // used would then read as missing, which is the one wrong answer worth guarding against —
            // and it is the answer that raises the loudest prompt.
            List<ModContainer> installed = null;
            if (worldList != null)
            {
                installed = ProfileMatcher.InstalledMods(screenManager, out bool reliable);
                if (!reliable)
                {
                    BetterModsModSystem.Logger?.Warning(
                        "[BetterMods] The installed mod list is incomplete; comparing this world's mod list would be meaningless. " +
                        "Falling back to the profile binding alone.");
                    installed = null;
                    worldList = null;
                }

                // A list recorded on 1.22 can name server-only mods, which 1.23's client never loads
                // and EnabledKeys therefore never predicts. See ProfileMatcher.ClientLoadable.
                worldList = ProfileMatcher.ClientLoadable(worldList, installed);
            }

            List<string> activeEnabled = worldList == null ? null : ProfileMatcher.EnabledKeys(installed, active.Id);
            if (worldList != null && activeEnabled == null)
            {
                BetterModsModSystem.Logger?.Warning(
                    "[BetterMods] Could not work out what the active profile enables; falling back to the profile binding alone.");
                worldList = null;
            }

            if (worldList != null)
            {
                comparison = WorldModList.Compare(worldList, activeEnabled);

                if (comparison.IsExactMatch) return null;

                suggestion = ProfileMatcher.Suggest(installed, worldList, active.Id, boundId);
            }
            else if (boundId != null && boundId != active.Id)
            {
                ModProfile bound = ProfileStore.FindProfile(boundId);
                if (bound != null) suggestion = new ProfileMatch { Profile = bound, IsBound = true };
            }

            bool missing = comparison != null && comparison.HasWarning;
            if (!missing && suggestion == null) return null;

            ProfileMismatchPrompt prompt = new ProfileMismatchPrompt
            {
                WorldName = info.WorldName,
                ActiveProfileName = active.Name,
                SuggestedProfileName = suggestion?.Profile?.Name,
                SuggestedIsExact = suggestion != null && suggestion.IsExact,
                SuggestedIsBound = suggestion != null && suggestion.IsBound,
                ExactMatchCount = suggestion?.ExactCount ?? 0,
                RecordedModCount = worldList?.Count ?? -1
            };

            if (comparison != null)
            {
                prompt.Missing.AddRange(comparison.Missing);
                prompt.Differences.AddRange(comparison.Differences);
            }

            // Only worth a line of its own when the suggestion is not already about the binding.
            if (boundId != null && boundId != active.Id && (suggestion == null || !suggestion.IsBound))
            {
                prompt.BoundProfileName = ProfileStore.FindProfile(boundId)?.Name;
            }

            return new GateDecision { Prompt = prompt, SuggestedProfileId = suggestion?.Profile?.Id };
        }

        private static void OnChoice(
            ScreenManager screenManager,
            StartServerArgs serverargs,
            SavegameFileInfo info,
            string saveFilePath,
            string suggestedProfileId,
            bool repairMode,
            ProfileMismatchChoice choice)
        {
            try
            {
                if (choice == ProfileMismatchChoice.Cancel)
                {
                    // Back to the world list. It is a cached screen, so LoadScreen never disposed it
                    // and this just re-runs its OnScreenLoaded (ScreenManager.cs:884-907).
                    screenManager.LoadAndCacheScreen(typeof(GuiScreenSingleplayer));
                    return;
                }

                pending = new PendingDecision
                {
                    Args = serverargs,
                    ApplyProfileId = choice == ProfileMismatchChoice.SwitchToSuggested ? suggestedProfileId : null,
                    Info = info,
                    SaveFilePath = saveFilePath,
                    RepairMode = repairMode
                };

                // Re-enter the patched method; the prefix picks the decision back up. Vanilla starts
                // worlds from a button handler in exactly this way (GuiScreenSingleplayer.cs:186-199).
                screenManager.ConnectToSingleplayer(serverargs);
            }
            catch (Exception e)
            {
                pending = null;
                BetterModsModSystem.Logger?.Error("[BetterMods] Could not act on the profile prompt; returning to the world list.");
                BetterModsModSystem.Logger?.Error(e);

                try
                {
                    screenManager.LoadAndCacheScreen(typeof(GuiScreenSingleplayer));
                }
                catch (Exception)
                {
                    // The menu is already in a bad way; do not make it worse.
                }
            }
        }

        /// <summary>
        /// Applies the chosen profile (if any), records the binding, and re-points
        /// <c>serverargs.DisabledMods</c> at whatever <c>ClientSettings</c> now holds. Order matters:
        /// apply first, sync second.
        /// </summary>
        private static void Proceed(
            string applyProfileId, SavegameFileInfo info, string saveFilePath, StartServerArgs serverargs, bool repairMode)
        {
            if (!string.IsNullOrWhiteSpace(applyProfileId))
            {
                ProfileStore.ApplyProfile(applyProfileId);
            }

            SyncServerArgs(serverargs);

            try
            {
                RecordBinding(info, saveFilePath, repairMode);

                // This start went through the menu, so whatever there was to say has been said.
                if (info != null && info.Ok) gatedSavegameId = info.SavegameId;
            }
            catch (Exception e)
            {
                // A binding is a convenience. Never let it cost the player their world.
                BetterModsModSystem.Logger?.Error("[BetterMods] Could not record the profile binding for this world.");
                BetterModsModSystem.Logger?.Error(e);
            }
        }

        /// <summary>
        /// Binds the world to the active profile, adopts a mod list that only the savegame carried, and
        /// refreshes the in-savegame mirror when it has drifted.
        /// <para>
        /// The local binding is recorded on every start. The in-savegame MIRROR is not: it is skipped
        /// for a read-only file, for a world on an older database version (vanilla is about to convert
        /// it) and for a corrupt-save repair start (the player has just been told this world is
        /// damaged and vanilla has not repaired it yet). Writing a convenience field into a save in
        /// either of those states is a risk taken for nothing.
        /// </para>
        /// </summary>
        private static void RecordBinding(SavegameFileInfo info, string saveFilePath, bool repairMode)
        {
            if (info == null || !info.Ok || string.IsNullOrWhiteSpace(info.SavegameId)) return;

            ModProfile active = ProfileStore.ActiveProfile;
            if (active == null) return;

            ProfileStore.BindSavegame(info.SavegameId, saveFilePath, info.WorldName, active.Id);

            // A world copied from another machine arrives with its mod list inside the savegame and
            // nothing in the local config. Adopt it, so the in-game recorder has something to compare
            // the next load against even before it overwrites it.
            List<string> stored = ProfileStore.GetModList(info.SavegameId, saveFilePath);
            if (stored == null && info.MirroredModList != null)
            {
                ProfileStore.RecordModList(info.SavegameId, saveFilePath, info.MirroredModList);
                stored = ProfileStore.GetModList(info.SavegameId, saveFilePath);
            }

            // The mirror only has to be rewritten when it disagrees, which keeps the common start out
            // of the database entirely. A read-only file is skipped: never write while the world is
            // in use somewhere.
            if (info.IsReadOnly) return;
            if (string.Equals(info.MirroredProfileId, active.Id, StringComparison.Ordinal) &&
                string.Equals(info.MirroredProfileName ?? "", active.Name ?? "", StringComparison.Ordinal) &&
                (stored == null || SameList(info.MirroredModList, stored)))
            {
                return;
            }

            // Checked last, so a start that was not going to write anything anyway stays silent.
            if (repairMode || info.NeedsUpgrade)
            {
                BetterModsModSystem.Logger?.Notification(
                    "[BetterMods] Not mirroring the profile into '" + saveFilePath + "': this start is " +
                    (repairMode ? "a corrupt-save repair." : "a database upgrade.") +
                    " The mirror is refreshed on the next normal start.");
                return;
            }

            SavegameFile.WriteMark(saveFilePath, active.Id, active.Name, stored);
        }

        /// <summary>The mirrored profile id, but only when it names a profile that exists here.</summary>
        private static string ValidatedMirror(SavegameFileInfo info)
        {
            if (string.IsNullOrWhiteSpace(info?.MirroredProfileId)) return null;

            return ProfileStore.FindProfile(info.MirroredProfileId) != null ? info.MirroredProfileId : null;
        }

        /// <summary>
        /// Re-points <c>StartServerArgs.DisabledMods</c> at the live setting list. Vanilla assigns the
        /// same reference (<c>DisabledMods = ClientSettings.DisabledMods</c>) at every call site, so
        /// this restores exactly what vanilla would have captured had it captured it now.
        /// Unconditional on purpose: it costs one field write and it closes the aliasing hole for good.
        /// </summary>
        private static void SyncServerArgs(StartServerArgs serverargs)
        {
            if (serverargs == null || DisabledModsRef == null) return;

            DisabledModsRef(serverargs) = ClientSettings.DisabledMods;
        }

        // ---------------------------------------------------------------- in-game side

        /// <summary>
        /// The in-game half: it RECORDS the mod list this world loaded with, and it is the fallback
        /// recorder for the cold-launch case, where no patch was installed before the first world of a
        /// session, so that start never passed through the menu-side gate.
        /// <para>
        /// Both recorders key on the same savegame identifier and only ever write the active profile,
        /// so they converge instead of fighting. This one leaves the file path alone — it does not
        /// know it — and never touches the savegame database: the world is open. The mod list
        /// therefore reaches the savegame mirror on the NEXT menu-side start, which is the first
        /// moment the file can safely be written.
        /// </para>
        /// <para>
        /// Singleplayer only: on a multiplayer server it binds nothing, records nothing and says
        /// nothing. Bindings that earlier builds recorded for servers are deliberately left alone: a
        /// <see cref="SavegameBinding"/> written here carries no path, no world name and no
        /// singleplayer flag, exactly like the cold-launch binding of a real singleplayer world, so
        /// a prune could not tell the two apart and would destroy genuine ones. A server's entry is
        /// inert - the menu-side gate only ever looks up the id of a local save file - unless a local
        /// save shares that id (the server runs a copy of it), and then it is at worst one stale
        /// suggestion that the next singleplayer load overwrites.
        /// </para>
        /// </summary>
        public static void OnWorldStarted(ICoreClientAPI api)
        {
            try
            {
                if (api == null) return;

                // Singleplayer only. On a multiplayer server SavegameIdentifier is the SERVER's
                // savegame id (sent in its identification packet), the menu-side gate never runs, and
                // the chat advice below would be wrong. capi.IsSinglePlayer is ClientMain.IsSingleplayer,
                // set from ScreenManager.StartGame's flag before any mod loads: true only on the
                // ConnectToSingleplayer route, false on every multiplayer connect (see NOTES).
                if (!api.IsSinglePlayer)
                {
                    gatedSavegameId = null;
                    return;
                }

                string savegameId = api.World?.SavegameIdentifier;
                if (string.IsNullOrWhiteSpace(savegameId)) return;

                // A start the menu-side gate handled has already put its case to the player, who
                // answered it. Do not repeat any of it in chat.
                bool alreadyPrompted = string.Equals(gatedSavegameId, savegameId, StringComparison.Ordinal);
                gatedSavegameId = null;

                ModProfile active = ProfileStore.ActiveProfile;
                if (active == null) return;

                // Read before the capture below overwrites it: this is what the world loaded with the
                // LAST time, and on a cold launch it is the only thing there is to compare against.
                List<string> previous = ProfileStore.GetModList(savegameId, null);

                string boundId = ProfileStore.GetBoundProfileId(savegameId, null);
                if (boundId == null)
                {
                    // Deliberately does NOT re-bind an existing binding: on a cold launch the player
                    // was never offered the choice. The next warm start settles it properly.
                    ProfileStore.BindSavegame(savegameId, null, null, active.Id);
                }

                // LevelFinalize, not here, is where the capture happens — see OnLevelLoaded.
                api.Event.LevelFinalize += () => OnLevelLoaded(api, savegameId, previous, boundId, active, alreadyPrompted);
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Error("[BetterMods] Could not check this world's profile binding.");
                BetterModsModSystem.Logger?.Error(e);
            }
        }

        /// <summary>
        /// Records the mod list, and puts up the cold-launch notice if there is one.
        /// <para>
        /// The capture is deliberately NOT done in <c>StartClientSide</c>. That runs from
        /// <c>ModLoader.instantiateMods</c> (SystemModHandler.cs:132), i.e. while the world is still
        /// being brought up — assets, chunks and level init all still ahead of it — so a load that
        /// then failed would leave a recorded list claiming success. <c>LevelFinalize</c> is the first
        /// event that means "this world is up", which is exactly the bar this record has to clear.
        /// Nothing is lost by waiting: <c>IModLoader.Mods</c> is fixed for the lifetime of the world.
        /// </para>
        /// </summary>
        private static void OnLevelLoaded(
            ICoreClientAPI api,
            string savegameId,
            List<string> previous,
            string boundId,
            ModProfile active,
            bool alreadyPrompted)
        {
            List<string> loaded = null;

            try
            {
                loaded = WorldModList.Capture(api);
                if (loaded != null) ProfileStore.RecordModList(savegameId, null, loaded);
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Error("[BetterMods] Could not record the mod list for this world.");
                BetterModsModSystem.Logger?.Error(e);
            }

            try
            {
                if (alreadyPrompted) return;

                List<string> lines = new List<string>();

                if (boundId != null && boundId != active.Id)
                {
                    ModProfile bound = ProfileStore.FindProfile(boundId);
                    string boundName = string.IsNullOrWhiteSpace(bound?.Name) ? boundId : bound.Name;

                    BetterModsModSystem.Logger?.Notification(
                        "[BetterMods] World '" + savegameId + "' is bound to profile '" + boundName + "' but loaded with '" + active.Name + "'.");

                    lines.Add("This world is bound to the mod profile \"" + boundName + "\", but it loaded with \"" + active.Name + "\".");
                }

                if (previous != null && loaded != null)
                {
                    // A record from 1.22 can name server-only mods, which 1.23's client loader never
                    // loads and so never captures; they are not missing. Without a readable installed
                    // list there is nothing to filter by, and the raw comparison is the old behaviour.
                    List<ModContainer> installed = InstalledModScanner.MenuMods(ClientProgram.screenManager, out bool reliable);
                    if (reliable) previous = ProfileMatcher.ClientLoadable(previous, installed);

                    ModListComparison comparison = WorldModList.Compare(previous, loaded);
                    if (comparison.Missing.Count > 0)
                    {
                        lines.Add(comparison.Missing.Count + " mod" + (comparison.Missing.Count == 1 ? "" : "s")
                                  + " this world previously loaded with " + (comparison.Missing.Count == 1 ? "is" : "are")
                                  + " not enabled: " + Summarise(comparison.Missing) + ".");
                    }
                }

                if (lines.Count == 0) return;

                lines.Add("Return to the main menu and start it again to be offered the switch.");

                // Deferred on purpose: the short delay keeps the line from being buried under the
                // join-time chat spam. The callback is torn down with the world, so it cannot outlive it.
                api.Event.RegisterCallback(_ => ShowNotice(api, lines), NoticeDelayMs);
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Error("[BetterMods] Could not work out what to say about this world's mods.");
                BetterModsModSystem.Logger?.Error(e);
            }
        }

        private static void ShowNotice(ICoreClientAPI api, List<string> lines)
        {
            try
            {
                foreach (string line in lines) api.ShowChatMessage("[BetterMods] " + line);
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not show the profile mismatch notice: " + e.Message);
            }
        }

        // ---------------------------------------------------------------- internals

        private static string Summarise(List<string> entries)
        {
            int shown = Math.Min(entries.Count, NoticeListLimit);
            string joined = string.Join(", ", entries.GetRange(0, shown));

            int rest = entries.Count - shown;
            return rest > 0 ? joined + " and " + rest + " more" : joined;
        }

        /// <summary>Drops blanks from a mod list, preserving the null / empty distinction.</summary>
        private static List<string> Clean(List<string> entries)
        {
            if (entries == null) return null;

            List<string> cleaned = new List<string>(entries.Count);
            foreach (string entry in entries)
            {
                if (!string.IsNullOrWhiteSpace(entry)) cleaned.Add(entry.Trim());
            }

            return cleaned;
        }

        /// <summary>Order-insensitive comparison of two mod lists, where null is not the empty list.</summary>
        private static bool SameList(List<string> a, List<string> b)
        {
            if (a == null || b == null) return a == null && b == null;
            if (a.Count != b.Count) return false;

            HashSet<string> set = new HashSet<string>(a, StringComparer.OrdinalIgnoreCase);
            foreach (string entry in b)
            {
                if (!set.Contains(entry)) return false;
            }

            return true;
        }

    }
}
