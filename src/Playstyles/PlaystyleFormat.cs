using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BetterMods.Updates;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.Common;

namespace BetterMods.Playstyles
{
    /// <summary>
    /// The <c>.vsplaystyle</c> file format: parsing, validation and writing. No game state, no UI.
    /// <para>
    /// The files are DATA ONLY and may come from any mod or be hand-edited, so everything read here
    /// is treated as hostile: the JSON is loaded into a <see cref="JObject"/> (LINQ to JSON - no type
    /// binding, no <c>TypeNameHandling</c>, nothing instantiated or executed) with a depth cap, every
    /// string is bounded and ASCII-cleaned, and every world-config value is later re-parsed through
    /// the defining attribute's own <c>DataType</c> (<see cref="TryCanonical"/>), because vanilla
    /// validates none of it (a dropdown value outside <c>Values</c> reaches the customize screen as
    /// <c>IndexOf</c> -1).
    /// </para>
    /// <para>
    /// The format is deliberately LENIENT: there is no version number to check, unknown top-level
    /// fields (including the <c>formatVersion</c>, <c>gameVersion</c> and <c>createdBy</c> that older
    /// BetterMods builds wrote) are ignored, and <c>format</c> is an optional type marker - the
    /// extension already says what the file is. The only thing that keeps a file out of the list is
    /// one that cannot be read at all, or that has no usable <c>basePlaystyle</c>.
    /// </para>
    /// </summary>
    public static class PlaystyleFormat
    {
        public const string Extension = ".vsplaystyle";

        /// <summary>The optional <c>format</c> marker written into every saved file. Never checked on read.</summary>
        public const string FormatName = "vsplaystyle";

        /// <summary>
        /// Prefix of every menu-side custom playstyle code. Such a code must never reach the server:
        /// <see cref="CustomPlaystyles.RemapForWorldStart"/> swaps the base back in.
        /// </summary>
        public const string CodePrefix = "bettermods-custom:";

        public const long MaxFileBytes = 64 * 1024;
        public const int MaxFiles = 200;
        public const int MaxNameLength = 60;
        public const int MaxDescriptionLength = 200;
        public const int MaxOverrides = 512;
        public const int MaxKeyLength = 128;
        public const int MaxRawValueLength = 1024;
        public const int MaxStringValueLength = 256;
        public const int MaxSlugLength = 128;
        public const int MaxGeneratedSlugLength = 64;
        public const int MaxJsonDepth = 16;

        public const int DefaultWorldHeight = 256;
        public const int MinWorldHeight = 128;
        public const int MaxWorldHeight = 512;
        public const int MaxCreativeWorldHeight = 2048;
        public const int WorldHeightStep = 64;

        private static readonly string[] ReservedDeviceNames =
        {
            "con", "prn", "aux", "nul",
            "com0", "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
            "lpt0", "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9"
        };

        public static bool IsCustomCode(string code)
        {
            return code != null && code.StartsWith(CodePrefix, StringComparison.Ordinal);
        }

        // ---------------------------------------------------------------- file names

        /// <summary>
        /// A file-name stem BetterMods accepts: 1-128 characters from <c>GamePaths.AllowedNameChars</c>
        /// (A-Z a-z 0-9 _ -), not a Windows device name.
        /// </summary>
        public static bool IsValidSlug(string slug)
        {
            if (string.IsNullOrEmpty(slug) || slug.Length > MaxSlugLength) return false;

            string allowed = GamePaths.AllowedNameChars ?? "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz1234567890_-";
            foreach (char c in slug)
            {
                if (allowed.IndexOf(c) < 0) return false;
            }

            return !IsReservedDeviceName(slug);
        }

        public static bool IsReservedDeviceName(string slug)
        {
            return slug != null && ReservedDeviceNames.Contains(slug.ToLowerInvariant());
        }

        /// <summary>
        /// The file-name stem for a display name: lower case, spaces to <c>-</c>, everything outside
        /// A-Z a-z 0-9 _ - dropped, runs of <c>-</c> collapsed, capped at 64. Never empty, never a
        /// device name.
        /// </summary>
        public static string MakeSlug(string name)
        {
            StringBuilder slug = new StringBuilder();
            foreach (char raw in name ?? "")
            {
                char c = char.ToLowerInvariant(raw);
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_')
                {
                    slug.Append(c);
                }
                else if ((c == '-' || char.IsWhiteSpace(c)) && slug.Length > 0 && slug[slug.Length - 1] != '-')
                {
                    slug.Append('-');
                }
            }

            string result = slug.ToString().Trim('-', '_');
            if (result.Length > MaxGeneratedSlugLength) result = result.Substring(0, MaxGeneratedSlugLength).Trim('-', '_');
            if (result.Length == 0) result = "playstyle";
            if (IsReservedDeviceName(result)) result += "-playstyle";

            return result;
        }

        // ---------------------------------------------------------------- reading

        /// <summary>
        /// Parses one file's text. Returns null, with <paramref name="error"/> set, only when the file
        /// is not JSON, is too large, or has no plain built-in <c>basePlaystyle</c>. Everything else is
        /// tolerated: <c>format</c>, any version field and any unknown field are ignored, and bad
        /// world-config entries only count towards <see cref="PlaystyleFile.RejectedKeys"/>.
        /// </summary>
        public static PlaystyleFile Parse(string json, string slug, out string error)
        {
            error = null;

            if (json == null || json.Length > MaxFileBytes)
            {
                error = "file is larger than " + (MaxFileBytes / 1024) + " KB";
                return null;
            }

            JObject root;
            try
            {
                using StringReader text = new StringReader(json);
                using JsonTextReader reader = new JsonTextReader(text)
                {
                    MaxDepth = MaxJsonDepth,
                    DateParseHandling = DateParseHandling.None,
                    FloatParseHandling = FloatParseHandling.Double
                };

                root = JObject.Load(reader, new JsonLoadSettings
                {
                    CommentHandling = CommentHandling.Ignore,
                    LineInfoHandling = LineInfoHandling.Ignore,
                    DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Replace
                });
            }
            catch (Exception e)
            {
                error = "not valid JSON (" + ModUpdateRules.AsciiLine(e.Message, 120) + ")";
                return null;
            }

            // "format", "formatVersion", "gameVersion", "createdBy" and any other top-level field this
            // build does not use are ignored on purpose: a version number never keeps a file out.

            string baseCode = StringOf(root["basePlaystyle"])?.Trim();
            if (!IsPlainCode(baseCode))
            {
                error = "\"basePlaystyle\" is missing or not a plain playstyle code";
                return null;
            }

            if (IsCustomCode(baseCode))
            {
                error = "\"basePlaystyle\" must be a built-in playstyle, not another custom one";
                return null;
            }

            PlaystyleFile file = new PlaystyleFile
            {
                Slug = slug,
                BaseCode = baseCode,
                Name = ModUpdateRules.AsciiLine(StringOf(root["name"]), MaxNameLength),
                Description = ModUpdateRules.AsciiLine(StringOf(root["description"]), MaxDescriptionLength),
                Author = ModUpdateRules.AsciiLine(StringOf(root["author"]), MaxNameLength),
                ReadOnly = IsTrue(root["readOnly"]),
                WorldHeight = IntOf(root["worldHeight"])
            };

            if (string.IsNullOrEmpty(file.Name)) file.Name = slug;

            if (root["worldConfig"] is JObject config)
            {
                foreach (JProperty property in config.Properties())
                {
                    if (file.Overrides.Count >= MaxOverrides || !IsPlainCode(property.Name) || property.Name.Length > MaxKeyLength)
                    {
                        file.RejectedKeys++;
                        continue;
                    }

                    string raw = TokenText(property.Value);
                    if (raw == null || raw.Length > MaxRawValueLength)
                    {
                        file.RejectedKeys++;
                        continue;
                    }

                    file.Overrides.Add(new KeyValuePair<string, string>(property.Name, raw));
                }
            }
            else if (root["worldConfig"] != null && root["worldConfig"].Type != JTokenType.Null)
            {
                file.RejectedKeys++;
            }

            if (root["settingMods"] is JObject sources)
            {
                foreach (JProperty property in sources.Properties().Take(MaxOverrides))
                {
                    string modId = ModUpdateRules.AsciiLine(StringOf(property.Value), MaxKeyLength);
                    if (IsPlainCode(property.Name) && !string.IsNullOrEmpty(modId)) file.SettingMods[property.Name] = modId;
                }
            }

            return file;
        }

        /// <summary>
        /// A JSON primitive as text, invariant culture: strings verbatim, booleans <c>true</c>/<c>false</c>,
        /// numbers round-trippable. Null for anything else (objects, arrays, null).
        /// </summary>
        public static string TokenText(JToken token)
        {
            if (!(token is JValue value)) return null;

            switch (value.Type)
            {
                case JTokenType.String:
                    return (string)value.Value;
                case JTokenType.Boolean:
                    return (bool)value.Value ? "true" : "false";
                case JTokenType.Integer:
                    return Convert.ToString(value.Value, CultureInfo.InvariantCulture);
                case JTokenType.Float:
                    return Convert.ToDouble(value.Value, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture);
                default:
                    return null;
            }
        }

        // ---------------------------------------------------------------- validation

        /// <summary>
        /// Re-parses <paramref name="raw"/> as the attribute's own <c>DataType</c> and returns the
        /// canonical string vanilla's playstyles use (<c>"true"</c>, <c>"1"</c>, <c>"0.5"</c>,
        /// dropdown values verbatim). Ranges are clamped to <c>[Min, Max]</c>; a dropdown or
        /// string-range value outside <c>Values</c>, an unparseable number, a non-finite double and a
        /// free-text value that is too long or would be markup in a tooltip are all rejected (false),
        /// so the caller keeps the base playstyle's value.
        /// </summary>
        public static bool TryCanonical(WorldConfigurationAttribute attribute, string raw, out string canonical)
        {
            canonical = null;
            if (attribute == null || raw == null) return false;

            string text = raw.Trim();
            CultureInfo inv = CultureInfo.InvariantCulture;

            switch (attribute.DataType)
            {
                case EnumDataType.Bool:
                    if (!bool.TryParse(text, out bool flag)) return false;
                    canonical = flag ? "true" : "false";
                    return true;

                case EnumDataType.IntInput:
                case EnumDataType.IntRange:
                {
                    if (!TryParseInt(text, out int number)) return false;
                    if (attribute.DataType == EnumDataType.IntRange && attribute.Max > attribute.Min)
                    {
                        number = Math.Clamp(number, (int)attribute.Min, (int)attribute.Max);
                    }

                    canonical = number.ToString(inv);
                    return true;
                }

                case EnumDataType.DoubleInput:
                case EnumDataType.DoubleRange:
                {
                    if (!double.TryParse(text, NumberStyles.Float, inv, out double number) || double.IsNaN(number) || double.IsInfinity(number)) return false;
                    if (attribute.DataType == EnumDataType.DoubleRange && attribute.Max > attribute.Min)
                    {
                        number = Math.Clamp(number, attribute.Min, attribute.Max);
                    }

                    canonical = number.ToString("R", inv);
                    return true;
                }

                case EnumDataType.String:
                    if (raw.Length > MaxStringValueLength) return false;
                    foreach (char c in raw)
                    {
                        if (c < 0x20 || c == 0x7f || c == '<' || c == '>') return false;
                    }

                    canonical = raw;
                    return true;

                case EnumDataType.DropDown:
                case EnumDataType.StringRange:
                    if (attribute.Values == null || Array.IndexOf(attribute.Values, raw) < 0) return false;
                    canonical = raw;
                    return true;

                default:
                    return false;
            }
        }

        private static bool TryParseInt(string text, out int number)
        {
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out number)) return true;

            // "5.0" from a hand-edited file is still five.
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
                && !double.IsNaN(d) && !double.IsInfinity(d) && Math.Floor(d) == d && d >= int.MinValue && d <= int.MaxValue)
            {
                number = (int)d;
                return true;
            }

            return false;
        }

        /// <summary>
        /// The world height a preset actually gets: the requested one (256 when absent) made a valid
        /// map size by vanilla's own <c>WorldConfig.ClampToValidMapSize</c>, then clamped and snapped
        /// to the customize screen's slider (128-512 in steps of 64; 128-2048 for creativebuilding).
        /// </summary>
        public static int EffectiveWorldHeight(int? requested, string baseCode)
        {
            int max = baseCode == "creativebuilding" ? MaxCreativeWorldHeight : MaxWorldHeight;
            int height = requested ?? DefaultWorldHeight;

            height = WorldConfig.ClampToValidMapSize(height, max);
            height = Math.Clamp(height, MinWorldHeight, max);
            height = MinWorldHeight + (int)Math.Round((height - MinWorldHeight) / (double)WorldHeightStep, MidpointRounding.AwayFromZero) * WorldHeightStep;

            return Math.Clamp(height, MinWorldHeight, max);
        }

        // ---------------------------------------------------------------- writing

        /// <summary>
        /// The file text for <paramref name="file"/>, indented, values as strings like vanilla's
        /// playstyles. No version or tool fields: <c>format</c>, <c>name</c>, <c>description</c>,
        /// <c>basePlaystyle</c>, <c>worldHeight</c>, <c>worldConfig</c>, <c>settingMods</c> (when
        /// any), <c>readOnly</c>, <c>author</c>.
        /// </summary>
        public static string Serialize(PlaystyleFile file)
        {
            JObject config = new JObject();
            foreach (KeyValuePair<string, string> entry in file.Overrides.OrderBy(e => e.Key, StringComparer.Ordinal))
            {
                config[entry.Key] = entry.Value;
            }

            JObject root = new JObject
            {
                ["format"] = FormatName,
                ["name"] = file.Name ?? "",
                ["description"] = file.Description ?? "",
                ["basePlaystyle"] = file.BaseCode,
            };

            if (file.WorldHeight.HasValue) root["worldHeight"] = file.WorldHeight.Value;
            root["worldConfig"] = config;

            if (file.SettingMods.Count > 0)
            {
                JObject sources = new JObject();
                foreach (KeyValuePair<string, string> entry in file.SettingMods.OrderBy(e => e.Key, StringComparer.Ordinal))
                {
                    sources[entry.Key] = entry.Value;
                }

                root["settingMods"] = sources;
            }

            root["readOnly"] = file.ReadOnly;
            root["author"] = file.Author ?? "";

            return root.ToString(Formatting.Indented);
        }

        // ---------------------------------------------------------------- helpers

        private static string StringOf(JToken token)
        {
            return token is JValue value && value.Type == JTokenType.String ? (string)value.Value : null;
        }

        private static bool IsTrue(JToken token)
        {
            if (!(token is JValue value)) return false;
            if (value.Type == JTokenType.Boolean) return (bool)value.Value;
            return value.Type == JTokenType.String && string.Equals((string)value.Value, "true", StringComparison.OrdinalIgnoreCase);
        }

        private static int? IntOf(JToken token)
        {
            string text = TokenText(token);
            return text != null && TryParseInt(text.Trim(), out int number) ? number : (int?)null;
        }

        /// <summary>Printable ASCII, no whitespace, no markup characters, 1-128 long.</summary>
        private static bool IsPlainCode(string code)
        {
            if (string.IsNullOrEmpty(code) || code.Length > MaxKeyLength) return false;

            foreach (char c in code)
            {
                if (c <= 0x20 || c >= 0x7f || c == '<' || c == '>' || c == '"' || c == '\\') return false;
            }

            return true;
        }
    }
}
