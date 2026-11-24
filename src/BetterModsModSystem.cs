using System;
using BetterMods.Gui;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.Client;

namespace BetterMods
{
    /// <summary>
    /// Client-only entry point. Brings the patches up via <see cref="PatchBootstrap"/> and registers
    /// the in-game profile dialog.
    /// <para>
    /// This is the game's own route in, and it only runs when a world loads. The optional
    /// <c>BetterModsHook</c> startup hook calls the same <see cref="PatchBootstrap.ApplyPatches"/>
    /// earlier, during game startup, which is what gets the profile tabs onto the main-menu mods
    /// screen on a cold launch.
    /// </para>
    /// </summary>
    public class BetterModsModSystem : ModSystem
    {
        public const string HarmonyId = "bettermods";

        /// <summary>
        /// The mod id from modinfo.json. Same string as <see cref="HarmonyId"/>, but a different
        /// thing: this one is what <c>ClientSettings.DisabledMods</c> entries are keyed by.
        /// </summary>
        public const string ModId = "bettermods";

        /// <summary>Set once the client side starts. Null while the player is in the main menu.</summary>
        public static ICoreClientAPI ClientApi { get; private set; }

        /// <summary>
        /// A logger that works everywhere. Prefers the client API's logger, and falls back to the
        /// platform logger, which is the only one available in main-menu context (there is no
        /// ICoreClientAPI there — the menu's API is a MainMenuAPI whose config methods throw).
        /// May be null very early during startup; all call sites use <c>?.</c>.
        /// </summary>
        public static ILogger Logger
        {
            get
            {
                ICoreClientAPI api = ClientApi;
                if (api?.Logger != null) return api.Logger;

                try
                {
                    return ScreenManager.Platform?.Logger;
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }

        public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Client;

        public override void StartClientSide(ICoreClientAPI api)
        {
            ClientApi = api;

            // Same call the optional BetterModsHook startup hook makes, so a hooked and an unhooked
            // launch take an identical path; whichever gets there first wins and the other is a no-op.
            // ApplyPatches leaves a Harmony failure to its caller on purpose (the type enumeration
            // sits outside its per-class try), and an escape from here would take the world load with
            // it - so this is where that contract is honoured.
            try
            {
                PatchBootstrap.ApplyPatches();
            }
            catch (Exception e)
            {
                Logger?.Error("[BetterMods] Could not install the Harmony patches; the mod manager stays vanilla.");
                Logger?.Error(e);
            }

            RegisterProfileDialog(api);

            // Which code mods this process is now holding an assembly for. That is what decides
            // whether a version swap in a profile needs a game restart to take effect.
            Profiles.LoadedModVersions.Record(api.ModLoader);

            // Second of the two savegame-binding recorders. The menu-side one (the
            // ConnectToSingleplayer prefix) covers every start where a patch was already installed;
            // this one covers the first world of a cold launch, where no third-party code had run yet.
            Profiles.SavegameProfileGate.OnWorldStarted(api);
        }

        /// <summary>
        /// Registers the in-game profile manager and its hotkey.
        /// <para>
        /// The hotkey is registered with NO default key: it is listed under Settings &gt; Controls as
        /// "Mod Profiles" and does nothing until the player binds it there. <see cref="UnboundKey"/>
        /// explains why -1 and not <c>GlKeys.Unknown</c>.
        /// </para>
        /// <para>
        /// <see cref="GuiDialog.OnBlockTexturesLoaded"/> would wire the handler by itself from
        /// <c>ToggleKeyCombinationCode</c>; setting it here as well is harmless (both routes end in
        /// <c>Toggle()</c>) and keeps the whole binding visible in one place.
        /// </para>
        /// </summary>
        /// <summary>
        /// The "no key" default for the profile dialog's hotkey. A negative key code is what the
        /// engine itself treats as unbound (<c>KeyCombination.ToString()</c> renders it as "?",
        /// 1.23 KeyCombination.cs:58), and it can never match a key event: every event's code comes
        /// from <c>KeyConverter.NewKeysToGlKeys[...]</c> (ClientPlatformWindows.cs:3777), which is
        /// never negative. <c>GlKeys.Unknown</c> (0) is NOT safe - it is what an unmapped physical key
        /// converts to, so it would open the dialog on some stray key. A binding the player saves
        /// under Settings &gt; Controls overrides this (<c>HotkeyManager.RegisterHotKey</c> reads
        /// <c>ClientSettings.KeyMapping</c>, HotkeyManager.cs:222-225).
        /// </summary>
        private const GlKeys UnboundKey = (GlKeys)(-1);

        private void RegisterProfileDialog(ICoreClientAPI api)
        {
            try
            {
                GuiDialogModProfiles dialog = new GuiDialogModProfiles(api);

                api.Input.RegisterHotKey(GuiDialogModProfiles.HotkeyCode, "Mod Profiles", UnboundKey, HotkeyType.GUIOrOtherControls);
                api.Input.SetHotKeyHandler(GuiDialogModProfiles.HotkeyCode, _ =>
                {
                    dialog.Toggle();
                    return true;
                });

                api.Gui.RegisterDialog(dialog);
            }
            catch (Exception e)
            {
                Logger?.Error("[BetterMods] Could not register the in-game profile dialog.");
                Logger?.Error(e);
            }
        }

        /// <summary>
        /// World exit. Runs from <c>ModLoader.Dispose()</c> (SystemModHandler.cs:237-242), i.e. every
        /// time the player returns to the main menu.
        /// <para>
        /// NOTE: deliberately no UnpatchAll here. Unpatching would tear down the main-menu patches,
        /// and without the startup hook they would never be reinstalled, because StartClientSide only
        /// runs when a world loads. The patches are process-lifetime by design - see PatchBootstrap.
        /// </para>
        /// <para>
        /// This is however the one moment at which ANOTHER mod can unpatch them, because its
        /// <c>Dispose()</c> runs from the same loop - and at least one shipping mod
        /// (<c>Conquest VS Edition</c>) calls <c>UnpatchAll(null)</c>, which takes every owner's
        /// patches with it. Mod systems are disposed in an order this mod does not control, so the
        /// check cannot happen inline; <see cref="PatchBootstrap.ScheduleRearm"/> defers it to the
        /// next frame, by which time the whole dispose loop has finished.
        /// </para>
        /// </summary>
        public override void Dispose()
        {
            ClientApi = null;
            PatchBootstrap.ScheduleRearm();

            // A check started from the in-game dialog is abandoned with the world; the mods screen
            // starts a fresh one. Installs only ever start from the menu and are left alone.
            Updates.ModUpdateService.CancelCheck();
            base.Dispose();
        }
    }
}
