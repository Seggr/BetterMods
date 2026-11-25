using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace BetterMods.Updates
{
    /// <summary>One release from the mod DB's per-mod release list (v1 <c>api/mod/&lt;modid&gt;</c>).</summary>
    public sealed class ModRelease
    {
        /// <summary><c>modversion</c>, trimmed. Never empty for a parsed release.</summary>
        public string Version;

        /// <summary>The game versions the author tagged it for (<c>tags</c>), as the DB spells them.</summary>
        public List<string> Tags = new List<string>();

        /// <summary><c>filename</c>, informational only - the installer re-asks <c>install-information</c> for the file.</summary>
        public string FileName;

        /// <summary><c>fileid</c>, 0 when absent.</summary>
        public int FileId;
    }

    /// <summary>Where a mod's DB release list stands this session (<see cref="ModReleaseCatalog"/>).</summary>
    public enum ReleaseListState
    {
        /// <summary>Not asked for: the mod is not one the update check found on the DB, or nobody has opened its picker yet.</summary>
        NotRequested,

        Loading,

        Loaded,

        /// <summary>The DB answered <c>{"statuscode":"404"}</c> for the id.</summary>
        NotOnDb,

        /// <summary>The request failed (offline, timeout, unreadable answer). Retried on a later open.</summary>
        Failed
    }

    /// <summary>What one row of the version picker is.</summary>
    public enum VersionRowKind
    {
        /// <summary>An installed version: clicking selects it for the viewed profile.</summary>
        Installed,

        /// <summary>A DB release that is not installed: clicking downloads and installs it (main menu only).</summary>
        Available,

        /// <summary>Muted "Loading versions..." while the release list is on its way.</summary>
        Loading,

        /// <summary>Muted "Not on the mod DB".</summary>
        NotOnDb,

        /// <summary>Muted "Mod DB versions unavailable" - the request failed.</summary>
        Unavailable,

        /// <summary>"Show all N versions" - expands a capped list in place.</summary>
        ShowAll
    }

    /// <summary>One row of the version picker, as <see cref="ModReleaseRules.BuildRows"/> decides it.</summary>
    public sealed class VersionRow
    {
        public VersionRowKind Kind;

        /// <summary>The version (Installed / Available), else null.</summary>
        public string Version;

        /// <summary>Index into the group's installed versions (Installed only), else -1.</summary>
        public int InstalledIndex = -1;

        /// <summary>The version the viewed profile uses.</summary>
        public bool Selected;

        /// <summary>
        /// Available only: the release is tagged for the running game version. False means the click
        /// asks first (the tier-2 "older game version" confirmation).
        /// </summary>
        public bool Compatible = true;

        /// <summary>
        /// Available and not <see cref="Compatible"/>: the game version it IS tagged for, newest tag
        /// first - the full version (e.g. <c>1.22.7</c>), for the confirmation. Null when it carries no tag at all.
        /// </summary>
        public string TaggedFor;

        /// <summary>Available and not <see cref="Compatible"/>: the short hint the row carries, e.g. <c>1.22</c> or <c>no game version</c>.</summary>
        public string GameVersionHint;

        /// <summary>The DB reported this (installed) release as retracted.</summary>
        public bool Retracted;

        /// <summary>The release is on the game's mod blocklist: shown, never installed.</summary>
        public bool Blocked;

        /// <summary>ShowAll only: how many rows the full list has.</summary>
        public int TotalCount;

        /// <summary>True for a row a click does something with.</summary>
        public bool Clickable => Kind == VersionRowKind.Installed || Kind == VersionRowKind.ShowAll || (Kind == VersionRowKind.Available && !Blocked);
    }

    /// <summary>
    /// The pure half of the version picker's mod DB rows: reading the per-mod release list, merging
    /// it with the installed versions, ordering, game-version classification and the visible-list
    /// cap. No game types, no I/O.
    /// </summary>
    public static class ModReleaseRules
    {
        /// <summary>
        /// How many not-installed releases a collapsed picker shows (newest first) before the
        /// "Show all" row. Installed versions are always shown and do not count.
        /// </summary>
        public const int CollapsedAvailableCount = 10;

        /// <summary>
        /// Reads the v1 <c>api/mod/&lt;modid&gt;</c> answer (verified live 2026-11-24:
        /// <c>{"statuscode":"200","mod":{...,"releases":[{"releaseid":55350,"mainfile":"https://moddbcdn...",
        /// "filename":"configlib_1.13.2.zip","fileid":119253,"downloads":64378,"tags":["1.22.0",...],
        /// "modidstr":"configlib","modversion":"1.13.2","created":"2026-09-02 09:10:12","changelog":"..."}]}}</c>,
        /// releases in upload order, not version order; retracted releases are NOT listed). An unknown
        /// id answers HTTP 200 <c>{"statuscode":"404"}</c> - <paramref name="notOnDb"/>. Returns null
        /// (and <paramref name="notOnDb"/> false) for a body that is not that shape. A release with no
        /// usable <c>modversion</c> is skipped; a version listed twice is kept once (first wins).
        /// </summary>
        public static List<ModRelease> ParseReleases(string json, out bool notOnDb)
        {
            notOnDb = false;
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

            string status = ModUpdateRules.ReadString(ModUpdateRules.GetIgnoreCase(root, "statuscode"));
            if (status == "404")
            {
                notOnDb = true;
                return null;
            }

            if (status != null && status != "200") return null;
            if (!(ModUpdateRules.GetIgnoreCase(root, "mod") is JObject mod)) return null;

            List<ModRelease> releases = new List<ModRelease>();
            if (!(ModUpdateRules.GetIgnoreCase(mod, "releases") is JArray array)) return releases;

            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JToken token in array)
            {
                if (!(token is JObject item)) continue;

                string version = ModUpdateRules.ReadString(ModUpdateRules.GetIgnoreCase(item, "modversion"))?.Trim();
                if (string.IsNullOrEmpty(version) || version.Length > 64 || !seen.Add(version)) continue;

                ModRelease release = new ModRelease
                {
                    Version = version,
                    FileName = ModUpdateRules.ReadString(ModUpdateRules.GetIgnoreCase(item, "filename")),
                    FileId = ModUpdateRules.ReadInt(ModUpdateRules.GetIgnoreCase(item, "fileid"))
                };

                if (ModUpdateRules.GetIgnoreCase(item, "tags") is JArray tags)
                {
                    foreach (JToken tag in tags)
                    {
                        string name = ModUpdateRules.ReadString(tag)?.Trim();
                        if (!string.IsNullOrEmpty(name)) release.Tags.Add(name);
                    }
                }

                releases.Add(release);
            }

            return releases;
        }

        /// <summary>True when <paramref name="release"/> is tagged for exactly <paramref name="gameVersion"/> - what the DB's own <c>gv</c> filter means.</summary>
        public static bool IsTaggedFor(ModRelease release, string gameVersion)
        {
            if (release?.Tags == null || string.IsNullOrWhiteSpace(gameVersion)) return false;

            string wanted = gameVersion.Trim();
            foreach (string tag in release.Tags)
            {
                if (string.Equals(tag, wanted, StringComparison.OrdinalIgnoreCase)) return true;
            }

            return false;
        }

        /// <summary>The newest of a release's tags (by <see cref="ModUpdateRules.CompareVersions"/>), or the last listed one when none compare; null when it has none.</summary>
        public static string NewestTag(ModRelease release)
        {
            if (release?.Tags == null || release.Tags.Count == 0) return null;

            return ModUpdateRules.NewestComparable(release.Tags) ?? release.Tags[release.Tags.Count - 1];
        }

        /// <summary>
        /// The rows of a version picker, top to bottom.
        /// <para>
        /// <paramref name="installedVersions"/> is the group's installed list in its own order (newest
        /// first, by the game's comparer) and is kept exactly as is. The DB releases that are not
        /// installed are sorted newest first by <see cref="ModUpdateRules.CompareVersions"/> (versions
        /// it cannot read go last, in DB order) and merged in: before each installed version come the
        /// releases strictly newer than it. So the two kinds interleave by version, and an installed
        /// version the comparer cannot read stays where the group put it.
        /// </para>
        /// <para>
        /// Capping: unless <paramref name="showAll"/>, only the newest
        /// <see cref="CollapsedAvailableCount"/> not-installed releases are kept (installed versions
        /// always are), followed by a <see cref="VersionRowKind.ShowAll"/> row - and only when that
        /// actually hides something.
        /// </para>
        /// <para>
        /// Status rows go last: Loading while the list is on its way, NotOnDb / Unavailable for a 404
        /// or a failed request. <see cref="ReleaseListState.NotRequested"/> adds nothing (the mod is
        /// not on the DB as far as this session knows, so the picker is installed versions only).
        /// </para>
        /// </summary>
        public static List<VersionRow> BuildRows(
            IReadOnlyList<string> installedVersions,
            int selectedIndex,
            ReleaseListState state,
            IReadOnlyList<ModRelease> releases,
            string runningGameVersion,
            bool showAll,
            string retractedInstalledVersion = null,
            Func<string, bool> isBlocked = null)
        {
            List<VersionRow> rows = new List<VersionRow>();
            installedVersions ??= Array.Empty<string>();

            HashSet<string> installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string version in installedVersions)
            {
                if (!string.IsNullOrWhiteSpace(version)) installed.Add(version.Trim());
            }

            List<ModRelease> available = new List<ModRelease>();
            if (state == ReleaseListState.Loaded && releases != null)
            {
                foreach (ModRelease release in releases)
                {
                    if (release == null || string.IsNullOrWhiteSpace(release.Version)) continue;
                    if (installed.Contains(release.Version.Trim())) continue;
                    available.Add(release);
                }

                available = SortNewestFirst(available);
            }

            // The cap: the newest N not-installed releases survive, in sorted order.
            int availableTotal = available.Count;
            bool capped = !showAll && availableTotal > CollapsedAvailableCount;
            List<ModRelease> shown = capped ? available.GetRange(0, CollapsedAvailableCount) : available;

            int next = 0;
            for (int i = 0; i < installedVersions.Count; i++)
            {
                string version = installedVersions[i];

                while (next < shown.Count && ModUpdateRules.CompareVersions(shown[next].Version, version) > 0)
                {
                    rows.Add(AvailableRow(shown[next++], runningGameVersion, isBlocked));
                }

                rows.Add(new VersionRow
                {
                    Kind = VersionRowKind.Installed,
                    Version = string.IsNullOrWhiteSpace(version) ? "--" : version,
                    InstalledIndex = i,
                    Selected = i == selectedIndex,
                    Retracted = !string.IsNullOrWhiteSpace(retractedInstalledVersion) && !string.IsNullOrWhiteSpace(version)
                                && string.Equals(version.Trim(), retractedInstalledVersion.Trim(), StringComparison.OrdinalIgnoreCase)
                });
            }

            while (next < shown.Count) rows.Add(AvailableRow(shown[next++], runningGameVersion, isBlocked));

            if (capped)
            {
                rows.Add(new VersionRow { Kind = VersionRowKind.ShowAll, TotalCount = installedVersions.Count + availableTotal });
            }

            switch (state)
            {
                case ReleaseListState.Loading:
                    rows.Add(new VersionRow { Kind = VersionRowKind.Loading });
                    break;
                case ReleaseListState.NotOnDb:
                    rows.Add(new VersionRow { Kind = VersionRowKind.NotOnDb });
                    break;
                case ReleaseListState.Failed:
                    rows.Add(new VersionRow { Kind = VersionRowKind.Unavailable });
                    break;
            }

            return rows;
        }

        /// <summary>
        /// True when a release list offers a version that is not installed - whether a single-version
        /// row still has something to pick. Null releases (not loaded) answer true: the row offers the
        /// picker until the list says otherwise.
        /// </summary>
        public static bool OffersOtherVersions(IReadOnlyList<string> installedVersions, IReadOnlyList<ModRelease> releases)
        {
            if (releases == null) return true;

            HashSet<string> installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (installedVersions != null)
            {
                foreach (string version in installedVersions)
                {
                    if (!string.IsNullOrWhiteSpace(version)) installed.Add(version.Trim());
                }
            }

            foreach (ModRelease release in releases)
            {
                if (release != null && !string.IsNullOrWhiteSpace(release.Version) && !installed.Contains(release.Version.Trim())) return true;
            }

            return false;
        }

        private static VersionRow AvailableRow(ModRelease release, string runningGameVersion, Func<string, bool> isBlocked)
        {
            VersionRow row = new VersionRow
            {
                Kind = VersionRowKind.Available,
                Version = release.Version.Trim(),
                Compatible = IsTaggedFor(release, runningGameVersion)
            };

            if (!row.Compatible)
            {
                row.TaggedFor = NewestTag(release);
                row.GameVersionHint = row.TaggedFor == null
                    ? "no game version"
                    : ModUpdateRules.AsciiLine(ModUpdateRules.GameVersionLabel(row.TaggedFor, runningGameVersion), 20);
            }

            try
            {
                row.Blocked = isBlocked != null && isBlocked(row.Version);
            }
            catch (Exception)
            {
                row.Blocked = false;
            }

            return row;
        }

        /// <summary>
        /// Newest first by <see cref="ModUpdateRules.CompareVersions"/>; versions it cannot read go
        /// last in their original order. An index tiebreak keeps the sort total and stable, so a
        /// comparer that answers "equal" for two spellings of one version cannot upset it.
        /// </summary>
        private static List<ModRelease> SortNewestFirst(List<ModRelease> releases)
        {
            List<int> comparable = new List<int>();
            List<ModRelease> rest = new List<ModRelease>();

            for (int i = 0; i < releases.Count; i++)
            {
                if (ModUpdateRules.CompareVersions(releases[i].Version, releases[i].Version) != null) comparable.Add(i);
                else rest.Add(releases[i]);
            }

            comparable.Sort((a, b) =>
            {
                int order = ModUpdateRules.CompareVersions(releases[b].Version, releases[a].Version) ?? 0;
                return order != 0 ? order : a.CompareTo(b);
            });

            List<ModRelease> sorted = new List<ModRelease>(releases.Count);
            foreach (int index in comparable) sorted.Add(releases[index]);
            sorted.AddRange(rest);
            return sorted;
        }
    }
}
