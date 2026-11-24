using System;
using System.IO;
using System.Reflection;
using Vintagestory.Common;

namespace BetterMods.Startup
{
    /// <summary>
    /// The "Add Shortcut" button on BetterMods' own mod card, on both surfaces: whether it is enabled,
    /// what its tooltip says, what a click does, and the one-line hint for players who have not set
    /// the startup hook up.
    /// <para>
    /// The rule is purely "our shortcut exists on the desktop -> disabled, else enabled" (Windows),
    /// whether or not this process was hook-launched. On Linux/macOS there is no shortcut: the button is
    /// shown disabled and its tooltip carries the exact launch command, which is also logged once.
    /// </para>
    /// </summary>
    public static class StartupOffer
    {
        public const string ButtonLabel = "Add Shortcut";

        /// <summary>The in-game dialog's lowest-priority notice; shown only while it is true and useful (<see cref="ShowHint"/>).</summary>
        public const string HintWindows = "Use Add Shortcut on the BetterMods card to have profiles on the main menu from startup.";

        public const string HintOther = "Hover the BetterMods card for how to have profiles on the main menu from startup.";

        private const string HookAssemblyName = "BetterModsHook";

        private static bool? hookLaunched;
        private static bool commandLogged;

        /// <summary>From the last <see cref="Refresh"/>: our shortcut is on the desktop.</summary>
        public static bool ShortcutExists { get; private set; }

        /// <summary>From the last <see cref="Refresh"/>: where it was found, or null.</summary>
        public static string ShortcutFound { get; private set; }

        /// <summary>The last click's failure, ASCII, for the surfaces' notice slots; null after a success.</summary>
        public static string LastError { get; private set; }

        /// <summary>The last click succeeded (the in-game dialog says so once).</summary>
        public static bool LastSucceeded { get; private set; }

        /// <summary>
        /// True when this process was started with BetterMods' startup hook.
        /// <para>
        /// Detected by the hook ASSEMBLY being loaded, not by a flag the hook sets on its way into
        /// <c>PatchBootstrap.ApplyPatches</c>, and not by reading <c>DOTNET_STARTUP_HOOKS</c>. The .NET
        /// host loads every startup hook into the default context before <c>Main</c>; nothing else ever
        /// loads <c>BetterModsHook</c> - BetterMods only copies its embedded bytes to disk, it never
        /// loads them. So the assembly being present is proof the hook ran in this process, whatever
        /// path it was given. The flag would be false on a hook launch that found no BetterMods.dll to
        /// load (a zip install's first launch, before the game has unpacked it) - the player already
        /// launches through the hook there and must not be nagged. The variable would be fooled by a
        /// differently named hook file and says nothing about whether it actually loaded.
        /// </para>
        /// </summary>
        public static bool HookLaunched
        {
            get
            {
                if (hookLaunched.HasValue) return hookLaunched.Value;

                bool found = false;
                try
                {
                    foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (string.Equals(assembly.GetName().Name, HookAssemblyName, StringComparison.OrdinalIgnoreCase))
                        {
                            found = true;
                            break;
                        }
                    }
                }
                catch (Exception)
                {
                    // Unknown counts as "not launched through it": the worst case is one hint line.
                }

                hookLaunched = found;
                return found;
            }
        }

        /// <summary>True for BetterMods' own row - the only card that gets the button.</summary>
        public static bool IsOwnMod(ModContainer mod)
        {
            return string.Equals(mod?.Info?.ModID, BetterModsModSystem.ModId, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Re-reads the desktop. Called once per card rebuild (from dressing BetterMods' row), never per
        /// frame - the surfaces read <see cref="ShortcutExists"/> afterwards. Never throws.
        /// </summary>
        public static void Refresh()
        {
            if (!DesktopShortcut.IsSupported)
            {
                ShortcutExists = false;
                ShortcutFound = null;
                LogCommandOnce();
                return;
            }

            ShortcutExists = DesktopShortcut.Exists(HookPathOrNull(), out string found);
            ShortcutFound = found;
        }

        /// <summary>The button is live: Windows, and no shortcut of ours on the desktop yet.</summary>
        public static bool CanAdd => DesktopShortcut.IsSupported && !ShortcutExists;

        /// <summary>
        /// The one-line hint is worth showing: not hook-launched, and (on Windows) no shortcut yet.
        /// Every surface ranks it below all of its own notices.
        /// </summary>
        public static bool ShowHint => !HookLaunched && !ShortcutExists;

        public static string Hint => DesktopShortcut.IsSupported ? HintWindows : HintOther;

        /// <summary>
        /// The button's tooltip paragraph, in VTML (already escaped). Says what a click does, or why the
        /// button is disabled.
        /// </summary>
        public static string TooltipLine()
        {
            const string Why = "Without the startup hook BetterMods only runs once a world has loaded, so a fresh launch shows the vanilla mods screen.";

            if (!DesktopShortcut.IsSupported)
            {
                string command = DesktopShortcut.ShellCommand(HookPathOrNull() ?? "");
                return Dim(Why) + "\n" + (command == null
                    ? "Launch the game with DOTNET_STARTUP_HOOKS set to " + Escape(HookPathOrNull() ?? "BetterModsHook.dll in ModConfig/BetterMods") + "."
                    : "To have profiles on the main menu from startup, launch the game with:\n" + Escape(command));
            }

            string line = ShortcutExists
                ? "Desktop shortcut already exists" + (ShortcutFound != null ? ": " + Escape(Path.GetFileNameWithoutExtension(ShortcutFound)) : "") +
                  ". Start the game from it to have profiles on the main menu from startup."
                : "Add Shortcut puts \"" + DesktopShortcut.ShortcutName + "\" on your desktop. Starting the game from it loads BetterMods at startup, so the profiles are on the main-menu mods screen from the first screen.";

            if (LastError != null) line += "\n<font color=\"#ff8080\">Last attempt failed: " + Escape(LastError) + "</font>";

            return Dim(Why) + "\n" + line;
        }

        /// <summary>
        /// The click. Installs or updates the hook file, then writes the shortcut. Records the result in
        /// <see cref="LastError"/> / <see cref="LastSucceeded"/> and refreshes <see cref="ShortcutExists"/>
        /// - the caller rebuilds (deferred) so the button turns disabled. Never throws.
        /// </summary>
        public static void AddShortcut()
        {
            LastSucceeded = false;

            try
            {
                if (!CanAdd) return;

                HookInstallOutcome outcome = StartupHookInstaller.Ensure(out string installError);
                if (outcome == HookInstallOutcome.Failed)
                {
                    Fail("could not install the startup hook: " + installError);
                    return;
                }

                string hookPath = StartupHookInstaller.HookPath;
                if (!DesktopShortcut.TryCreate(hookPath, out string created, out string error))
                {
                    Fail(error);
                    return;
                }

                LastError = null;
                LastSucceeded = true;
                StartupLog.Notification("[BetterMods] Created the desktop shortcut '" + created + "' (startup hook '" + hookPath + "').");
            }
            catch (Exception e)
            {
                Fail(e.Message);
            }
            finally
            {
                Refresh();
            }
        }

        private static void Fail(string reason)
        {
            LastError = Updates.ModUpdateRules.AsciiLine(string.IsNullOrWhiteSpace(reason) ? "unknown error" : reason, 160);
            StartupLog.Warning("[BetterMods] Add Shortcut failed: " + reason);
        }

        /// <summary>Linux/macOS: the command goes in the log once, where it can be copied (a tooltip cannot).</summary>
        private static void LogCommandOnce()
        {
            if (commandLogged || HookLaunched) return;
            commandLogged = true;

            string command = DesktopShortcut.ShellCommand(HookPathOrNull() ?? "");
            if (command == null) return;

            StartupLog.Notification(
                "[BetterMods] To have profiles on the main menu from startup, launch the game with: " + command);
        }

        private static string HookPathOrNull()
        {
            try
            {
                return StartupHookInstaller.HookPath;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string Dim(string vtml) => "<font opacity=\"0.6\">" + vtml + "</font>";

        private static string Escape(string text)
        {
            return string.IsNullOrEmpty(text) ? "" : text.Replace("<", "&lt;").Replace(">", "&gt;");
        }
    }
}
