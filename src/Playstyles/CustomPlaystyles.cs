using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.Client;
using Vintagestory.Common;

namespace BetterMods.Playstyles
{
    /// <summary>
    /// The custom playstyle library: the <c>.vsplaystyle</c> files in <c>&lt;data&gt;\Playstyles</c>,
    /// the menu-side <see cref="PlayStyle"/> entries built from them, and the remap that keeps their
    /// codes away from the server. Main thread only (every caller is a menu screen or a menu patch).
    /// <para>
    /// A custom entry is an ordinary <see cref="PlayStyle"/> whose <c>WorldConfig</c> is the base
    /// playstyle's with the file's overrides merged in, so vanilla's selection, hover text and the
    /// customize screen all work on it unchanged. Its code, <c>bettermods-custom:&lt;slug&gt;</c>, is
    /// menu-only: <see cref="RemapForWorldStart"/> swaps the base back in on the way into the world,
    /// so the server - and the saved world - only ever see a real playstyle plus a full world config.
    /// </para>
    /// </summary>
    public static class CustomPlaystyles
    {
        public const string FolderName = "Playstyles";

        /// <summary>What every vanilla start uses when the playstyle defines no height (<c>WorldConfig.MapsizeY</c>).</summary>
        public const int VanillaWorldHeight = 256;

        private static readonly AccessTools.FieldRef<ScreenManager, List<ModContainer>> VerifiedModsRef =
            GameFields.Ref<ScreenManager, List<ModContainer>>("verifiedMods", "custom playstyles fall back to the first built-in playstyle when remapping");

        private static List<PlaystyleFile> files = new List<PlaystyleFile>();
        private static string scanSignature;

        /// <summary>The entries the most recent <see cref="AppendTo"/> built, by code.</summary>
        private static readonly Dictionary<string, BuiltPlaystyle> built = new Dictionary<string, BuiltPlaystyle>(StringComparer.Ordinal);

        /// <summary>
        /// Every custom code ever offered this session -> the real playstyle it stands for. Never
        /// cleared, so a code whose file has since vanished still remaps.
        /// </summary>
        private static readonly Dictionary<string, RealPlaystyle> remapTargets = new Dictionary<string, RealPlaystyle>(StringComparer.Ordinal);

        private static MethodBase connectMethod;
        private static MethodBase initGuiMethod;
        private static bool armedWarningLogged;

        /// <summary>
        /// "path|what was skipped" lines already written to the log. <see cref="Build"/> runs on every
        /// list load (each screen rebuild, each clone), so each distinct message is logged once a session.
        /// </summary>
        private static readonly HashSet<string> loggedSkips = new HashSet<string>(StringComparer.Ordinal);

        private const int MaxLoggedKeys = 12;

        /// <summary>A custom playstyle as built against the installed mods.</summary>
        public sealed class BuiltPlaystyle
        {
            public PlaystyleFile File;
            public PlayStyle PlayStyle;
            public string Code => File.Code;

            /// <summary>False when the base playstyle is not installed: listed, greyed, click refused.</summary>
            public bool Usable;
            public string UnusableReason;

            /// <summary>
            /// "2 settings ignored: mod X not installed" and the like; null when there is nothing to say.
            /// Plain text (not VTML). Shown only in the entry's tooltip and the log - never on the row,
            /// and never a reason to make the entry unusable.
            /// </summary>
            public string Notes;

            public int WorldHeight;
            public PlayStyle Base;
        }

        private sealed class RealPlaystyle
        {
            public string Code;
            public string LangCode;
            public string WorldType;
        }

        /// <summary>
        /// <c>&lt;data&gt;\Playstyles</c> - the same folder any mod gets from
        /// <c>api.GetOrCreateDataPath("Playstyles")</c>, built by hand because the main menu's
        /// <c>MainMenuAPI.GetOrCreateDataPath</c> throws. Follows <c>--dataPath</c>. Null when the
        /// game has not set its data path (the feature then stays off).
        /// </summary>
        public static string Folder
        {
            get
            {
                string data = GamePaths.DataPath;
                return string.IsNullOrEmpty(data) ? null : Path.Join(data, FolderName);
            }
        }

        public static IReadOnlyList<PlaystyleFile> Files => files;

        public static BuiltPlaystyle Find(string code)
        {
            return code != null && built.TryGetValue(code, out BuiltPlaystyle entry) ? entry : null;
        }

        /// <summary>The built-in code a custom code stands for, if it was ever offered this session.</summary>
        public static string BaseOf(string customCode)
        {
            return customCode != null && remapTargets.TryGetValue(customCode, out RealPlaystyle real) ? real.Code : null;
        }

        // ---------------------------------------------------------------- scanning

        /// <summary>
        /// Re-reads the folder if anything in it changed (names, sizes, write times). Cheap when
        /// nothing did: one directory listing. Never throws.
        /// </summary>
        public static void Refresh()
        {
            try
            {
                string folder = Folder;
                if (folder == null || !Directory.Exists(folder))
                {
                    files = new List<PlaystyleFile>();
                    scanSignature = "";
                    return;
                }

                List<FileInfo> candidates = new DirectoryInfo(folder)
                    .EnumerateFiles("*" + PlaystyleFormat.Extension, SearchOption.TopDirectoryOnly)
                    .Where(f => f.Extension.Equals(PlaystyleFormat.Extension, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                StringBuilder signature = new StringBuilder();
                foreach (FileInfo info in candidates)
                {
                    signature.Append(info.Name).Append('|').Append(info.Length).Append('|').Append(info.LastWriteTimeUtc.Ticks).Append('\n');
                }

                string current = signature.ToString();
                if (current == scanSignature) return;

                scanSignature = current;
                files = Scan(candidates);
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not read the Playstyles folder: " + e.Message);
                files = new List<PlaystyleFile>();
                scanSignature = null;
            }
        }

        /// <summary>Forgets the folder signature, so the next <see cref="Refresh"/> re-reads every file.</summary>
        public static void Invalidate()
        {
            scanSignature = null;
        }

        private static List<PlaystyleFile> Scan(List<FileInfo> candidates)
        {
            List<PlaystyleFile> result = new List<PlaystyleFile>();
            HashSet<string> codes = new HashSet<string>(StringComparer.Ordinal);

            if (candidates.Count > PlaystyleFormat.MaxFiles)
            {
                BetterModsModSystem.Logger?.Warning(
                    "[BetterMods] The Playstyles folder holds " + candidates.Count + " playstyle files; only the first " +
                    PlaystyleFormat.MaxFiles + " (by name) are listed.");
            }

            foreach (FileInfo info in candidates.Take(PlaystyleFormat.MaxFiles))
            {
                try
                {
                    string slug = Path.GetFileNameWithoutExtension(info.Name);

                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        Skip(info, "it is a link, not a plain file");
                        continue;
                    }

                    if (!PlaystyleFormat.IsValidSlug(slug))
                    {
                        Skip(info, "the file name may only use A-Z a-z 0-9 _ - (1-128 characters)");
                        continue;
                    }

                    if (info.Length > PlaystyleFormat.MaxFileBytes)
                    {
                        Skip(info, "it is larger than " + (PlaystyleFormat.MaxFileBytes / 1024) + " KB");
                        continue;
                    }

                    string json = File.ReadAllText(info.FullName, Encoding.UTF8);
                    PlaystyleFile file = PlaystyleFormat.Parse(json, slug, out string error);
                    if (file == null)
                    {
                        Skip(info, error);
                        continue;
                    }

                    file.Path = info.FullName;

                    // Codes are lower-cased; on a case-sensitive file system two files can collide.
                    string code = PlaystyleFormat.CodePrefix + slug.ToLowerInvariant();
                    if (codes.Contains(code))
                    {
                        string unique = code;
                        for (int n = 2; codes.Contains(unique); n++) unique = code + "-" + n;

                        BetterModsModSystem.Logger?.Warning(
                            "[BetterMods] Playstyle file " + info.Name + " collides with another file's name; listed as " + unique + ".");
                        code = unique;
                    }

                    codes.Add(code);
                    file.Code = code;
                    result.Add(file);
                }
                catch (Exception e)
                {
                    Skip(info, e.Message);
                }
            }

            // Name order in the list, file name as the tie-break.
            return result
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(f => f.Slug, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static void Skip(FileInfo info, string reason)
        {
            BetterModsModSystem.Logger?.Warning("[BetterMods] Skipping playstyle file " + info?.Name + ": " + reason);
        }

        // ---------------------------------------------------------------- building the list

        /// <summary>
        /// Whether custom playstyles may be offered at all: only while BetterMods' remap prefix on
        /// <c>ScreenManager.ConnectToSingleplayer</c> AND its index guard on the new-world screen's
        /// <c>InitGui</c> are both installed. Without the first, a custom code would reach the server
        /// (NullReferenceException at world creation); without the second, the cached screen's stale
        /// index could run off the end of the list. Either missing means vanilla's list, unchanged.
        /// </summary>
        public static bool FeatureArmed()
        {
            try
            {
                connectMethod ??= AccessTools.Method(typeof(ScreenManager), "ConnectToSingleplayer", new[] { typeof(StartServerArgs) });
                initGuiMethod ??= AccessTools.Method(typeof(GuiScreenSingleplayerNewWorld), "InitGui");

                bool armed = HasOurPrefix(connectMethod) && HasOurPrefix(initGuiMethod);
                if (!armed && !armedWarningLogged)
                {
                    armedWarningLogged = true;
                    BetterModsModSystem.Logger?.Warning(
                        "[BetterMods] Custom playstyles are off this session: a patch they depend on is not installed.");
                }

                return armed;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool HasOurPrefix(MethodBase method)
        {
            if (method == null) return false;

            HarmonyLib.Patches info = Harmony.GetPatchInfo(method);
            return info != null && info.Prefixes.Any(p => p.owner == BetterModsModSystem.HarmonyId);
        }

        /// <summary>
        /// Appends one entry per playstyle file to a NEW-WORLD <see cref="WorldConfig"/>'s freshly
        /// loaded list, after vanilla's sort, so every built-in keeps its index. Called from the
        /// <c>WorldConfig.LoadPlayStyles</c> postfix. Never throws.
        /// </summary>
        public static void AppendTo(WorldConfig config)
        {
            try
            {
                if (config == null || !config.IsNewWorld) return;

                List<PlayStyle> list = config.PlayStyles;
                if (list == null) return;

                built.Clear();
                if (files.Count == 0 || !FeatureArmed()) return;

                // LoadPlayStyles builds a brand-new list each call, so this is never a second append;
                // guarded anyway, because a duplicate code would make CurrentPlayStyle ambiguous.
                if (list.Any(p => PlaystyleFormat.IsCustomCode(p?.Code))) return;

                List<PlayStyle> builtIns = list.Where(p => p != null).ToList();
                Dictionary<string, AttributeSource> attributes = AttributeMap(config.mods);

                double order = 1000.0;
                foreach (PlaystyleFile file in files)
                {
                    BuiltPlaystyle entry = Build(file, builtIns, attributes, order);
                    order += 1.0;

                    built[entry.Code] = entry;
                    list.Add(entry.PlayStyle);
                }
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Error("[BetterMods] Could not add the custom playstyles; the list stays vanilla.");
                BetterModsModSystem.Logger?.Error(e);

                // Take back anything half-added rather than leave a list the screen cannot trust.
                try
                {
                    config?.PlayStyles?.RemoveAll(p => PlaystyleFormat.IsCustomCode(p?.Code));
                }
                catch (Exception)
                {
                    // Nothing more to do.
                }

                built.Clear();
            }
        }

        private static BuiltPlaystyle Build(PlaystyleFile file, List<PlayStyle> builtIns, Dictionary<string, AttributeSource> attributes, double order)
        {
            PlayStyle basePlaystyle = builtIns.FirstOrDefault(p => string.Equals(p.Code, file.BaseCode, StringComparison.Ordinal));

            JObject merged = basePlaystyle?.WorldConfig?.Token is JObject baseConfig ? (JObject)baseConfig.DeepClone() : new JObject();

            // Settings this install cannot honour are skipped and the entry still loads: a key no
            // installed mod defines, a value of the wrong type, an out-of-list dropdown value. Ranges
            // are clamped by TryCanonical. Only a missing base (below) makes an entry unusable.
            Dictionary<string, int> ignoredByMod = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            List<string> unknownKeys = new List<string>();
            List<string> invalidKeys = new List<string>();
            List<string> missingModKeys = new List<string>();
            int unknown = 0;
            int invalid = file.RejectedKeys;

            foreach (KeyValuePair<string, string> entry in file.Overrides)
            {
                if (!attributes.TryGetValue(entry.Key, out AttributeSource source))
                {
                    if (file.SettingMods.TryGetValue(entry.Key, out string modId))
                    {
                        ignoredByMod.TryGetValue(modId, out int count);
                        ignoredByMod[modId] = count + 1;
                        missingModKeys.Add(entry.Key + " (" + modId + ")");
                    }
                    else
                    {
                        unknown++;
                        unknownKeys.Add(entry.Key);
                    }

                    continue;
                }

                if (PlaystyleFormat.TryCanonical(source.Attribute, entry.Value, out string canonical))
                {
                    merged[entry.Key] = canonical;
                }
                else
                {
                    invalid++;
                    invalidKeys.Add(entry.Key);
                }
            }

            List<string> notes = new List<string>();
            foreach (KeyValuePair<string, int> mod in ignoredByMod.OrderBy(m => m.Key, StringComparer.OrdinalIgnoreCase).Take(3))
            {
                notes.Add(Plural(mod.Value, "setting") + " ignored: mod " + mod.Key + " not installed");
            }

            if (ignoredByMod.Count > 3) notes.Add("settings for " + (ignoredByMod.Count - 3) + " more missing mods ignored");
            if (unknown > 0) notes.Add(Plural(unknown, "setting") + " ignored: no installed mod defines " + (unknown == 1 ? "it" : "them"));
            if (invalid > 0) notes.Add(Plural(invalid, "invalid setting") + " left at the base value");

            BuiltPlaystyle result = new BuiltPlaystyle
            {
                File = file,
                Base = basePlaystyle,
                Usable = basePlaystyle != null,
                UnusableReason = basePlaystyle == null
                    ? "Needs the playstyle '" + file.BaseCode + "', which no enabled mod provides."
                    : null,
                Notes = notes.Count > 0 ? string.Join("; ", notes) + "." : null,
                WorldHeight = PlaystyleFormat.EffectiveWorldHeight(file.WorldHeight, file.BaseCode),
                PlayStyle = new PlayStyle
                {
                    Code = file.Code,
                    LangCode = file.Code,
                    ListOrder = order,
                    WorldType = basePlaystyle?.WorldType,
                    PlayListCode = basePlaystyle?.PlayListCode,
                    Mods = basePlaystyle?.Mods,
                    WorldConfig = new JsonObject(merged)
                }
            };

            if (result.Notes != null) LogSkips(file, missingModKeys, unknownKeys, invalidKeys);

            if (basePlaystyle != null)
            {
                remapTargets[file.Code] = new RealPlaystyle
                {
                    Code = basePlaystyle.Code,
                    LangCode = basePlaystyle.LangCode,
                    WorldType = basePlaystyle.WorldType
                };
            }

            return result;
        }

        private static string Plural(int count, string noun)
        {
            return count + " " + noun + (count == 1 ? "" : "s");
        }

        /// <summary>
        /// One log line per file naming the settings that were skipped (keys, capped), written once a
        /// session per distinct message. The entry itself still loads; this is information only.
        /// </summary>
        private static void LogSkips(PlaystyleFile file, List<string> missingModKeys, List<string> unknownKeys, List<string> invalidKeys)
        {
            try
            {
                List<string> parts = new List<string>();
                if (missingModKeys.Count > 0) parts.Add("for mods that are not installed: " + KeyList(missingModKeys));
                if (unknownKeys.Count > 0) parts.Add("no installed mod defines: " + KeyList(unknownKeys));
                if (invalidKeys.Count > 0) parts.Add("invalid value, base value kept: " + KeyList(invalidKeys));

                int dropped = file.RejectedKeys;
                if (dropped > 0) parts.Add(Plural(dropped, "entry") + " not readable as a setting (bad key, non-text value or over the limit)");

                if (parts.Count == 0) return;

                string message = "[BetterMods] Playstyle file " + System.IO.Path.GetFileName(file.Path ?? file.Slug ?? "") +
                                 " loaded; skipped settings - " + string.Join("; ", parts) + ".";

                if (loggedSkips.Add((file.Path ?? file.Slug) + "|" + message)) BetterModsModSystem.Logger?.Notification(message);
            }
            catch (Exception)
            {
                // Logging only.
            }
        }

        private static string KeyList(List<string> keys)
        {
            string list = string.Join(", ", keys.Take(MaxLoggedKeys));
            return keys.Count > MaxLoggedKeys ? list + " and " + (keys.Count - MaxLoggedKeys) + " more" : list;
        }

        /// <summary>An attribute an installed mod defines, and that mod's id.</summary>
        public sealed class AttributeSource
        {
            public WorldConfigurationAttribute Attribute;
            public string ModId;
        }

        /// <summary>
        /// Attribute code -> definition, across <paramref name="mods"/> exactly the way vanilla's
        /// <c>loadWorldConfigValues</c>/<c>allDefaultValues</c> walk them (every mod, later wins). The
        /// mod id comes from the mod itself, not from <c>attribute.ModInfo</c>, which only the
        /// customize screen fills in.
        /// </summary>
        public static Dictionary<string, AttributeSource> AttributeMap(List<ModContainer> mods)
        {
            Dictionary<string, AttributeSource> map = new Dictionary<string, AttributeSource>(StringComparer.Ordinal);
            if (mods == null) return map;

            foreach (ModContainer mod in mods)
            {
                WorldConfigurationAttribute[] attributes = mod?.WorldConfig?.WorldConfigAttributes;
                if (attributes == null) continue;

                foreach (WorldConfigurationAttribute attribute in attributes)
                {
                    if (attribute?.Code == null) continue;
                    map[attribute.Code] = new AttributeSource { Attribute = attribute, ModId = mod.Info?.ModID };
                }
            }

            return map;
        }

        // ---------------------------------------------------------------- the remap

        /// <summary>
        /// If <paramref name="serverargs"/> carries a custom playstyle code, replaces it with its base's
        /// <c>Code</c>, <c>LangCode</c> and <c>WorldType</c>. <c>WorldConfiguration</c> and
        /// <c>MapSizeY</c> are left alone - they already carry the preset in full. Idempotent (a real
        /// code is left untouched) and never throws.
        /// <para>
        /// This MUST run before anything else on the way into a world: the server's
        /// <c>SaveGame.SetNewWorldConfig</c> does <c>selectPlayStyle(code)</c>, and
        /// <c>loadWorldConfigValuesFromPlaystyle</c> dereferences <c>CurrentPlayStyle</c> with no null
        /// check, so an unknown code is a NullReferenceException at world creation. It also keeps the
        /// world 100% vanilla: it loads, and lists, without BetterMods.
        /// </para>
        /// </summary>
        public static void RemapForWorldStart(ScreenManager screenManager, StartServerArgs serverargs)
        {
            string from = serverargs?.PlayStyle;
            if (!PlaystyleFormat.IsCustomCode(from)) return;

            RealPlaystyle target = null;
            try
            {
                if (!remapTargets.TryGetValue(from, out target))
                {
                    // Not offered this session (should not happen): resolve from the file's base, then
                    // from whatever the installed mods offer first.
                    List<PlayStyle> real = InstalledPlaystyles(screenManager);
                    string baseCode = files.FirstOrDefault(f => f.Code == from)?.BaseCode;
                    PlayStyle match = real.FirstOrDefault(p => p.Code == baseCode) ?? real.FirstOrDefault();
                    if (match != null) target = new RealPlaystyle { Code = match.Code, LangCode = match.LangCode, WorldType = match.WorldType };
                }
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Error("[BetterMods] Resolving the base of custom playstyle " + from + " failed.");
                BetterModsModSystem.Logger?.Error(e);
            }

            // Last resort, never a custom code: the server only knows real ones.
            target ??= new RealPlaystyle { Code = "surviveandbuild", LangCode = "preset-surviveandbuild", WorldType = "standard" };

            serverargs.PlayStyle = target.Code;
            serverargs.PlayStyleLangCode = target.LangCode;
            serverargs.WorldType = target.WorldType;

            BetterModsModSystem.Logger?.Notification(
                "[BetterMods] New world from custom playstyle " + from + ": created as playstyle '" + target.Code +
                "' with the preset's world settings and height.");
        }

        /// <summary>The real playstyles the installed, enabled mods offer, in vanilla's order (<c>LoadPlayStyles</c>).</summary>
        private static List<PlayStyle> InstalledPlaystyles(ScreenManager screenManager)
        {
            List<PlayStyle> result = new List<PlayStyle>();
            List<ModContainer> mods = screenManager != null && VerifiedModsRef != null ? VerifiedModsRef(screenManager) : null;
            if (mods == null) return result;

            foreach (ModContainer mod in mods)
            {
                if (mod == null || mod.Error.HasValue || !mod.Enabled || mod.WorldConfig?.PlayStyles == null) continue;

                foreach (PlayStyle playstyle in mod.WorldConfig.PlayStyles)
                {
                    if (playstyle?.Code == null || PlaystyleFormat.IsCustomCode(playstyle.Code)) continue;
                    if (result.Any(p => p.Code == playstyle.Code)) continue;
                    result.Add(playstyle);
                }
            }

            return result.OrderBy(p => p.ListOrder).ToList();
        }

        // ---------------------------------------------------------------- saving

        /// <summary>The settings a Save captures, taken at the click.</summary>
        public sealed class SaveCapture
        {
            public string BaseCode;
            public int WorldHeight;
            public Dictionary<string, string> Overrides = new Dictionary<string, string>(StringComparer.Ordinal);
            public Dictionary<string, string> SettingMods = new Dictionary<string, string>(StringComparer.Ordinal);

            /// <summary>The current entry when it is itself a custom preset (prefills the name).</summary>
            public BuiltPlaystyle From;
        }

        /// <summary>
        /// Captures the new-world screen's current settings as a preset: the base playstyle (the
        /// selection itself, or its base when the selection is a custom preset), every attribute whose
        /// effective value differs from defaults + base - which includes anything changed on the
        /// customize screen - and the world height. Never the seed or the world name.
        /// <para>
        /// Reads <c>Jworldconfig</c>, which vanilla keeps as defaults &lt;- playstyle &lt;- custom
        /// (<c>updateJWorldConfig</c>); it deliberately does NOT call <c>WorldConfig.ToJson()</c>,
        /// which writes <c>playstyle</c> and <c>worldHeight</c> into that same object in place.
        /// </para>
        /// </summary>
        public static SaveCapture Capture(WorldConfig config, out string error)
        {
            error = null;

            PlayStyle current = config?.CurrentPlayStyle;
            if (current == null)
            {
                error = "Select a playstyle first.";
                return null;
            }

            SaveCapture capture = new SaveCapture { BaseCode = current.Code };
            BuiltPlaystyle custom = Find(current.Code);
            if (custom != null)
            {
                if (!custom.Usable)
                {
                    error = custom.UnusableReason;
                    return null;
                }

                capture.BaseCode = custom.Base.Code;
                capture.From = custom;
            }
            else if (PlaystyleFormat.IsCustomCode(current.Code))
            {
                error = "The selected playstyle is no longer available.";
                return null;
            }

            PlayStyle basePlaystyle = config.PlayStyles.FirstOrDefault(p => p?.Code == capture.BaseCode && !PlaystyleFormat.IsCustomCode(p.Code));
            if (basePlaystyle == null)
            {
                error = "The playstyle '" + capture.BaseCode + "' is not available.";
                return null;
            }

            if (!(config.Jworldconfig?.Token is JObject effective))
            {
                error = "The world settings could not be read.";
                return null;
            }

            JObject baseline = WorldConfig.allDefaultValues(config.mods)?.Token as JObject ?? new JObject();
            if (basePlaystyle.WorldConfig?.Token is JObject baseConfig)
            {
                foreach (JProperty property in baseConfig.Properties()) baseline[property.Name] = property.Value.DeepClone();
            }

            foreach (KeyValuePair<string, AttributeSource> entry in AttributeMap(config.mods).OrderBy(e => e.Key, StringComparer.Ordinal))
            {
                string effectiveText = PlaystyleFormat.TokenText(effective[entry.Key]);
                if (effectiveText == null || !PlaystyleFormat.TryCanonical(entry.Value.Attribute, effectiveText, out string value)) continue;

                string baseText = PlaystyleFormat.TokenText(baseline[entry.Key]);
                string baseValue = baseText != null && PlaystyleFormat.TryCanonical(entry.Value.Attribute, baseText, out string canonicalBase) ? canonicalBase : null;

                if (string.Equals(value, baseValue, StringComparison.Ordinal)) continue;

                capture.Overrides[entry.Key] = value;
                if (!string.IsNullOrEmpty(entry.Value.ModId)) capture.SettingMods[entry.Key] = entry.Value.ModId;
            }

            capture.WorldHeight = PlaystyleFormat.EffectiveWorldHeight(config.MapsizeY, capture.BaseCode);

            return capture;
        }

        /// <summary>Where a Save of a given name will land.</summary>
        public sealed class SavePlan
        {
            public string Slug;
            public string Path;

            /// <summary>An existing BetterMods-writable file is in the way; ask before replacing it.</summary>
            public PlaystyleFile Replaces;
        }

        /// <summary>
        /// Picks the file for <paramref name="name"/>: its slug, unless a file of that name (any case)
        /// exists. An existing writable custom preset is offered for overwrite; a read-only (mod) file,
        /// one BetterMods could not read, or anything else in the way moves the slug on (<c>-2</c>, ...).
        /// </summary>
        public static SavePlan PlanSave(string name, out string error)
        {
            error = null;

            string folder = Folder;
            if (folder == null)
            {
                error = "The game's data folder is not known.";
                return null;
            }

            Invalidate();
            Refresh();

            string slug = PlaystyleFormat.MakeSlug(name);
            HashSet<string> taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(folder))
            {
                foreach (string entry in Directory.EnumerateFileSystemEntries(folder)) taken.Add(Path.GetFileName(entry));
            }

            for (int n = 1; n < 1000; n++)
            {
                string candidate = n == 1 ? slug : slug + "-" + n;
                string fileName = candidate + PlaystyleFormat.Extension;

                if (!taken.Contains(fileName)) return new SavePlan { Slug = candidate, Path = Path.Join(folder, fileName) };

                PlaystyleFile existing = files.FirstOrDefault(f => string.Equals(f.Slug, candidate, StringComparison.OrdinalIgnoreCase));
                if (existing != null && !existing.ReadOnly && File.Exists(existing.Path))
                {
                    return new SavePlan { Slug = existing.Slug, Path = existing.Path, Replaces = existing };
                }
            }

            error = "Could not find a free file name for '" + slug + "'.";
            return null;
        }

        /// <summary>
        /// Writes the preset: temp file in the same folder, then moved into place. Re-checks, right
        /// before replacing, that the file in the way is still a writable custom preset. Returns the
        /// new entry's code, or null with <paramref name="error"/> set.
        /// </summary>
        public static string Write(SavePlan plan, string name, string description, SaveCapture capture, out string error)
        {
            error = null;
            string temp = null;

            try
            {
                string folder = Folder;
                if (folder == null || plan == null || capture == null)
                {
                    error = "Nothing to save.";
                    return null;
                }

                string target = Path.GetFullPath(plan.Path);
                if (!IsInsideFolder(target, folder) || !target.EndsWith(PlaystyleFormat.Extension, StringComparison.OrdinalIgnoreCase))
                {
                    error = "Refusing to write outside the Playstyles folder.";
                    return null;
                }

                if (File.Exists(target))
                {
                    if (plan.Replaces == null)
                    {
                        error = "A file named " + Path.GetFileName(target) + " appeared in the meantime.";
                        return null;
                    }

                    PlaystyleFile onDisk = ReadOne(target, out string readError);
                    if (onDisk == null || onDisk.ReadOnly)
                    {
                        error = onDisk == null
                            ? "The existing " + Path.GetFileName(target) + " could not be read (" + readError + "); it was left alone."
                            : "The existing " + Path.GetFileName(target) + " is read-only (supplied by a mod); it was left alone.";
                        return null;
                    }
                }

                PlaystyleFile file = new PlaystyleFile
                {
                    Name = Updates.ModUpdateRules.AsciiLine(name, PlaystyleFormat.MaxNameLength),
                    Description = Updates.ModUpdateRules.AsciiLine(description, PlaystyleFormat.MaxDescriptionLength),
                    BaseCode = capture.BaseCode,
                    WorldHeight = capture.WorldHeight,
                    ReadOnly = false,
                    Author = ""
                };

                foreach (KeyValuePair<string, string> entry in capture.Overrides) file.Overrides.Add(entry);
                foreach (KeyValuePair<string, string> entry in capture.SettingMods) file.SettingMods[entry.Key] = entry.Value;

                string json = PlaystyleFormat.Serialize(file);
                if (Encoding.UTF8.GetByteCount(json) > PlaystyleFormat.MaxFileBytes)
                {
                    error = "The playstyle is too large to save.";
                    return null;
                }

                Directory.CreateDirectory(folder);
                temp = Path.Join(folder, "." + plan.Slug + "." + Guid.NewGuid().ToString("N") + ".tmp");
                File.WriteAllText(temp, json, new UTF8Encoding(false));
                File.Move(temp, target, overwrite: true);
                temp = null;

                BetterModsModSystem.Logger?.Notification("[BetterMods] Saved playstyle '" + file.Name + "' to " + target);

                Invalidate();
                Refresh();
                return files.FirstOrDefault(f => PathsEqual(f.Path, target))?.Code;
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Error("[BetterMods] Saving a playstyle failed.");
                BetterModsModSystem.Logger?.Error(e);
                error = "Could not write the file: " + Updates.ModUpdateRules.AsciiLine(e.Message, 160);
                return null;
            }
            finally
            {
                if (temp != null)
                {
                    try
                    {
                        if (File.Exists(temp)) File.Delete(temp);
                    }
                    catch (Exception)
                    {
                        // A stray temp file is harmless: the scanner ignores anything that is not *.vsplaystyle.
                    }
                }
            }
        }

        // ---------------------------------------------------------------- deleting

        /// <summary>
        /// Deletes a custom preset's file - only after re-checking that it resolves to a plain
        /// <c>.vsplaystyle</c> directly inside the Playstyles folder and still reads as a writable
        /// (not <c>readOnly</c>) preset. Built-in playstyles have no file and can never get here.
        /// Returns null on success, else the reason nothing was deleted.
        /// </summary>
        public static string Delete(PlaystyleFile file)
        {
            try
            {
                string folder = Folder;
                if (file?.Path == null || folder == null) return "That playstyle has no file.";
                if (file.ReadOnly) return "That playstyle is supplied by a mod and cannot be deleted here.";

                string target = Path.GetFullPath(file.Path);
                if (!IsInsideFolder(target, folder) || !target.EndsWith(PlaystyleFormat.Extension, StringComparison.OrdinalIgnoreCase))
                {
                    return "Refusing to delete a file outside the Playstyles folder.";
                }

                if (!File.Exists(target))
                {
                    Invalidate();
                    Refresh();
                    return null;
                }

                if ((File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0) return "The file is a link; it was left alone.";

                PlaystyleFile onDisk = ReadOne(target, out string readError);
                if (onDisk == null) return "The file could not be read (" + readError + "); it was left alone.";
                if (onDisk.ReadOnly) return "The file is marked read-only (supplied by a mod); it was left alone.";

                File.Delete(target);
                BetterModsModSystem.Logger?.Notification("[BetterMods] Deleted playstyle '" + onDisk.Name + "' (" + target + ").");

                Invalidate();
                Refresh();
                return null;
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Error("[BetterMods] Deleting a playstyle failed.");
                BetterModsModSystem.Logger?.Error(e);
                return "Could not delete the file: " + Updates.ModUpdateRules.AsciiLine(e.Message, 160);
            }
        }

        // ---------------------------------------------------------------- helpers

        private static PlaystyleFile ReadOne(string path, out string error)
        {
            error = null;
            FileInfo info = new FileInfo(path);
            if (info.Length > PlaystyleFormat.MaxFileBytes)
            {
                error = "too large";
                return null;
            }

            return PlaystyleFormat.Parse(File.ReadAllText(path, Encoding.UTF8), Path.GetFileNameWithoutExtension(path), out error);
        }

        private static bool IsInsideFolder(string fullPath, string folder)
        {
            string parent = Path.GetDirectoryName(fullPath);
            return parent != null && PathsEqual(parent, folder);
        }

        private static bool PathsEqual(string a, string b)
        {
            if (a == null || b == null) return false;

            string left = Path.TrimEndingDirectorySeparator(Path.GetFullPath(a));
            string right = Path.TrimEndingDirectorySeparator(Path.GetFullPath(b));
            return string.Equals(left, right, OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
        }
    }
}
