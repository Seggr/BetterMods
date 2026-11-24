using System;
using System.IO;
using System.Security.Cryptography;
using Vintagestory.API.Config;

namespace BetterMods.Startup
{
    /// <summary>What <see cref="StartupHookInstaller.Ensure"/> did.</summary>
    public enum HookInstallOutcome
    {
        /// <summary>The file on disk already is the embedded build. Nothing was written.</summary>
        UpToDate,

        /// <summary>There was no file; the embedded build was written.</summary>
        Installed,

        /// <summary>There was a different file; it was replaced by the embedded build.</summary>
        Updated,

        /// <summary>Nothing usable was written. Whatever was on disk before is still there, untouched.</summary>
        Failed
    }

    /// <summary>
    /// Keeps <c>&lt;ModConfig&gt;\BetterMods\BetterModsHook.dll</c> identical to the startup hook
    /// embedded in this very <c>BetterMods.dll</c>, so nobody ever copies a dll by hand.
    /// <para>
    /// <b>Why that path.</b> The .NET host needs a real file before <c>Main</c>, so the hook cannot be
    /// read out of the mod zip (zip contents are not files). The game's unpack cache is a file, but its
    /// folder name embeds the zip's hash and therefore changes with every release - and a
    /// <c>DOTNET_STARTUP_HOOKS</c> entry naming a file that no longer exists is a game that does not
    /// start. A loose dll anywhere in a mod search path is collected as a broken code mod.
    /// <c>ModConfig\BetterMods\</c> is none of these: the mod loader never scans it, it survives mod
    /// updates, and it moves with <c>--dataPath</c> (<see cref="GamePaths.ModConfig"/>).
    /// </para>
    /// <para>
    /// <b>Rule: always converge to the embedded build of the BetterMods that is running.</b> No version
    /// comparison, no "keep the newer one". The hook's only job is to call this assembly's
    /// <c>PatchBootstrap.ApplyPatches</c>, so the right hook is by definition the one built alongside the
    /// BetterMods that will be loaded - including after a deliberate downgrade. SHA-256 decides
    /// "different", nothing else.
    /// </para>
    /// <para>
    /// <b>Replacing a hook the process is running.</b> When the game was launched through the hook,
    /// the runtime keeps that file mapped for the life of the process. Verified on Windows / .NET 10
    /// with a throwaway harness, both from another process and from inside the hooked process itself:
    /// overwriting it (<c>File.WriteAllBytes</c>, <c>File.Copy(overwrite)</c>,
    /// <c>File.Move(overwrite)</c> onto it) and deleting it are refused, but <b>renaming it is
    /// allowed</b>, and a fresh file can then be moved into the freed name. So the update is: write the
    /// new bytes to a temp file beside it and flush them to disk; try one atomic replace (works whenever
    /// the file is not mapped, and always on Linux/macOS, where rename(2) does not care); if Windows
    /// refuses, rename the live file aside to <c>BetterModsHook.dll.old-&lt;n&gt;</c>, move the temp file
    /// into the stable name, and if THAT fails, rename the old file straight back. The stable name is
    /// never left empty by a failure; the shortcut's "only set the variable if the file exists" guard
    /// is the backstop for a crash in the microseconds between the two renames, not the plan.
    /// Leftover <c>.old-*</c> and <c>.tmp-*</c> files are deleted best-effort on every later check -
    /// the one this session renamed aside is still mapped and simply stays until the next start.
    /// </para>
    /// <para>Nothing here throws to its caller.</para>
    /// </summary>
    public static class StartupHookInstaller
    {
        public const string HookFileName = "BetterModsHook.dll";

        /// <summary>The manifest resource name; set by the <c>EmbedStartupHook</c> target in BetterMods.csproj.</summary>
        private const string ResourceName = "BetterMods.Startup.BetterModsHook.dll";

        private const string AsideMarker = ".old-";
        private const string TempMarker = ".tmp-";

        private static readonly object Lock = new object();
        private static bool checkedThisSession;

        /// <summary><c>&lt;ModConfig&gt;\BetterMods</c>, absolute. Honours <c>--dataPath</c>.</summary>
        public static string HookDirectory => Path.GetFullPath(Path.Combine(GamePaths.ModConfig, "BetterMods"));

        /// <summary>The stable path every launch command and shortcut points <c>DOTNET_STARTUP_HOOKS</c> at.</summary>
        public static string HookPath => Path.Combine(HookDirectory, HookFileName);

        /// <summary>
        /// The automatic check: once per session, from <see cref="PatchBootstrap.ApplyPatches"/> -
        /// i.e. at the first world load, or during startup when the hook itself loaded this mod.
        /// Never throws.
        /// </summary>
        public static void EnsureInstalledOnce()
        {
            lock (Lock)
            {
                if (checkedThisSession) return;
                checkedThisSession = true;
            }

            Ensure(out _);
        }

        /// <summary>
        /// Brings the file on disk in line with the embedded hook. Logs what it changed (and nothing
        /// when nothing changed). <paramref name="error"/> is a short reason when the outcome is
        /// <see cref="HookInstallOutcome.Failed"/>, else null. Never throws.
        /// </summary>
        public static HookInstallOutcome Ensure(out string error)
        {
            lock (Lock)
            {
                error = null;
                string path = null;

                try
                {
                    byte[] embedded = ReadEmbedded();
                    if (embedded == null || embedded.Length == 0)
                    {
                        error = "this BetterMods build does not contain the startup hook";
                        StartupLog.Warning("[BetterMods] Startup hook not installed: " + error + ".");
                        return HookInstallOutcome.Failed;
                    }

                    string directory = HookDirectory;
                    path = Path.Combine(directory, HookFileName);

                    Directory.CreateDirectory(directory);
                    DeleteLeftovers(directory);

                    bool exists = File.Exists(path);
                    if (exists && SameBytes(path, embedded)) return HookInstallOutcome.UpToDate;

                    string temp = path + TempMarker + Guid.NewGuid().ToString("N");
                    try
                    {
                        WriteFlushed(temp, embedded);

                        if (!exists)
                        {
                            File.Move(temp, path);
                            StartupLog.Notification("[BetterMods] Installed the startup hook at '" + path + "'.");
                            return HookInstallOutcome.Installed;
                        }

                        string aside = Replace(temp, path);
                        StartupLog.Notification(
                            "[BetterMods] Updated the startup hook at '" + path + "'" +
                            (aside != null
                                ? " (the running copy was moved aside to '" + Path.GetFileName(aside) + "' and is removed on a later start)."
                                : "."));
                        return HookInstallOutcome.Updated;
                    }
                    finally
                    {
                        TryDelete(temp);
                    }
                }
                catch (Exception e)
                {
                    error = e.Message;
                    StartupLog.Warning(
                        "[BetterMods] Could not install or update the startup hook" + (path != null ? " at '" + path + "'" : "") +
                        "; whatever was there is unchanged. " + e.Message);
                    return HookInstallOutcome.Failed;
                }
            }
        }

        /// <summary>
        /// Puts <paramref name="temp"/> at <paramref name="path"/>. Returns the name the old file was
        /// renamed to when it had to be moved aside, else null. Throws with <paramref name="path"/>
        /// holding the old file (never nothing) when it cannot.
        /// </summary>
        private static string Replace(string temp, string path)
        {
            try
            {
                // One atomic step whenever the old file is not mapped into a process.
                File.Move(temp, path, overwrite: true);
                return null;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                // Windows refuses to replace an image file the runtime has mapped - this process was
                // launched through the hook. Renaming it is allowed; see the class remarks.
            }

            string aside = FreeAsideName(path);
            File.Move(path, aside);

            try
            {
                File.Move(temp, path);
            }
            catch (Exception)
            {
                try
                {
                    File.Move(aside, path);
                }
                catch (Exception restore)
                {
                    // Nothing left to try. The launch guard ("only set the variable if the file
                    // exists") keeps the game starting; the next check writes a fresh copy.
                    StartupLog.Error(
                        "[BetterMods] Could not put the old startup hook back at '" + path + "' after a failed update; " +
                        "it is at '" + aside + "'. " + restore.Message);
                }

                throw;
            }

            // Still mapped, so this normally fails; a later start removes it.
            TryDelete(aside);
            return aside;
        }

        private static string FreeAsideName(string path)
        {
            for (int n = 1; n < 1000; n++)
            {
                string candidate = path + AsideMarker + n;
                if (!File.Exists(candidate)) return candidate;
            }

            return path + AsideMarker + Guid.NewGuid().ToString("N");
        }

        /// <summary>
        /// Removes renamed-aside copies from earlier sessions and temp files from interrupted writes.
        /// Best effort: a copy this or another running game still has mapped refuses, and stays.
        /// </summary>
        private static void DeleteLeftovers(string directory)
        {
            foreach (string marker in new[] { AsideMarker, TempMarker })
            {
                string[] files;
                try
                {
                    files = Directory.GetFiles(directory, HookFileName + marker + "*");
                }
                catch (Exception)
                {
                    continue;
                }

                foreach (string file in files) TryDelete(file);
            }
        }

        private static void TryDelete(string file)
        {
            try
            {
                if (file != null && File.Exists(file)) File.Delete(file);
            }
            catch (Exception)
            {
                // Mapped by a running game, or otherwise locked. Harmless; retried next time.
            }
        }

        private static void WriteFlushed(string file, byte[] bytes)
        {
            using (FileStream stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }

            // Read back before it is allowed anywhere near the stable name: a short or damaged hook
            // file is a game that does not start.
            if (!SameBytes(file, bytes)) throw new IOException("The startup hook did not read back intact from '" + file + "'.");
        }

        private static bool SameBytes(string file, byte[] expected)
        {
            byte[] actual = File.ReadAllBytes(file);
            return SHA256.HashData(actual).AsSpan().SequenceEqual(SHA256.HashData(expected));
        }

        /// <summary>The embedded hook, or null if this build has none. Never loaded - only copied.</summary>
        private static byte[] ReadEmbedded()
        {
            using Stream stream = typeof(StartupHookInstaller).Assembly.GetManifestResourceStream(ResourceName);
            if (stream == null) return null;

            using MemoryStream buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }
    }
}
