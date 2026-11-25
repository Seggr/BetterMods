using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace BetterMods.Hook
{
    /// <summary>
    /// Works out which BetterMods the game is going to load, and returns the one
    /// <c>BetterMods.dll</c> path the game will load it from - unpacking a zip install into the
    /// game's own unpack cache first if the game has not done that yet.
    /// <para>
    /// Returning the *wrong* path would be worse than returning none: the hook pre-loads the assembly
    /// with <c>Assembly.UnsafeLoadFrom</c>, which is exactly what <c>ModAssemblyLoader.LoadFrom</c>
    /// does (ModAssemblyLoader.cs:28-31). Loading the same path twice returns the same instance and
    /// the game's load is a no-op; loading a *different* path with the same simple name makes
    /// <c>ModContainer.LoadAssembly</c> throw "Assembly with same name is already loaded" and flags
    /// the mod <c>ModError.ChangedVersion</c> - BetterMods would then fail to load at all. So this
    /// replays the game's own choice, and stands down wherever that choice cannot be replayed exactly.
    /// </para>
    /// <para>
    /// The game's choice, identical on 1.22.7 and 1.23.0 (ModLoader.LoadMods / DisableMods /
    /// CheckDuplicateModIDMods / CheckAndSortDependencies):
    /// </para>
    /// <list type="number">
    /// <item>Every folder (with a <c>modinfo.json</c>) and every <c>.zip</c> (any case) directly in a
    /// mod search path is a mod; its id is <c>modinfo.json</c>'s <c>modid</c> - read from the archive
    /// root for a zip - or, without one, <c>ModInfo.ToModID(name)</c>. File names do not matter.</item>
    /// <item><c>ClientSettings.DisabledMods</c> switches off every mod listed as
    /// <c>"modid@version"</c> or bare <c>"modid"</c> (exact, ordinal) - which is how a BetterMods
    /// profile picks one of several installed versions.</item>
    /// <item>Of the enabled mods sharing an id, only the newest (<c>ModInfo.CompareTo</c>, i.e.
    /// <c>GameVersion.IsNewerVersionThan</c>) survives; the rest are errored.</item>
    /// <item>The survivor is dropped if its <c>game</c> dependency is not met
    /// (<c>SemVer(running) &gt;= SemVer(required)</c>).</item>
    /// </list>
    /// <para>
    /// Where that cannot be replayed exactly - two enabled copies of the same version, a version the
    /// game's own comparer would throw on - this stands down, as the hook always has when it found
    /// more than one candidate. Loose <c>.dll</c>/<c>.cs</c> copies of BetterMods are not considered:
    /// they are not a way BetterMods is distributed.
    /// </para>
    /// </summary>
    internal static class HookModLocator
    {
        public const string ModId = "bettermods";

        private const string ModInfoFileName = "modinfo.json";
        private const long MaxModInfoBytes = 1024 * 1024;

        private const string ApiAssemblyName = "VintagestoryAPI";
        private const string GameVersionTypeName = "Vintagestory.API.Config.GameVersion";
        private const string ShortGameVersionFieldName = "ShortGameVersion";
        private const string SemVerTypeName = "ProperVersion.SemVer";

        /// <summary>One installed copy of BetterMods, as the game would see it.</summary>
        internal sealed class ModSource
        {
            public string Path;
            public bool IsZip;
            public string Version;

            /// <summary>The <c>"game"</c> dependency's version string; null when there is none.</summary>
            public string GameDependency;

            public override string ToString()
            {
                return "'" + Path + "' (v" + Version + ")";
            }
        }

        /// <summary>
        /// The <c>BetterMods.dll</c> to load, or null - in which case a line saying why has already
        /// been written to the hook log. Never throws.
        /// </summary>
        public static string Locate()
        {
            List<string> roots = HookPaths.ModSearchPaths();
            List<ModSource> installed = FindInstalled(roots);

            if (installed.Count == 0)
            {
                // Not finding the mod is a no-op, not a failure - the player may have uninstalled it
                // and left the hook configured, and that has to stay harmless. But it is never
                // silent: the list of places that were looked at is what turns "no tabs" into an
                // answer.
                HookLog.Write("Did nothing: no BetterMods (a folder or .zip whose modinfo.json says modid '" + ModId +
                              "') in these mod paths: " + Describe(roots) + ". If BetterMods was removed on purpose " +
                              "this is harmless; to stop it entirely, delete the 'Vintage Story (BetterMods)' " +
                              "shortcut and the ModConfig\\BetterMods folder.");
                return null;
            }

            HashSet<string> disabled = new HashSet<string>(HookPaths.ReadDisabledMods(), StringComparer.Ordinal);
            List<ModSource> enabled = new List<ModSource>();
            foreach (ModSource mod in installed)
            {
                if (!disabled.Contains(ModId + "@" + mod.Version) && !disabled.Contains(ModId)) enabled.Add(mod);
            }

            if (enabled.Count == 0)
            {
                HookLog.Write("Did nothing: BetterMods is switched off in the mod manager (installed: " +
                              Describe(installed) + ").");
                return null;
            }

            ModSource chosen = PickNewest(enabled);
            if (chosen == null)
            {
                HookLog.Write("Skipped: " + enabled.Count + " enabled copies of BetterMods and none is newer than the " +
                              "others (" + Describe(enabled) + "). Remove or switch off all but one.");
                return null;
            }

            bool? gameOk = SatisfiesGameDependency(chosen.GameDependency, out string running);
            if (gameOk == false)
            {
                HookLog.Write("Skipped: BetterMods " + chosen + " needs game " + chosen.GameDependency + ", and this is " +
                              running + ". The game will not load it either.");
                return null;
            }

            if (!chosen.IsZip)
            {
                string dll = Path.Combine(chosen.Path, HookPaths.ModAssemblyFileName);
                if (!File.Exists(dll))
                {
                    HookLog.Write("Did nothing: the BetterMods folder " + chosen + " has no " + HookPaths.ModAssemblyFileName + ".");
                    return null;
                }

                return Path.GetFullPath(dll);
            }

            // Unpacking is a permanent decision about the game's cache, so it is only made once the
            // game is known to accept this zip. If the check itself could not run, an extraction the
            // game already made is still used - exactly what the hook did before it could unpack.
            if (gameOk == null)
            {
                HookLog.Write("Could not check BetterMods " + chosen + " against the running game version; " +
                              "will use an existing extraction but not make one.");
            }

            return HookZipUnpacker.Resolve(chosen.Path, HookPaths.UnpackCachePath, allowUnpack: gameOk == true);
        }

        // ---------------------------------------------------------------- finding installed copies

        /// <summary>
        /// Every folder or zip in the mod search paths whose mod id is <see cref="ModId"/>. Other mods'
        /// archives are only opened read-only to read their <c>modinfo.json</c>, and never kept open.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static List<ModSource> FindInstalled(List<string> roots)
        {
            List<ModSource> found = new List<ModSource>();

            foreach (string root in roots)
            {
                FileSystemInfo[] entries;
                try
                {
                    DirectoryInfo dir = new DirectoryInfo(root);
                    if (!dir.Exists) continue;
                    entries = dir.GetFileSystemInfos();
                }
                catch (Exception)
                {
                    continue;
                }

                foreach (FileSystemInfo entry in entries)
                {
                    // CollectMods skips dot-names on 1.23; nobody installs BetterMods as one anyway.
                    if (entry.Name.StartsWith('.')) continue;

                    try
                    {
                        string modInfo;
                        bool isZip;

                        if (entry is DirectoryInfo folder)
                        {
                            string file = Path.Combine(folder.FullName, ModInfoFileName);
                            if (!File.Exists(file)) continue;
                            if (new FileInfo(file).Length > MaxModInfoBytes) continue;
                            modInfo = File.ReadAllText(file);
                            isZip = false;
                        }
                        else if (string.Equals(Path.GetExtension(entry.Name), ".zip", StringComparison.OrdinalIgnoreCase))
                        {
                            modInfo = ReadModInfoFromZip(entry.FullName);
                            if (modInfo == null) continue;
                            isZip = true;
                        }
                        else
                        {
                            continue;
                        }

                        if (!TryParseModInfo(modInfo, out string modId, out string version, out string gameDependency)) continue;
                        if (!string.Equals(modId, ModId, StringComparison.Ordinal)) continue;

                        found.Add(new ModSource
                        {
                            Path = entry.FullName,
                            IsZip = isZip,
                            Version = version,
                            GameDependency = gameDependency
                        });
                    }
                    catch (Exception)
                    {
                        // Somebody else's unreadable mod is not ours to report.
                    }
                }
            }

            return found;
        }

        /// <summary>
        /// <c>modinfo.json</c> from the archive root, as <c>ZipArchive.GetEntry("modinfo.json")</c>
        /// finds it (exact name, as ModContainer.LoadModInfo does), or null.
        /// </summary>
        private static string ReadModInfoFromZip(string zipPath)
        {
            using FileStream stream = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);

            ZipArchiveEntry entry = archive.GetEntry(ModInfoFileName);
            if (entry == null || entry.Length > MaxModInfoBytes) return null;

            using StreamReader reader = new StreamReader(entry.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }

        /// <summary>
        /// The three fields the game's choice depends on, read the way Newtonsoft fills
        /// <c>ModInfo</c>: property names case-insensitively (the last match wins), a missing
        /// <c>modid</c> derived from <c>name</c>, a missing <c>version</c> left as <c>""</c>, and
        /// dependencies keyed by their exact name.
        /// </summary>
        internal static bool TryParseModInfo(string json, out string modId, out string version, out string gameDependency)
        {
            modId = null;
            version = "";
            gameDependency = null;

            try
            {
                using JsonDocument document = JsonDocument.Parse(
                    json.TrimStart('﻿'),
                    new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });

                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return false;

                string name = null;
                JsonElement? dependencies = null;

                foreach (JsonProperty property in root.EnumerateObject())
                {
                    if (property.Name.Equals("modid", StringComparison.OrdinalIgnoreCase))
                    {
                        modId = AsString(property.Value);
                    }
                    else if (property.Name.Equals("version", StringComparison.OrdinalIgnoreCase))
                    {
                        version = AsString(property.Value) ?? "";
                    }
                    else if (property.Name.Equals("name", StringComparison.OrdinalIgnoreCase))
                    {
                        name = AsString(property.Value);
                    }
                    else if (property.Name.Equals("dependencies", StringComparison.OrdinalIgnoreCase))
                    {
                        dependencies = property.Value;
                    }
                }

                if (modId == null) modId = ToModId(name);
                if (modId == null) return false;

                if (dependencies.HasValue && dependencies.Value.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty dependency in dependencies.Value.EnumerateObject())
                    {
                        if (dependency.Name == "game") gameDependency = AsString(dependency.Value) ?? "";
                    }
                }

                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string AsString(JsonElement value)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.String: return value.GetString();
                case JsonValueKind.Number:
                case JsonValueKind.True:
                case JsonValueKind.False: return value.GetRawText();
                default: return null;
            }
        }

        /// <summary><c>ModInfo.ToModID</c>: ASCII letters and digits only, lower-cased.</summary>
        private static string ToModId(string name)
        {
            if (name == null) return null;

            StringBuilder id = new StringBuilder(name.Length);
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                bool letter = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
                bool digit = c >= '0' && c <= '9';
                if (digit && i == 0) return null; // the game throws, so the mod has no info at all
                if (letter || digit) id.Append(char.ToLowerInvariant(c));
            }

            return id.ToString();
        }

        // ---------------------------------------------------------------- the game's choice

        /// <summary>
        /// The copy <c>CheckDuplicateModIDMods</c> keeps: the one strictly newer than every other by
        /// <c>GameVersion.IsNewerVersionThan</c>. Null if there is no such copy (equal versions), or if
        /// a version is one the game's comparer throws on.
        /// </summary>
        internal static ModSource PickNewest(List<ModSource> enabled)
        {
            if (enabled.Count == 1) return enabled[0];

            try
            {
                foreach (ModSource candidate in enabled)
                {
                    bool newest = true;
                    foreach (ModSource other in enabled)
                    {
                        if (ReferenceEquals(other, candidate)) continue;
                        if (!IsNewerVersionThan(candidate.Version, other.Version))
                        {
                            newest = false;
                            break;
                        }
                    }

                    if (newest) return candidate;
                }
            }
            catch (Exception)
            {
                // Fall through: the game's own sort would have thrown on this version too.
            }

            return null;
        }

        /// <summary>
        /// <c>GameVersion.IsNewerVersionThan(version, reference)</c>, ported verbatim (GameVersion.cs,
        /// identical on 1.22.7 and 1.23.0). Not called by reflection because it is a plain function
        /// of two strings and the hook should not need the game for it.
        /// </summary>
        internal static bool IsNewerVersionThan(string version, string reference)
        {
            int[] referenceParts = SplitVersionString(reference);
            int[] versionParts = SplitVersionString(version);

            for (int i = 0; i < referenceParts.Length; i++)
            {
                if (i >= versionParts.Length) return false;
                if (referenceParts[i] > versionParts[i]) return false;
                if (referenceParts[i] < versionParts[i]) return true;
            }

            return false;
        }

        /// <summary><c>GameVersion.SplitVersionString</c>, ported verbatim.</summary>
        private static int[] SplitVersionString(string version)
        {
            if (string.IsNullOrEmpty(version)) throw new ArgumentNullException(nameof(version));

            int dash = version.IndexOf('-');
            string numeric = dash < 1 ? version : version.Substring(0, dash);

            int dots = 0;
            foreach (char c in numeric) if (c == '.') dots++;

            if (dots == 1)
            {
                numeric += ".0";
                version = dash < 1 ? numeric : numeric + version.Substring(dash);
            }

            string[] parts = version.Split(new[] { ".", "-" }, StringSplitOptions.None);

            if (parts.Length <= 3)
            {
                string[] extended = new string[parts.Length + 1];
                Array.Copy(parts, extended, parts.Length);
                extended[parts.Length] = "3";
                parts = extended;
            }
            else if (parts[3] == "rc") parts[3] = "2";
            else if (parts[3] == "pre") parts[3] = "1";
            else parts[3] = "0";

            int[] numbers = new int[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                int.TryParse(parts[i], out numbers[i]);
            }

            return numbers;
        }

        /// <summary>
        /// <c>ModLoader.SatisfiesVersion(required, running game version)</c>: true when there is no
        /// requirement, <c>SemVer(running) &gt;= SemVer(required)</c> otherwise. The SemVer rules are
        /// the game's own, called by reflection on <c>ProperVersion.SemVer</c> (public in
        /// VintagestoryAPI on 1.22.7 and 1.23.0), with the running version read from the
        /// <c>GameVersion.ShortGameVersion</c> const - the version the <c>game</c> core mod reports.
        /// Null if any of that could not be done.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool? SatisfiesGameDependency(string required, out string running)
        {
            running = null;
            if (string.IsNullOrEmpty(required) || required == "*") return true;

            try
            {
                Assembly api = FindLoaded(ApiAssemblyName) ?? Assembly.Load(new AssemblyName(ApiAssemblyName));

                FieldInfo field = api.GetType(GameVersionTypeName, throwOnError: false)
                    ?.GetField(ShortGameVersionFieldName, BindingFlags.Public | BindingFlags.Static);
                if (field == null) return null;

                running = field.IsLiteral ? field.GetRawConstantValue() as string : field.GetValue(null) as string;
                if (string.IsNullOrEmpty(running)) return true;

                Type semVer = api.GetType(SemVerTypeName, throwOnError: false);
                MethodInfo tryParse = semVer?.GetMethod("TryParse", BindingFlags.Public | BindingFlags.Static, null,
                    new[] { typeof(string), semVer.MakeByRefType() }, null);
                MethodInfo atLeast = semVer?.GetMethod("op_GreaterThanOrEqual", BindingFlags.Public | BindingFlags.Static, null,
                    new[] { semVer, semVer }, null);
                if (tryParse == null || atLeast == null) return null;

                object[] requiredArgs = { required, null };
                tryParse.Invoke(null, requiredArgs);
                object[] runningArgs = { running, null };
                tryParse.Invoke(null, runningArgs);

                return atLeast.Invoke(null, new[] { runningArgs[1], requiredArgs[1] }) is bool ok ? ok : null;
            }
            catch (Exception e)
            {
                HookLog.Write("Could not compare BetterMods' game dependency '" + required + "' with the running game.", e);
                return null;
            }
        }

        private static Assembly FindLoaded(string name)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (string.Equals(assembly.GetName().Name, name, StringComparison.OrdinalIgnoreCase)) return assembly;
                }
                catch (Exception)
                {
                    // Skip it.
                }
            }

            return null;
        }

        private static string Describe(List<string> paths)
        {
            return paths == null || paths.Count == 0 ? "<none>" : string.Join("; ", paths);
        }

        private static string Describe(List<ModSource> mods)
        {
            List<string> parts = new List<string>();
            foreach (ModSource mod in mods) parts.Add(mod.ToString());
            return Describe(parts);
        }
    }
}
