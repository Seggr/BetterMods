using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
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

        /// <summary>
        /// Where <c>ModLoader</c> unpacks zip mods: <c>Path.Combine(GamePaths.Cache, "unpack")</c>,
        /// <c>GamePaths.Cache</c> being <c>&lt;dataPath&gt;\Cache</c> (ModLoader.cs:42-43 and
        /// GamePaths.cs:26 on both 1.22.7 and 1.23.0).
        /// </summary>
        public static string UnpackCachePath => Path.Combine(DataPath, "Cache", "unpack");

        // ---------------------------------------------------------------- mod search paths

        /// <summary>
        /// The game's mod search paths, in the game's own order: <c>ClientSettings.ModPaths</c> first,
        /// then every <c>--addModPath</c>. Relative entries are rooted at the binaries directory, as
        /// <c>ModLoader</c>'s constructor does (ModLoader.cs:104).
        /// </summary>
        public static List<string> ModSearchPaths()
        {
            List<string> configured = null;
            try
            {
                configured = ReadStringListSetting("modPaths");
                if (configured != null && configured.Count == 0) configured = null;
            }
            catch (Exception e)
            {
                // Belt and braces for the one failure ReadStringListSetting's own catch cannot see:
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
        /// <c>ClientSettings.DisabledMods</c>: the <c>"modid@version"</c> (or bare <c>"modid"</c>)
        /// entries <c>ModLoader.DisableMods</c> switches off. Empty when the file or the list is
        /// missing or unreadable - failing open, as BetterMods' own switched-off check does.
        /// <para>
        /// Values are returned exactly as stored: the game builds a plain (ordinal) <c>HashSet</c>
        /// from them (ModLoader.cs:330-343 on 1.23.0, :318-331 on 1.22.7), so neither case nor
        /// whitespace is normalised there either.
        /// </para>
        /// </summary>
        public static List<string> ReadDisabledMods()
        {
            try
            {
                return ReadStringListSetting("disabledMods", trim: false) ?? new List<string>();
            }
            catch (Exception e)
            {
                HookLog.Write("Could not read the disabled mods; treating every mod as enabled.", e);
                return new List<string>();
            }
        }

        /// <summary>
        /// One <c>stringListSettings</c> entry of clientsettings.json, or null if it cannot be read
        /// - in which case the caller falls back to what <c>ClientSettings</c> would have seeded it
        /// with.
        /// <para>
        /// Parsed by hand rather than via <c>ClientSettings</c>, which lives in VintagestoryLib and is
        /// unreachable from this assembly - and which must not be touched this early anyway, because
        /// its static constructor dereferences <c>ScreenManager.Platform.Logger</c> on a parse failure.
        /// <c>SettingsBase</c> stores a string list under <c>stringListSettings</c>, keyed by name.
        /// </para>
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static List<string> ReadStringListSetting(string name, bool trim = true)
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
                if (!lists.TryGetProperty(name, out JsonElement list)) return null;
                if (list.ValueKind != JsonValueKind.Array) return null;

                List<string> values = new List<string>();
                foreach (JsonElement element in list.EnumerateArray())
                {
                    if (element.ValueKind != JsonValueKind.String) continue;

                    string value = element.GetString();
                    if (string.IsNullOrWhiteSpace(value)) continue;
                    values.Add(trim ? value.Trim() : value);
                }

                return values;
            }
            catch (Exception e)
            {
                HookLog.Write("Could not read '" + name + "' from '" + path + "'; using the default instead.", e);
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
