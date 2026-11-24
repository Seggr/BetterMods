using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BetterMods.Hook
{
    /// <summary>
    /// Path resolution for the startup hook, done by hand.
    /// <para>
    /// <c>GamePaths</c> lives in VintagestoryAPI and is therefore unavailable here — the hook must be
    /// loadable before any game assembly. Everything below re-derives what <c>GamePaths</c> would
    /// say, from <see cref="AppContext.BaseDirectory"/>, the raw command line and the game's own
    /// clientsettings.json, using the same rules the game uses:
    /// </para>
    /// <list type="bullet">
    /// <item><c>GamePaths.Binaries</c> is <c>AppDomain.CurrentDomain.BaseDirectory</c>.</item>
    /// <item><c>GamePaths.DataPath</c> is <c>%APPDATA%\VintagestoryData</c> — or the binaries
    /// directory in a dev environment, which <c>RuntimeEnv</c> defines as "there is no assets folder
    /// next to the binaries" (GamePaths.cs:63-68, RuntimeEnv.cs:76) — overridden by <c>--dataPath</c>
    /// (ClientProgram.cs:67-70).</item>
    /// <item><c>GamePaths.Logs</c> is <c>&lt;dataPath&gt;\Logs</c>, overridden by <c>--logPath</c>.</item>
    /// <item><c>GamePaths.Cache</c> is <c>&lt;dataPath&gt;\Cache</c>, and zip mods are unpacked under
    /// <c>&lt;cache&gt;\unpack</c> (ModLoader.cs:42).</item>
    /// <item>The mod search paths are <c>ClientSettings.ModPaths</c> — read from clientsettings.json,
    /// because it is user-editable and only DEFAULTS to <c>"Mods"</c> plus
    /// <c>&lt;dataPath&gt;\Mods</c> (ClientSettings.cs:2021-2025) — followed by every
    /// <c>--addModPath</c> (ScreenManager.cs:522-525, SystemModHandler.cs:143-146). Relative entries
    /// are rooted at the binaries directory (ModLoader.cs:104).</item>
    /// </list>
    /// <para>
    /// Relative values off the command line are rooted at the binaries directory rather than left to
    /// the working directory. The game gets to the same place: <c>ClientProgram.Start</c> sets
    /// <c>Environment.CurrentDirectory</c> to the binaries directory (ClientProgram.cs:146-150)
    /// before anything reads <c>GamePaths.DataPath</c>. Doing it explicitly means the hook also
    /// answers correctly in the window BEFORE that assignment, which is when its own log file path is
    /// first needed.
    /// </para>
    /// </summary>
    internal static class HookPaths
    {
        public const string ModAssemblyFileName = "BetterMods.dll";
        public const string LogFileName = "bettermods-hook.log";

        /// <summary>Length of the hash suffix <c>ModContainer.Unpack</c> puts on a cache folder name.</summary>
        private const int UnpackHashLength = 12;

        private static string cachedDataPath;

        /// <summary>The game install directory, i.e. what <c>GamePaths.Binaries</c> returns.</summary>
        public static string GameDirectory => AppContext.BaseDirectory;

        /// <summary>Harmony as shipped by the game. Loading any other copy would be a second 0Harmony.</summary>
        public static string HarmonyPath => Path.Combine(GameDirectory, "Lib", "0Harmony.dll");

        /// <summary>
        /// <c>GamePaths.DataPath</c>. Read from the command line rather than from the game, because at
        /// hook-install time <c>ClientProgram</c> has not applied <c>--dataPath</c> yet. Neither the
        /// command line nor the base directory changes during the process, so it is resolved once.
        /// </summary>
        public static string DataPath
        {
            get
            {
                if (cachedDataPath != null) return cachedDataPath;

                string fromArgs = FirstOptionValue("dataPath");
                if (!string.IsNullOrWhiteSpace(fromArgs))
                {
                    cachedDataPath = Root(fromArgs.Trim());
                    return cachedDataPath;
                }

                if (IsDevEnvironment())
                {
                    cachedDataPath = GameDirectory;
                    return cachedDataPath;
                }

                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify);
                cachedDataPath = Path.Combine(appData, "VintagestoryData");
                return cachedDataPath;
            }
        }

        /// <summary>Where this hook writes its own failures. There is no game logger this early.</summary>
        public static string LogFilePath
        {
            get
            {
                string custom = FirstOptionValue("logPath");
                string dir = string.IsNullOrWhiteSpace(custom) ? Path.Combine(DataPath, "Logs") : Root(custom.Trim());
                return Path.Combine(dir, LogFileName);
            }
        }

        /// <summary><c>ClientSettings</c>' backing file (ClientSettings.cs:19).</summary>
        public static string ClientSettingsPath => Path.Combine(DataPath, "clientsettings.json");

        /// <summary>Where <c>ModLoader</c> unpacks zip mods (<c>GamePaths.Cache</c> + "unpack", ModLoader.cs:42).</summary>
        public static string UnpackCachePath => Path.Combine(DataPath, "Cache", "unpack");

        // ---------------------------------------------------------------- finding the mod

        /// <summary>
        /// Every <c>BetterMods.dll</c> the game could be about to load, and the places that were
        /// looked at to find them.
        /// <para>
        /// Returning the *wrong* path would be worse than returning none: the hook pre-loads the
        /// assembly with <c>Assembly.UnsafeLoadFrom</c>, which is exactly what
        /// <c>ModAssemblyLoader.LoadFrom</c> does (ModAssemblyLoader.cs:28-31). Loading the same path
        /// twice returns the same instance and the mod load is a no-op; loading a *different* path
        /// with the same simple name makes <c>ModContainer.LoadAssembly</c> throw "Assembly with same
        /// name is already loaded" and flags the mod <c>ModError.ChangedVersion</c>
        /// (ModContainer.cs:558-566) — BetterMods would then fail to load at all. So every candidate
        /// here must be a path the game itself would use, and the caller stands down rather than pick
        /// between two.
        /// </para>
        /// <para>
        /// Two layouts put that file on disk:
        /// </para>
        /// <list type="bullet">
        /// <item><b>Folder mod</b> — <c>&lt;modpath&gt;\&lt;anything&gt;\BetterMods.dll</c>. The folder
        /// name is not part of the contract (the game reads modinfo.json for the mod id), so any
        /// immediate subfolder counts, not just one called "bettermods".</item>
        /// <item><b>Zip mod</b> — the archive itself contains the assembly, so there is nothing to
        /// load until the game extracts it. <c>ModContainer.Unpack</c> (ModContainer.cs:108-124) does
        /// that into <c>&lt;cache&gt;\unpack\&lt;zip file name&gt;_&lt;first 6 bytes of the zip's
        /// SHA-1, hex&gt;</c>, and is only called from <c>ModLoader.LoadMods</c> (ModLoader.cs:192),
        /// i.e. at WORLD LOAD. So a freshly installed zip has nothing on disk on its first launch;
        /// from the second one the cache folder is there and is exactly the path the game will use
        /// again, because <c>Unpack</c> early-returns when the folder already exists.</item>
        /// </list>
        /// <para>
        /// The hook deliberately does NOT extract the zip itself. It would be writing into the game's
        /// cache from before <c>Main</c>, and any difference from the game's own extraction would be
        /// permanent: <c>Unpack</c> trusts an existing folder and never re-checks its contents. A
        /// launch that reports "not found" costs the player nothing; a corrupted mod cache costs them
        /// the mod. The cache is therefore read-only here, and used only after the zip that produced
        /// it has been re-hashed and still matches — a stale folder from an older zip is precisely
        /// the "different dll, same simple name" case that breaks the mod load.
        /// </para>
        /// </summary>
        public static List<string> FindModAssemblies(out List<string> searched)
        {
            searched = new List<string>();
            List<string> found = new List<string>();

            List<string> roots = ModSearchPaths();

            foreach (string root in roots)
            {
                searched.Add(root);

                string[] subFolders;
                try
                {
                    if (!Directory.Exists(root)) continue;
                    subFolders = Directory.GetDirectories(root);
                }
                catch (Exception)
                {
                    continue;
                }

                foreach (string folder in subFolders) AddCandidate(folder, found);
            }

            AddUnpackedZipCandidates(roots, found, searched);

            return found;
        }

        /// <summary>
        /// Adds <c>&lt;folder&gt;\BetterMods.dll</c> if it is there and is not already listed.
        /// </summary>
        private static void AddCandidate(string folder, List<string> found)
        {
            string candidate;
            try
            {
                candidate = Path.GetFullPath(Path.Combine(folder, ModAssemblyFileName));
                if (!File.Exists(candidate)) return;
            }
            catch (Exception)
            {
                return;
            }

            foreach (string existing in found)
            {
                if (string.Equals(existing, candidate, StringComparison.OrdinalIgnoreCase)) return;
            }

            found.Add(candidate);
        }

        /// <summary>
        /// Scans the game's unpack cache for a folder that holds our assembly and still corresponds
        /// to a zip that is installed right now.
        /// </summary>
        private static void AddUnpackedZipCandidates(List<string> roots, List<string> found, List<string> searched)
        {
            string cache = UnpackCachePath;
            searched.Add(cache);

            string[] folders;
            try
            {
                if (!Directory.Exists(cache)) return;
                folders = Directory.GetDirectories(cache);
            }
            catch (Exception)
            {
                return;
            }

            foreach (string folder in folders)
            {
                try
                {
                    if (!File.Exists(Path.Combine(folder, ModAssemblyFileName))) continue;
                    if (!MatchesAnInstalledZip(Path.GetFileName(folder), roots)) continue;
                }
                catch (Exception)
                {
                    continue;
                }

                AddCandidate(folder, found);
            }
        }

        /// <summary>
        /// True if <paramref name="folderName"/> is <c>&lt;zip file name&gt;_&lt;12 hex&gt;</c> and a
        /// zip of that name in one of the mod search paths still hashes to that suffix - i.e. this
        /// cache folder is the current extraction of a currently installed archive, and the game will
        /// reuse it rather than make a new one.
        /// </summary>
        private static bool MatchesAnInstalledZip(string folderName, List<string> roots)
        {
            if (string.IsNullOrEmpty(folderName)) return false;

            int split = folderName.LastIndexOf('_');
            if (split <= 0 || folderName.Length - split - 1 != UnpackHashLength) return false;

            string zipFileName = folderName.Substring(0, split);
            string expectedHash = folderName.Substring(split + 1);

            foreach (string root in roots)
            {
                string zip;
                try
                {
                    zip = Path.Combine(root, zipFileName);
                    if (!File.Exists(zip)) continue;
                }
                catch (Exception)
                {
                    continue;
                }

                string actual = ShortHash(zip);
                if (actual != null && string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase)) return true;
            }

            return false;
        }

        /// <summary>
        /// The hash <c>ModContainer.Unpack</c> names a cache folder with: the first six bytes of the
        /// file's SHA-1, lower-case hex (ModContainer.cs:118-124).
        /// </summary>
        private static string ShortHash(string path)
        {
            try
            {
                byte[] hash;
                using (FileStream stream = File.OpenRead(path))
                {
                    hash = SHA1.HashData(stream);
                }

                StringBuilder text = new StringBuilder(UnpackHashLength);
                for (int i = 0; i < UnpackHashLength / 2 && i < hash.Length; i++)
                {
                    text.Append(hash[i].ToString("x2"));
                }

                return text.ToString();
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ---------------------------------------------------------------- mod search paths

        /// <summary>
        /// The game's mod search paths, in the game's own order: <c>ClientSettings.ModPaths</c> first,
        /// then every <c>--addModPath</c>. Relative entries are rooted at the binaries directory, as
        /// <c>ModLoader</c>'s constructor does (ModLoader.cs:104).
        /// </summary>
        private static List<string> ModSearchPaths()
        {
            List<string> configured = null;
            try
            {
                configured = ReadModPathsFromSettings();
            }
            catch (Exception e)
            {
                // Belt and braces for the one failure ReadModPathsFromSettings' own catch cannot see:
                // System.Text.Json is resolved when that method is JITTED, i.e. before its try block
                // is entered, so a framework that somehow cannot supply it would throw out here.
                HookLog.Write("Could not read the mod paths; using the default ones instead.", e);
            }

            configured = configured ?? new List<string>
            {
                "Mods",
                Path.Combine(DataPath, "Mods")
            };

            List<string> paths = new List<string>();
            foreach (string entry in configured) AddPath(paths, entry);
            foreach (string extra in OptionValues("addModPath")) AddPath(paths, extra);

            return paths;
        }

        private static void AddPath(List<string> paths, string entry)
        {
            if (string.IsNullOrWhiteSpace(entry)) return;

            string full;
            try
            {
                full = Path.GetFullPath(Root(entry.Trim()));
            }
            catch (Exception)
            {
                return;
            }

            foreach (string existing in paths)
            {
                if (string.Equals(existing, full, StringComparison.OrdinalIgnoreCase)) return;
            }

            paths.Add(full);
        }

        /// <summary>
        /// <c>ClientSettings.ModPaths</c>, straight out of the settings file, or null if it cannot be
        /// read - in which case the caller falls back to the same defaults <c>ClientSettings</c> would
        /// have seeded it with.
        /// <para>
        /// Parsed by hand rather than via <c>ClientSettings</c>, which lives in VintagestoryLib and is
        /// unreachable from this assembly - and which must not be touched this early anyway, because
        /// its static constructor dereferences <c>ScreenManager.Platform.Logger</c> on a parse failure.
        /// <c>SettingsBase</c> stores a string list under <c>stringListSettings</c>, keyed by name.
        /// </para>
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static List<string> ReadModPathsFromSettings()
        {
            string path = ClientSettingsPath;

            try
            {
                if (!File.Exists(path)) return null;

                byte[] bytes = File.ReadAllBytes(path);
                int start = HasUtf8Bom(bytes) ? 3 : 0;

                using JsonDocument document = JsonDocument.Parse(
                    new ReadOnlyMemory<byte>(bytes, start, bytes.Length - start),
                    new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });

                if (!document.RootElement.TryGetProperty("stringListSettings", out JsonElement lists)) return null;
                if (lists.ValueKind != JsonValueKind.Object) return null;
                if (!lists.TryGetProperty("modPaths", out JsonElement modPaths)) return null;
                if (modPaths.ValueKind != JsonValueKind.Array) return null;

                List<string> values = new List<string>();
                foreach (JsonElement element in modPaths.EnumerateArray())
                {
                    if (element.ValueKind != JsonValueKind.String) continue;

                    string value = element.GetString();
                    if (!string.IsNullOrWhiteSpace(value)) values.Add(value.Trim());
                }

                return values.Count > 0 ? values : null;
            }
            catch (Exception e)
            {
                HookLog.Write("Could not read the mod paths from '" + path + "'; using the default ones instead.", e);
                return null;
            }
        }

        private static bool HasUtf8Bom(byte[] bytes)
        {
            return bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        }

        /// <summary>
        /// <c>RuntimeEnv.IsDevEnvironment</c>: no assets folder beside the binaries (RuntimeEnv.cs:76).
        /// A probe failure is answered with "not a dev environment", which is the normal install.
        /// </summary>
        private static bool IsDevEnvironment()
        {
            try
            {
                return !Directory.Exists(Path.Combine(GameDirectory, "assets"));
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string Root(string path)
        {
            try
            {
                return Path.IsPathRooted(path) ? path : Path.Combine(GameDirectory, path);
            }
            catch (Exception)
            {
                return path;
            }
        }

        // ---------------------------------------------------------------- command line

        /// <summary>
        /// Values of a <c>--name</c> option. Accepts both <c>--name value</c> and <c>--name=value</c>,
        /// and collects consecutive values for the list-valued options (<c>--addModPath</c> is an
        /// <c>IEnumerable&lt;string&gt;</c> in <c>ClientProgramArgs</c>), which is how
        /// CommandLineParser reads them.
        /// </summary>
        private static List<string> OptionValues(string name)
        {
            List<string> values = new List<string>();

            string[] args;
            try
            {
                args = Environment.GetCommandLineArgs();
            }
            catch (Exception)
            {
                return values;
            }

            if (args == null) return values;

            string flag = "--" + name;
            string assigned = flag + "=";

            for (int i = 1; i < args.Length; i++)
            {
                string arg = args[i];
                if (string.IsNullOrEmpty(arg)) continue;

                if (arg.StartsWith(assigned, StringComparison.OrdinalIgnoreCase))
                {
                    values.Add(arg.Substring(assigned.Length));
                    continue;
                }

                if (!arg.Equals(flag, StringComparison.OrdinalIgnoreCase)) continue;

                for (int j = i + 1; j < args.Length; j++)
                {
                    string value = args[j];
                    if (string.IsNullOrEmpty(value) || value.StartsWith("-", StringComparison.Ordinal)) break;
                    values.Add(value);
                    i = j;
                }
            }

            return values;
        }

        private static string FirstOptionValue(string name)
        {
            List<string> values = OptionValues(name);
            return values.Count > 0 ? values[0] : null;
        }
    }
}
