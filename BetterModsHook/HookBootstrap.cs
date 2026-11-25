using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

namespace BetterMods.Hook
{
    /// <summary>
    /// Waits for the game to come up, then loads BetterMods and asks it to install its Harmony
    /// patches — so the main-menu mods screen is patched on a cold launch, without the player having
    /// to enter and leave a world first.
    /// <para>
    /// <b>Everything here is pure reflection.</b> Not one Harmony or BetterMods type is named in
    /// code. That is deliberate: a method that mentions such a type by name has it in its metadata,
    /// and the JIT resolves it when the method is compiled — which for this assembly happens before
    /// those assemblies could possibly load, and would fault. Pure reflection removes the trap
    /// entirely rather than tiptoeing around it, and it is also why this project references nothing
    /// but the framework. <see cref="InvokeApplyPatches"/> is additionally marked
    /// <see cref="MethodImplOptions.NoInlining"/> so that the whole late-bound step stays in a method
    /// that is only ever jitted after the assemblies are loaded.
    /// </para>
    /// <para>
    /// <b>Timing.</b> Two things make "as early as possible" wrong:
    /// </para>
    /// <list type="number">
    /// <item>Nothing in <c>Lib\</c> resolves until <c>ClientProgram.Main</c> registers
    /// <c>AssemblyResolver.AssemblyResolve</c> (ClientProgram.cs:49) — the deps.json entries for
    /// 0Harmony, MonoMod.*, Newtonsoft.Json and cairo-sharp point at <c>lib/net10.0/...</c> paths
    /// that do not exist on disk, so the host drops them and that event handler is the only route to
    /// them.</item>
    /// <item><c>ClientSettings</c> has a static constructor that reads clientsettings.json and, on a
    /// parse failure, dereferences <c>ScreenManager.Platform.Logger</c> (ClientSettings.cs:1818-1830).
    /// Touching it before <c>ClientProgram</c> has applied <c>--dataPath</c> and constructed the
    /// platform would read the wrong settings file, or poison the type with a
    /// TypeInitializationException. BetterMods' <c>ProfileStore</c> reads
    /// <c>ClientSettings.DisabledMods</c>, so this matters.</item>
    /// </list>
    /// <para>
    /// Both are satisfied by one observable condition: <c>ClientProgram.screenManager != null</c>.
    /// It is assigned at ClientProgram.cs:159, after <c>--dataPath</c> is applied (:67-70), after
    /// <c>GamePaths.EnsurePathsExist()</c> (:151) and after the first <c>ClientSettings</c> read
    /// (:155) — and long before the player can open the Mods screen. So stage 2 polls that field on
    /// each subsequent assembly load; the game loads plenty more (Lang, OpenTK, Cairo, SkiaSharp,
    /// SQLite, and Mono.Cecil when <c>ScreenManager.loadMods()</c> reads mod metadata) between then
    /// and the main menu appearing.
    /// </para>
    /// <para>
    /// <b>Reentrancy.</b> The work runs inline, on the thread that raised
    /// <see cref="AppDomain.AssemblyLoad"/>, which during startup is the game's main thread. On
    /// CoreCLR that notification is raised from managed code after the assembly is fully loaded and
    /// published, and nested loads from the same thread are the ordinary case — the game's own
    /// <c>AssemblyResolve</c> handler loads assemblies from inside the loader too. The hazard that
    /// would justify deferring is patching a method while another thread executes it, and none of
    /// BetterMods' targets (<c>GuiScreenMods</c>, <c>GuiElementModCell</c>,
    /// <c>ScreenManager.ConnectToSingleplayer</c>) can be running at this point: no screen has been
    /// composed and no world can be started. The handler is unsubscribed and the one-shot latch
    /// closed <i>before</i> any loading happens, so the assembly loads that Harmony and
    /// Reflection.Emit trigger cannot re-enter this code.
    /// </para>
    /// </summary>
    internal static class HookBootstrap
    {
        private const string GameLibAssemblyName = "VintagestoryLib";
        private const string ClientProgramTypeName = "Vintagestory.Client.ClientProgram";
        private const string ScreenManagerFieldName = "screenManager";

        private const string BootstrapTypeName = "BetterMods.PatchBootstrap";
        private const string ApplyPatchesMethodName = "ApplyPatches";

        private const int StateWaitingForGameLib = 0;
        private const int StateWaitingForStartup = 1;
        private const int StateFinished = 2;

        private static int state = StateWaitingForGameLib;

        /// <summary>Resolved once VintagestoryLib is loaded; null means "give up quietly".</summary>
        private static FieldInfo screenManagerField;

        /// <summary>
        /// The only thing that is safe to do before <c>Main</c>. Subscribing costs nothing and
        /// touches no game state.
        /// </summary>
        public static void Install()
        {
            AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;

            // The hook runs before the entry point is even jitted, so VintagestoryLib cannot be
            // loaded yet and this loop finds nothing today. It is here so that the design does not
            // silently depend on that ordering.
            try
            {
                foreach (Assembly loaded in AppDomain.CurrentDomain.GetAssemblies())
                {
                    OnWaitingForGameLib(loaded);
                    if (Volatile.Read(ref state) != StateWaitingForGameLib) break;
                }
            }
            catch (Exception e)
            {
                // The subscription above is already in place, so this costs nothing but a log line.
                HookLog.Write("Could not scan the already-loaded assemblies; waiting for the event instead.", e);
            }
        }

        private static void OnAssemblyLoad(object sender, AssemblyLoadEventArgs args)
        {
            try
            {
                switch (Volatile.Read(ref state))
                {
                    case StateWaitingForGameLib:
                        OnWaitingForGameLib(args?.LoadedAssembly);
                        break;

                    case StateWaitingForStartup:
                        OnWaitingForStartup();
                        break;
                }
            }
            catch (Exception e)
            {
                // A failed hook must degrade to "the mod behaves exactly as it did without it",
                // never to a broken launch. Stop trying, and leave a trail.
                Stop();
                HookLog.Write("Aborted: unexpected failure while waiting for the game to start.", e);
            }
        }

        private static void OnWaitingForGameLib(Assembly loaded)
        {
            if (loaded == null) return;

            string name;
            try
            {
                name = loaded.GetName().Name;
            }
            catch (Exception)
            {
                return;
            }

            if (!string.Equals(name, GameLibAssemblyName, StringComparison.OrdinalIgnoreCase)) return;

            // ClientProgram.screenManager is a public static field (ClientProgram.cs:45). Reading it
            // cannot run a static constructor prematurely: Main itself is a static method on that
            // type, so the type is already initialised by the time we get here.
            Type clientProgram = loaded.GetType(ClientProgramTypeName, throwOnError: false);
            FieldInfo field = clientProgram?.GetField(ScreenManagerFieldName, BindingFlags.Public | BindingFlags.Static);

            if (field == null)
            {
                // The readiness probe is gone, which means this game version is not the one this
                // hook was written against. Failing closed is the safe direction: no patches from
                // the hook, and the mod still works normally once a world has been loaded.
                Stop();
                HookLog.Write("Disabled: could not find " + ClientProgramTypeName + "." + ScreenManagerFieldName +
                              " in " + GameLibAssemblyName + ". The game version is probably newer than this hook.");
                return;
            }

            screenManagerField = field;
            Volatile.Write(ref state, StateWaitingForStartup);
        }

        private static void OnWaitingForStartup()
        {
            FieldInfo field = screenManagerField;
            if (field == null) return;

            if (field.GetValue(null) == null) return;

            // Close the latch and unsubscribe BEFORE doing anything that loads assemblies, so the
            // loads below cannot re-enter this handler.
            if (Interlocked.Exchange(ref state, StateFinished) == StateFinished) return;
            Unsubscribe();

            try
            {
                Run();
            }
            catch (Exception e)
            {
                HookLog.Write("Failed to install BetterMods' patches. The mod itself is unaffected: " +
                              "it will patch the menu as usual once a world has been loaded.", e);
            }
        }

        private static void Run()
        {
            // Which BetterMods the game will load, and the one path it will load it from - a zip
            // install is unpacked into the game's own cache folder for it first if need be. Every
            // "no" is logged inside, with the reason.
            string modPath = HookModLocator.Locate();
            if (modPath == null) return;

            string harmonyPath = HookPaths.HarmonyPath;
            if (!File.Exists(harmonyPath))
            {
                HookLog.Write("Skipped: no Harmony at '" + harmonyPath + "'.");
                return;
            }

            // UnsafeLoadFrom, from the game's own Lib folder and from the mod folder the game will
            // itself use, is exactly what ModAssemblyLoader.LoadFrom does - so the game's later load
            // of either resolves to this same instance instead of conflicting with it.
            Assembly.UnsafeLoadFrom(harmonyPath);
            Assembly mod = Assembly.UnsafeLoadFrom(modPath);

            InvokeApplyPatches(mod);
        }

        /// <summary>
        /// The late-bound call into BetterMods. Kept in its own non-inlined method purely so that
        /// nothing about it can be jitted as part of <see cref="Run"/>, which runs before the mod
        /// assembly exists.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void InvokeApplyPatches(Assembly mod)
        {
            Type bootstrap = mod.GetType(BootstrapTypeName, throwOnError: false);
            if (bootstrap == null)
            {
                HookLog.Write("Skipped: '" + DescribeLocation(mod) + "' has no " + BootstrapTypeName +
                              ". The installed BetterMods is probably older than this hook.");
                return;
            }

            MethodInfo apply = bootstrap.GetMethod(
                ApplyPatchesMethodName,
                BindingFlags.Public | BindingFlags.Static,
                null,
                Type.EmptyTypes,
                null);

            if (apply == null)
            {
                HookLog.Write("Skipped: " + BootstrapTypeName + " has no public static " +
                              ApplyPatchesMethodName + "().");
                return;
            }

            try
            {
                apply.Invoke(null, null);
            }
            catch (TargetInvocationException e)
            {
                HookLog.Write("BetterMods threw while applying its patches.", e.InnerException ?? e);
                return;
            }

            // Deliberately not "applied": ApplyPatches is a no-op when the player has switched
            // BetterMods off in the mod manager, and that decision belongs to the mod, which has the
            // game's own parsed ClientSettings in hand. It logs its own line in that case.
            HookLog.Write("Loaded BetterMods from '" + DescribeLocation(mod) + "' and asked it to install its patches.");
        }

        private static string DescribeLocation(Assembly mod)
        {
            try
            {
                return mod.Location;
            }
            catch (Exception)
            {
                return "<unknown>";
            }
        }

        private static void Stop()
        {
            Volatile.Write(ref state, StateFinished);
            Unsubscribe();
        }

        private static void Unsubscribe()
        {
            try
            {
                AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
            }
            catch (Exception)
            {
                // The state latch already guarantees the work runs at most once.
            }
        }
    }
}
