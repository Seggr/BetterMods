using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace BetterMods.Updates
{
    /// <summary>One installed mod as the update check sees it: its id and its newest installed version.</summary>
    public sealed class InstalledModSnapshot
    {
        public InstalledModSnapshot(string modId, string newestVersion)
        {
            ModId = modId;
            NewestVersion = newestVersion;
        }

        public string ModId { get; }

        public string NewestVersion { get; }
    }

    /// <summary>
    /// One entry of the mod DB's <c>install-information</c> answer. Every field is optional on the
    /// wire; <see cref="ErrorCode"/> is 0 when the server reported none.
    /// </summary>
    public sealed class ModDbEntry
    {
        public int ErrorCode;
        public string RecommendedUpgrade;
        public string FileName;
        public string FileUrl;
        public string RetractionReason;

        public bool IsRetracted => ErrorCode == ModUpdateRules.ErrorReleaseRetracted || ErrorCode == ModUpdateRules.ErrorReleaseRetractedForced;
    }

    /// <summary>
    /// The pure half of the mod update feature: which specs to send, how to read the answer, whether
    /// an answer is an update, and whether a download is something we are willing to write to disk.
    /// <para>
    /// Nothing here touches the game, the network or the file system, so all of it can be exercised
    /// with plain strings. Every method is total - bad input is an answer ("not an update",
    /// "rejected"), never an exception.
    /// </para>
    /// </summary>
    public static class ModUpdateRules
    {
        /// <summary>Specs per request. The DB documents no limit; this keeps URLs short and failures local.</summary>
        public const int SpecsPerRequest = 50;

        public const int ErrorSpecParseFailed = 4001;
        public const int ErrorSpecNotFound = 4041;
        public const int ErrorReleaseRetracted = 4101;
        public const int ErrorReleaseRetractedForced = 4102;

        /// <summary>The only hosts a mod file may be fetched from - the DB itself and the CDN it redirects to.</summary>
        public static readonly string[] AllowedDownloadHosts = { "mods.vintagestory.at", "moddbcdn.vintagestory.at" };

        /// <summary>
        /// The version shape the mod DB accepts in a spec
        /// (<c>anegostudios/vsmoddb</c>, <c>lib/api/public/mods.php</c>). Anything else answers 4001.
        /// </summary>
        private static readonly Regex DbVersion = new Regex(@"^\d+\.\d+\.\d+(?:-(?:dev|pre|rc)\.\d+)?$", RegexOptions.CultureInvariant);

        /// <summary>
        /// The shapes this file is willing to COMPARE: one to four numeric parts, optionally followed by
        /// a <c>dev</c>/<c>pre</c>/<c>rc</c> tag with an optional number. Wider than
        /// <see cref="DbVersion"/> on purpose, so a locally installed <c>1.0</c> can still be compared
        /// with a DB release <c>1.0.1</c>; anything wider still is "cannot tell", never a guess.
        /// </summary>
        private static readonly Regex ComparableVersion =
            new Regex(@"^(\d+)(?:\.(\d+))?(?:\.(\d+))?(?:\.(\d+))?(?:-(dev|pre|rc)(?:\.(\d+))?)?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        /// <summary>Mod ids vanilla never offers to manage from the DB (<c>ModDbUtil</c> excludes them too).</summary>
        private static readonly HashSet<string> BundledModIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "game", "creative", "survival"
        };

        public static bool IsBundledModId(string modId) => !string.IsNullOrEmpty(modId) && BundledModIds.Contains(modId);

        /// <summary>True when the mod DB will accept <paramref name="version"/> in a <c>modid@version</c> spec.</summary>
        public static bool IsDbVersion(string version)
        {
            return !string.IsNullOrEmpty(version) && DbVersion.IsMatch(version);
        }

        // ---------------------------------------------------------------- version comparison

        /// <summary>
        /// Orders two versions: positive when <paramref name="a"/> is newer, negative when older, 0
        /// when equal - or null when either is not a shape this file understands. Release ranks above
        /// <c>rc</c>, above <c>pre</c>, above <c>dev</c>, which is the order <c>GameVersion</c> uses.
        /// Missing numeric parts count as 0, so <c>1.0</c> equals <c>1.0.0</c>.
        /// </summary>
        public static int? CompareVersions(string a, string b)
        {
            if (!TryParse(a, out long[] left) || !TryParse(b, out long[] right)) return null;

            for (int i = 0; i < left.Length; i++)
            {
                int order = left[i].CompareTo(right[i]);
                if (order != 0) return order;
            }

            return 0;
        }

        /// <summary>True for a comparable version without a pre-release tag.</summary>
        public static bool IsStable(string version)
        {
            return TryParse(version, out long[] parts) && parts[4] == ReleaseRank;
        }

        /// <summary>
        /// The newest of <paramref name="versions"/> that this file can compare, or null when none
        /// can be. Versions it cannot parse are skipped rather than guessed at.
        /// </summary>
        public static string NewestComparable(IEnumerable<string> versions)
        {
            string newest = null;
            if (versions == null) return null;

            foreach (string version in versions)
            {
                if (CompareVersions(version, version) == null) continue;
                if (newest == null || CompareVersions(version, newest) > 0) newest = version;
            }

            return newest;
        }

        private const long ReleaseRank = 3;

        /// <summary>
        /// Five slots: four numeric parts, then the release rank (dev 0, pre 1, rc 2, release 3), then
        /// the tag number. The rank sits after the numbers so <c>1.2.3-rc.1</c> orders just below
        /// <c>1.2.3</c> and above <c>1.2.2</c>.
        /// </summary>
        private static bool TryParse(string version, out long[] parts)
        {
            parts = null;
            if (string.IsNullOrWhiteSpace(version)) return false;

            Match match = ComparableVersion.Match(version.Trim());
            if (!match.Success) return false;

            long[] result = new long[6];
            for (int i = 0; i < 4; i++)
            {
                Group group = match.Groups[i + 1];
                if (!group.Success) continue;
                if (!long.TryParse(group.Value, out result[i])) return false;
            }

            string tag = match.Groups[5].Success ? match.Groups[5].Value.ToLowerInvariant() : null;
            result[4] = tag switch
            {
                null => ReleaseRank,
                "rc" => 2,
                "pre" => 1,
                _ => 0
            };

            if (match.Groups[6].Success && !long.TryParse(match.Groups[6].Value, out result[5])) return false;

            parts = result;
            return true;
        }

        // ---------------------------------------------------------------- the decision

        /// <summary>
        /// The version to offer as an update over <paramref name="installedNewest"/>, or null.
        /// <para>
        /// Only ever upward: a recommendation that is older than, equal to, or not comparable with
        /// what is installed is not an update (an installed <c>2.0.0-rc.1</c> against a newest stable
        /// <c>1.14.3</c> is a downgrade and is refused). And never from a stable install onto a
        /// pre-release - the DB does not recommend those, and if it ever did the player has not
        /// opted into pre-releases by installing a stable build.
        /// </para>
        /// </summary>
        public static string DecideUpdate(string installedNewest, string recommended)
        {
            if (string.IsNullOrWhiteSpace(installedNewest) || string.IsNullOrWhiteSpace(recommended)) return null;

            int? order = CompareVersions(recommended, installedNewest);
            if (order == null || order.Value <= 0) return null;

            if (!IsStable(recommended) && IsStable(installedNewest)) return null;

            return recommended.Trim();
        }

        // ---------------------------------------------------------------- older game versions (tier 2)

        /// <summary>
        /// Reads <c>GET &lt;ModDbUrl&gt;api/gameversions</c> (verified live 2026-11-23:
        /// <c>{"statuscode":"200","gameversions":[{"tagid":-2815...,"name":"1.22.7","color":"#CCCCCC"},...]}</c>,
        /// ~16 KB, oldest first) into the version names, or null when the body is not that shape.
        /// Order is not relied upon.
        /// </summary>
        public static List<string> ParseGameVersions(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;

            JObject root;
            try
            {
                root = JObject.Parse(json);
            }
            catch (Exception)
            {
                return null;
            }

            string status = ReadString(GetIgnoreCase(root, "statuscode"));
            if (status != null && status != "200") return null;
            if (!(GetIgnoreCase(root, "gameversions") is JArray list)) return null;

            List<string> versions = new List<string>();
            foreach (JToken item in list)
            {
                string name = item is JObject entry ? ReadString(GetIgnoreCase(entry, "name")) : null;
                if (!string.IsNullOrWhiteSpace(name)) versions.Add(name.Trim());
            }

            return versions;
        }

        /// <summary>
        /// The game version tier 2 asks the DB about: the newest STABLE version (no dev/pre/rc tag)
        /// strictly older than <paramref name="runningVersion"/>, or null when there is none or the
        /// running version cannot be compared. On <c>1.23.0-pre.1</c> with the live list that is
        /// <c>1.22.7</c>; on a <c>1.23.1</c> it would be <c>1.23.0</c>.
        /// </summary>
        public static string OlderStableGameVersion(IEnumerable<string> versions, string runningVersion)
        {
            if (versions == null || CompareVersions(runningVersion, runningVersion) == null) return null;

            string best = null;
            foreach (string version in versions)
            {
                if (!IsStable(version) || !IsDbVersion(version)) continue;

                int? order = CompareVersions(version, runningVersion);
                if (order == null || order.Value >= 0) continue;

                if (best == null || CompareVersions(version, best) > 0) best = version;
            }

            return best;
        }

        /// <summary><c>"1.22"</c> for <c>"1.22.7"</c> - the short label a card button carries. The input when it has no second part.</summary>
        public static string MajorMinor(string version)
        {
            if (string.IsNullOrWhiteSpace(version)) return "";

            string[] parts = version.Trim().Split('.');
            return parts.Length >= 2 ? parts[0] + "." + parts[1] : version.Trim();
        }

        /// <summary>
        /// The short label for a game version that is not the running one - what the tier-2 card
        /// button and the version picker's "(1.22)" hint carry. Major.minor is enough when it is a
        /// different minor (<c>"1.22"</c> on 1.23.x); within the running minor (1.23.0 on 1.23.1) only
        /// the full version tells the player it is not their own.
        /// </summary>
        public static string GameVersionLabel(string otherGameVersion, string runningGameVersion)
        {
            if (string.IsNullOrWhiteSpace(otherGameVersion)) return "";

            string label = MajorMinor(otherGameVersion);
            return label == MajorMinor(runningGameVersion) ? otherGameVersion.Trim() : label;
        }

        // ---------------------------------------------------------------- specs

        /// <summary>
        /// One spec per mod id - the DB answers keyed by mod id, so a second spec for the same id
        /// overwrites the first. <paramref name="versioned"/> sends <c>modid@version</c> (only for
        /// versions the DB can parse; others are skipped), otherwise the bare id, which asks for the
        /// newest stable release tagged for the requested game version.
        /// </summary>
        public static List<string> BuildSpecs(IEnumerable<InstalledModSnapshot> mods, bool versioned)
        {
            List<string> specs = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (mods == null) return specs;

            foreach (InstalledModSnapshot mod in mods)
            {
                if (mod == null || !IsSpecModId(mod.ModId)) continue;
                if (versioned && !IsDbVersion(mod.NewestVersion)) continue;
                if (!seen.Add(mod.ModId)) continue;

                specs.Add(versioned ? mod.ModId + "@" + mod.NewestVersion : mod.ModId);
            }

            return specs;
        }

        /// <summary><c>modid@version</c> for an exact release, or null when either half cannot go into a spec.</summary>
        public static string ReleaseSpec(string modId, string version)
        {
            return IsSpecModId(modId) && IsDbVersion(version) ? modId + "@" + version : null;
        }

        /// <summary>
        /// A mod id that can travel in the comma-separated <c>ids</c> parameter unescaped and
        /// unambiguous. <c>ModInfo.ToModID</c> only ever produces letters, digits and underscores; a
        /// hand-written id with a comma or an <c>@</c> would split into a different spec.
        /// </summary>
        private static bool IsSpecModId(string modId)
        {
            if (string.IsNullOrEmpty(modId) || modId.Length > 100) return false;

            foreach (char c in modId)
            {
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '-' || c == '.';
                if (!ok) return false;
            }

            return true;
        }

        /// <summary>Splits <paramref name="items"/> into runs of at most <paramref name="size"/>.</summary>
        public static List<List<T>> Chunk<T>(IReadOnlyList<T> items, int size)
        {
            List<List<T>> chunks = new List<List<T>>();
            if (items == null || items.Count == 0) return chunks;
            if (size < 1) size = 1;

            for (int i = 0; i < items.Count; i += size)
            {
                chunks.Add(new List<T>(SliceOf(items, i, Math.Min(size, items.Count - i))));
            }

            return chunks;
        }

        private static IEnumerable<T> SliceOf<T>(IReadOnlyList<T> items, int start, int count)
        {
            for (int i = 0; i < count; i++) yield return items[start + i];
        }

        /// <summary>The query string for one request (everything after <c>install-information?</c>).</summary>
        public static string BuildQuery(IEnumerable<string> specs, string gameVersion)
        {
            StringBuilder query = new StringBuilder("gv=").Append(Uri.EscapeDataString(gameVersion ?? ""));
            query.Append("&ids=");

            bool first = true;
            foreach (string spec in specs ?? Array.Empty<string>())
            {
                if (!first) query.Append(',');
                query.Append(Uri.EscapeDataString(spec));
                first = false;
            }

            return query.ToString();
        }

        // ---------------------------------------------------------------- response

        /// <summary>
        /// Reads an <c>install-information</c> body into one entry per mod id (case-insensitive), or
        /// null when the body is not that shape at all.
        /// <para>
        /// Observed live (not what the README says): the answer is keyed by the mod id as the DB
        /// knows it, not by the spec as sent, and a spec whose id differs from the DB's only in case
        /// comes back TWICE - a 4041 under the id as sent and the real answer under the canonical id.
        /// So a case-insensitive collision keeps the entry without an error code.
        /// </para>
        /// </summary>
        public static Dictionary<string, ModDbEntry> ParseInstallInformation(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;

            JObject root;
            try
            {
                root = JObject.Parse(json);
            }
            catch (Exception)
            {
                return null;
            }

            if (!(GetIgnoreCase(root, "data") is JObject data)) return null;

            Dictionary<string, ModDbEntry> entries = new Dictionary<string, ModDbEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (JProperty property in data.Properties())
            {
                if (!(property.Value is JObject value)) continue;

                ModDbEntry entry = new ModDbEntry
                {
                    ErrorCode = ReadInt(GetIgnoreCase(value, "errorCode")),
                    RecommendedUpgrade = ReadString(GetIgnoreCase(value, "recommendedUpgrade")),
                    FileName = ReadString(GetIgnoreCase(value, "fileName")),
                    FileUrl = ReadString(GetIgnoreCase(value, "fileUrl")),
                    RetractionReason = ReadString(GetIgnoreCase(value, "retractionReason"))
                };

                string key = property.Name ?? "";
                if (entries.TryGetValue(key, out ModDbEntry existing) && existing.ErrorCode == 0 && entry.ErrorCode != 0) continue;

                entries[key] = entry;
            }

            return entries;
        }

        internal static JToken GetIgnoreCase(JObject obj, string name)
        {
            return obj?.GetValue(name, StringComparison.OrdinalIgnoreCase);
        }

        internal static string ReadString(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return null;
            if (token.Type == JTokenType.String || token.Type == JTokenType.Integer || token.Type == JTokenType.Float) return token.ToString();
            return null;
        }

        internal static int ReadInt(JToken token)
        {
            if (token == null) return 0;
            if (token.Type == JTokenType.Integer) return (int)(long)token;
            return token.Type == JTokenType.String && int.TryParse((string)token, out int value) ? value : 0;
        }

        // ---------------------------------------------------------------- download validation

        /// <summary>
        /// The server's <c>fileName</c> as a bare file name we are willing to create, or null with a
        /// reason. Vanilla (<c>ModDbUtil.DownloadModAsync</c>) does <c>Path.Combine(installPath,
        /// fileName)</c> on it unchecked; here it must be a plain <c>.zip</c> name that cannot climb out
        /// of the mods folder, name a device, or hide from the mod scanner behind a leading dot.
        /// </summary>
        public static string ValidateFileName(string fileName, out string reason)
        {
            reason = null;

            if (string.IsNullOrWhiteSpace(fileName))
            {
                reason = "the mod DB gave no file name";
                return null;
            }

            string name = fileName.Trim();
            if (name.Length > 150 || name != fileName || Path.IsPathRooted(name) || name.Contains('/') || name.Contains('\\')
                || name.Contains(':') || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.IndexOfAny(WindowsInvalidChars) >= 0
                || Path.GetFileName(name) != name || name.StartsWith(".", StringComparison.Ordinal) || name.EndsWith(".", StringComparison.Ordinal)
                || name.Contains(".."))
            {
                reason = "the mod DB gave an unusable file name";
                return null;
            }

            if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || name.Length <= 4)
            {
                reason = "the mod DB file is not a .zip";
                return null;
            }

            foreach (char c in name)
            {
                if (c < 0x20 || c == 0x7f)
                {
                    reason = "the mod DB gave an unusable file name";
                    return null;
                }
            }

            string stem = Path.GetFileNameWithoutExtension(name).ToUpperInvariant();
            int dot = stem.IndexOf('.');
            if (dot >= 0) stem = stem.Substring(0, dot);
            if (ReservedDeviceNames.Contains(stem.TrimEnd(' ')))
            {
                reason = "the mod DB gave an unusable file name";
                return null;
            }

            return name;
        }

        /// <summary>
        /// Path.GetInvalidFileNameChars is platform-dependent (on Linux only NUL and '/'), but a mod
        /// folder is often synced between machines, so the Windows set is enforced everywhere.
        /// </summary>
        private static readonly char[] WindowsInvalidChars = { '<', '>', ':', '"', '/', '\\', '|', '?', '*' };

        private static readonly HashSet<string> ReservedDeviceNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };

        /// <summary>
        /// Resolves the DB's relative <c>fileUrl</c> against the configured mod DB address and accepts
        /// it only as https on an allowed host. The same host test must be applied again to the final
        /// address after redirects (<see cref="IsAllowedDownloadUri"/>).
        /// </summary>
        public static Uri ResolveDownloadUri(string modDbUrl, string fileUrl, out string reason)
        {
            reason = null;

            if (string.IsNullOrWhiteSpace(fileUrl))
            {
                reason = "the mod DB gave no download address";
                return null;
            }

            if (!Uri.TryCreate(modDbUrl ?? "", UriKind.Absolute, out Uri baseUri) || !Uri.TryCreate(baseUri, fileUrl.Trim(), out Uri resolved))
            {
                reason = "the download address could not be read";
                return null;
            }

            if (!IsAllowedDownloadUri(resolved))
            {
                reason = "the download address is not on the official mod DB";
                return null;
            }

            return resolved;
        }

        /// <summary>https, default port, no credentials, and one of <see cref="AllowedDownloadHosts"/>.</summary>
        public static bool IsAllowedDownloadUri(Uri uri)
        {
            if (uri == null || !uri.IsAbsoluteUri) return false;
            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;
            if (!uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo)) return false;

            foreach (string host in AllowedDownloadHosts)
            {
                if (string.Equals(uri.IdnHost, host, StringComparison.OrdinalIgnoreCase)) return true;
            }

            return false;
        }

        // ---------------------------------------------------------------- display

        /// <summary>
        /// Third-party text made safe for Cairo's toy font API (no glyph fallback) and for a one-line
        /// slot: non-ASCII becomes '?', control characters become spaces, whitespace runs collapse, and
        /// the result is cut to <paramref name="maxLength"/> with an ASCII ellipsis.
        /// </summary>
        public static string AsciiLine(string text, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";

            StringBuilder line = new StringBuilder(text.Length);
            bool lastSpace = false;
            foreach (char c in text)
            {
                char mapped = c < 0x20 || c == 0x7f || char.IsWhiteSpace(c) ? ' ' : c > 0x7e ? '?' : c;
                if (mapped == ' ')
                {
                    if (lastSpace) continue;
                    lastSpace = true;
                }
                else
                {
                    lastSpace = false;
                }

                line.Append(mapped);
            }

            string result = line.ToString().Trim();
            if (maxLength > 3 && result.Length > maxLength) result = result.Substring(0, maxLength - 3).TrimEnd() + "...";
            return result;
        }

        private static readonly Regex HtmlTag = new Regex(@"<[^>]*>", RegexOptions.CultureInvariant);

        private static readonly Regex HtmlBreak = new Regex(@"<\s*(?:br|/p|/div|/li|/h[1-6])\b[^>]*>", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private static readonly Regex NumericEntity = new Regex(@"&#(x[0-9a-fA-F]{1,6}|[0-9]{1,7});", RegexOptions.CultureInvariant);

        /// <summary>
        /// Server-provided rich text (a retraction reason arrives as <c>&lt;p&gt;...&lt;/p&gt;</c>, observed
        /// live) reduced to plain text: block-closing tags and <c>&lt;br&gt;</c> become line breaks, every
        /// other tag is dropped, and the handful of entities the DB's editor emits are decoded. Not an
        /// HTML parser - a <c>&lt;</c> with no closing <c>&gt;</c> is left as text. Never throws; null
        /// in, empty out. The result is still raw text: pass it through <see cref="AsciiLine"/> (or an
        /// equivalent) before it reaches Cairo or VTML.
        /// </summary>
        public static string StripHtml(string html)
        {
            if (string.IsNullOrEmpty(html)) return "";

            string text = HtmlBreak.Replace(html, "\n");
            text = HtmlTag.Replace(text, "");
            text = NumericEntity.Replace(text, m =>
            {
                string value = m.Groups[1].Value;
                bool hex = value.StartsWith("x", StringComparison.OrdinalIgnoreCase);
                if (!int.TryParse(hex ? value.Substring(1) : value, hex ? System.Globalization.NumberStyles.HexNumber : System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out int code)) return " ";

                // Control characters and anything past the BMP collapse to a space; AsciiLine turns
                // the rest of the non-ASCII range into '?' later.
                return code >= 0x20 && code < 0xD800 ? ((char)code).ToString() : " ";
            });

            return text
                .Replace("&nbsp;", " ")
                .Replace("&quot;", "\"")
                .Replace("&#39;", "'")
                .Replace("&apos;", "'")
                .Replace("&lt;", "<")
                .Replace("&gt;", ">")
                .Replace("&amp;", "&")
                .Trim();
        }

        // ---------------------------------------------------------------- mod page

        /// <summary>
        /// A <c>urlalias</c> we are willing to put in a path: what the DB itself generates (letters,
        /// digits, '-' and '_'). Anything else falls back to the numeric <c>show/mod/</c> address.
        /// </summary>
        private static readonly Regex UrlAlias = new Regex(@"^[A-Za-z0-9][A-Za-z0-9_-]{0,99}$", RegexOptions.CultureInvariant);

        /// <summary>
        /// The configured mod DB root (<c>ClientSettings.ModDbUrl</c>) normalised to end in '/', or null
        /// unless it is an absolute https address without credentials. The mod page button only ever
        /// opens pages under this root.
        /// </summary>
        public static string HttpsModDbRoot(string modDbUrl)
        {
            if (string.IsNullOrWhiteSpace(modDbUrl)) return null;

            string root = modDbUrl.Trim();
            if (!root.EndsWith("/", StringComparison.Ordinal)) root += "/";

            if (!Uri.TryCreate(root, UriKind.Absolute, out Uri uri)) return null;
            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return null;
            if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) return null;

            return uri.AbsoluteUri;
        }

        /// <summary>
        /// Reads the v1 <c>api/mod/&lt;modid&gt;</c> answer (verified live:
        /// <c>{"statuscode":"200","mod":{"assetid":9551,"urlalias":"configlib",...}}</c>; an unknown id
        /// answers <c>{"statuscode":"404"}</c>). False for anything that is not a found mod.
        /// </summary>
        public static bool ParseModPage(string json, out int assetId, out string urlAlias)
        {
            assetId = 0;
            urlAlias = null;
            if (string.IsNullOrWhiteSpace(json)) return false;

            JObject root;
            try
            {
                root = JObject.Parse(json);
            }
            catch (Exception)
            {
                return false;
            }

            string status = ReadString(GetIgnoreCase(root, "statuscode"));
            if (status != null && status != "200") return false;
            if (!(GetIgnoreCase(root, "mod") is JObject mod)) return false;

            assetId = ReadInt(GetIgnoreCase(mod, "assetid"));
            urlAlias = ReadString(GetIgnoreCase(mod, "urlalias"));
            return assetId > 0 || !string.IsNullOrEmpty(urlAlias);
        }

        /// <summary>
        /// The mod's page on the DB: <c>&lt;root&gt;&lt;urlalias&gt;</c> when the alias is a plain slug (the
        /// form the DB links to itself), else <c>&lt;root&gt;show/mod/&lt;assetid&gt;</c> (both verified live
        /// to answer 200). Null when the root is not https or neither part is usable.
        /// </summary>
        public static string BuildModPageUrl(string modDbUrl, string urlAlias, int assetId)
        {
            string root = HttpsModDbRoot(modDbUrl);
            if (root == null) return null;

            if (!string.IsNullOrEmpty(urlAlias) && UrlAlias.IsMatch(urlAlias)) return root + urlAlias;
            if (assetId > 0) return root + "show/mod/" + assetId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return null;
        }

        /// <summary>What may follow the mod DB root in a page address <see cref="BuildModPageUrl"/> made.</summary>
        private static readonly Regex ModPagePath = new Regex(@"^(?:[A-Za-z0-9][A-Za-z0-9_-]{0,99}|show/mod/[1-9][0-9]{0,9})$", RegexOptions.CultureInvariant);

        /// <summary>A lower-case DNS host name: dot-separated LDH labels, at least two of them.</summary>
        private static readonly Regex HostName = new Regex(@"^(?=.{1,253}$)[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?(?:\.[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?)+$", RegexOptions.CultureInvariant);

        /// <summary>
        /// True when <paramref name="host"/> is something the link trust list may hold: a lower-case
        /// ASCII DNS name with at least one dot. No port, no credentials, no wildcard, no IP literal.
        /// </summary>
        public static bool IsTrustableHostName(string host)
        {
            return !string.IsNullOrEmpty(host) && HostName.IsMatch(host) && Uri.CheckHostName(host) == UriHostNameType.Dns;
        }

        /// <summary>
        /// The host the "always trust" choice may remember for <paramref name="url"/>, or null when the
        /// address must not take part in link trust at all (the caller then falls back to the game's
        /// own confirm-every-time link path). Pure; re-run on every click.
        /// <para>
        /// The address qualifies only if it is EXACTLY a mod page <see cref="BuildModPageUrl"/> would
        /// build under the CURRENT mod DB root <paramref name="modDbUrl"/>: absolute <c>https</c>; no
        /// credentials, query or fragment; the scheme's default port and no explicit one; a lower-case
        /// ASCII DNS host (no IP literal, no IDN) that is the root's own host; its canonical form
        /// identical to the string given; and nothing after the root but a plain url alias or
        /// <c>show/mod/&lt;assetid&gt;</c>. The root itself must pass the same host and port rules.
        /// </para>
        /// </summary>
        public static string TrustableModPageHost(string url, string modDbUrl)
        {
            try
            {
                if (string.IsNullOrEmpty(url) || !url.StartsWith("https://", StringComparison.Ordinal)) return null;

                string root = HttpsModDbRoot(modDbUrl);
                if (root == null || !url.StartsWith(root, StringComparison.Ordinal)) return null;
                if (!ModPagePath.IsMatch(url.Substring(root.Length))) return null;

                if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri) || !Uri.TryCreate(root, UriKind.Absolute, out Uri rootUri)) return null;
                if (!IsPlainHttpsHost(uri) || !IsPlainHttpsHost(rootUri)) return null;
                if (!string.Equals(uri.AbsoluteUri, url, StringComparison.Ordinal)) return null;
                if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) return null;
                if (!string.Equals(uri.Host, rootUri.Host, StringComparison.Ordinal)) return null;

                return uri.Host;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// https, no user info, the scheme's default port, and a DNS host that is already in its
        /// lower-case ASCII form.
        /// </summary>
        private static bool IsPlainHttpsHost(Uri uri)
        {
            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)) return false;
            if (!string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort) return false;
            if (uri.HostNameType != UriHostNameType.Dns) return false;
            if (!string.Equals(uri.Host, uri.IdnHost, StringComparison.Ordinal)) return false;

            // An explicit ":443" is still IsDefaultPort and is dropped from AbsoluteUri, so the caller's
            // "canonical form == the string given" check is what rejects it in the page address.
            return IsTrustableHostName(uri.Host);
        }
    }
}
