using System;
using System.Collections.Generic;
using ProperVersion;

namespace BetterMods.Profiles
{
    /// <summary>One installed version of a mod as the dependency rules see it. Plain values only.</summary>
    public sealed class DepVersion
    {
        public DepVersion(string key, string version, bool blocked, IReadOnlyList<KeyValuePair<string, string>> dependencies)
        {
            Key = key;
            Version = version;
            Blocked = blocked;
            Dependencies = dependencies ?? Array.Empty<KeyValuePair<string, string>>();
        }

        /// <summary><c>"modid@version"</c>, the profile key.</summary>
        public string Key { get; }

        public string Version { get; }

        /// <summary>On the mod blocklist: vanilla never loads it, so it can never satisfy anything.</summary>
        public bool Blocked { get; }

        /// <summary>Declared dependencies, mod id to requested version (empty or <c>*</c> = any).</summary>
        public IReadOnlyList<KeyValuePair<string, string>> Dependencies { get; }
    }

    /// <summary>
    /// One mod id as the viewed profile has it: every installed version (newest first), whether it is
    /// switched on, and which version it uses. Plain values only, so the rules below can be exercised
    /// without the game.
    /// </summary>
    public sealed class DepMod
    {
        public DepMod(string modId, bool coreMod, bool enabled, int selectedIndex, List<DepVersion> versions)
        {
            ModId = modId;
            CoreMod = coreMod;
            Enabled = enabled;
            SelectedIndex = selectedIndex;
            Versions = versions ?? new List<DepVersion>();
        }

        public string ModId { get; }

        /// <summary><c>ModInfo.CoreMod</c> on any installed copy - game-provided, never fixed from here.</summary>
        public bool CoreMod { get; }

        public bool Enabled { get; }

        public int SelectedIndex { get; }

        /// <summary>Newest first, like <see cref="ModVersionGroup.Versions"/>.</summary>
        public List<DepVersion> Versions { get; }

        public DepVersion Selected => SelectedIndex >= 0 && SelectedIndex < Versions.Count ? Versions[SelectedIndex] : null;
    }

    /// <summary>Why one declared dependency is not met for the viewed profile.</summary>
    public enum DependencyProblem
    {
        /// <summary>No installed copy at all.</summary>
        Missing,

        /// <summary>Installed, but switched off in the profile.</summary>
        Disabled,

        /// <summary>Switched on at a version below the requirement.</summary>
        TooOld,

        /// <summary>A game-provided mod (<c>game</c>, a core mod) below the requirement. Never fixed here.</summary>
        GameTooOld,

        /// <summary>A core mod switched off in the profile. Never fixed here.</summary>
        CoreModDisabled
    }

    public sealed class DependencyIssue
    {
        public string ModId;

        /// <summary>The requested version as declared; empty means any.</summary>
        public string Required;

        /// <summary>The version the profile would provide, or null when there is none.</summary>
        public string Installed;

        public DependencyProblem Problem;

        /// <summary>Game-provided (the <c>game</c> mod, a bundled or core mod): reported, never fixed.</summary>
        public bool GameProvided => Problem == DependencyProblem.GameTooOld || Problem == DependencyProblem.CoreModDisabled;
    }

    /// <summary>A dependency that has to come from the mod DB.</summary>
    public sealed class DependencyDownload
    {
        public string ModId;

        /// <summary>The highest version any dependent asked for; empty means any.</summary>
        public string Required;

        /// <summary>Every installed key of the mod (possibly none), for the profile transaction.</summary>
        public List<string> ExistingKeys = new List<string>();
    }

    /// <summary>What it takes to satisfy one mod's dependencies, transitively, for one profile.</summary>
    public sealed class DependencyPlan
    {
        /// <summary>Mod id to the installed key the profile should switch on, in the order decided.</summary>
        public readonly List<KeyValuePair<string, string>> Enable = new List<KeyValuePair<string, string>>();

        public readonly List<DependencyDownload> Download = new List<DependencyDownload>();

        /// <summary>Mod id to an ASCII reason it cannot be fixed from installed copies or the DB.</summary>
        public readonly List<KeyValuePair<string, string>> Unfixable = new List<KeyValuePair<string, string>>();

        /// <summary>Game-provided requirements that are not met. Information only.</summary>
        public readonly List<string> Notes = new List<string>();

        public bool HasActions => Enable.Count > 0 || Download.Count > 0;
    }

    /// <summary>
    /// The game's dependency rule and a planner built on it. Pure - plain values in, plain values out,
    /// never a throw.
    /// <para>
    /// <b>The rule (verified, 1.23.0 <c>ModLoader.CheckAndSortDependencies</c> /
    /// <c>SatisfiesVersion</c>):</b> a dependency is met when a mod with that id is enabled, error-free
    /// and <c>SemVer(provided) &gt;= SemVer(requested)</c>; an empty requested or provided version, or
    /// a requested <c>*</c>, always passes. <c>SemVer.TryParse</c>'s result is used even when parsing
    /// failed (its "best guess"), and a pre-release ranks BELOW its release - so
    /// <c>1.23.0-pre.1</c> does not satisfy <c>1.23.0</c>. The check runs BEFORE 1.23's side filter
    /// (<c>LoadMods</c> disables wrong-side mods only after <c>verifyMods</c>), so a server-only
    /// dependency is still required, and still satisfies, on the client. See NOTES, "Mod dependencies".
    /// </para>
    /// </summary>
    public static class DependencyRules
    {
        /// <summary>How deep the planner follows dependencies of dependencies.</summary>
        public const int MaxDepth = 8;

        /// <summary>Ids the game itself provides; never fixed from here (the update check skips them too).</summary>
        private static readonly HashSet<string> GameProvidedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "game", "creative", "survival"
        };

        /// <summary>
        /// <c>ModLoader.SatisfiesVersion</c>, exactly: same short-circuits, same <c>SemVer</c>, same
        /// "ignore the parse result" behaviour. Where <c>SemVer</c> itself throws (a malformed
        /// pre-release such as <c>-00</c>, which would fail the game's own check with an exception) the
        /// answer is "cannot tell", reported as satisfied so nothing is offered on a guess.
        /// </summary>
        public static bool Satisfies(string requested, string provided)
        {
            if (string.IsNullOrEmpty(requested) || string.IsNullOrEmpty(provided) || requested == "*") return true;

            try
            {
                SemVer.TryParse(requested, out SemVer wanted);
                SemVer.TryParse(provided, out SemVer have);
                return have >= wanted;
            }
            catch (Exception)
            {
                return true;
            }
        }

        /// <summary>
        /// The higher of two requirements under the same <c>SemVer</c> order; an empty or <c>*</c>
        /// requirement is the lowest. Never throws.
        /// </summary>
        public static string HigherRequirement(string a, string b)
        {
            bool aAny = string.IsNullOrEmpty(a) || a == "*";
            bool bAny = string.IsNullOrEmpty(b) || b == "*";
            if (aAny) return bAny ? "" : b;
            if (bAny) return a;

            return Satisfies(b, a) ? a : b;
        }

        public static bool IsAny(string requested) => string.IsNullOrEmpty(requested) || requested == "*";

        /// <summary>
        /// The game-provided test: the bundled ids, or a mod installed as a core mod. Not installed and
        /// not bundled is an ordinary (missing) mod.
        /// </summary>
        public static bool IsGameProvided(string modId, IReadOnlyDictionary<string, DepMod> mods)
        {
            if (string.IsNullOrEmpty(modId)) return false;
            if (GameProvidedIds.Contains(modId)) return true;

            return mods != null && mods.TryGetValue(modId, out DepMod mod) && mod != null && mod.CoreMod;
        }

        /// <summary>
        /// The unmet direct dependencies of <paramref name="mod"/>'s selected version, or an empty
        /// list. Only meaningful for a mod that is ON - a switched-off mod loads nothing and needs
        /// nothing - so an off mod answers empty. <paramref name="gameVersion"/> stands in for the
        /// <c>game</c> mod when it is not in <paramref name="mods"/>.
        /// </summary>
        public static List<DependencyIssue> Issues(DepMod mod, IReadOnlyDictionary<string, DepMod> mods, string gameVersion)
        {
            List<DependencyIssue> issues = new List<DependencyIssue>();

            try
            {
                if (mod == null || !mod.Enabled || mod.Selected == null) return issues;

                foreach (KeyValuePair<string, string> dependency in mod.Selected.Dependencies)
                {
                    DependencyIssue issue = Check(dependency.Key, dependency.Value, mods, gameVersion);
                    if (issue != null) issues.Add(issue);
                }
            }
            catch (Exception)
            {
                // Total by contract: an answer we cannot compute is "nothing to report".
            }

            return issues;
        }

        /// <summary>One requirement against the profile, or null when it is met (or cannot be judged).</summary>
        private static DependencyIssue Check(string modId, string required, IReadOnlyDictionary<string, DepMod> mods, string gameVersion)
        {
            if (string.IsNullOrWhiteSpace(modId)) return null;

            DepMod target = null;
            mods?.TryGetValue(modId, out target);

            if (IsGameProvided(modId, mods))
            {
                if (target != null && !target.Enabled)
                {
                    return new DependencyIssue { ModId = modId, Required = required ?? "", Problem = DependencyProblem.CoreModDisabled };
                }

                string provided = target?.Selected?.Version;
                if (provided == null && string.Equals(modId, "game", StringComparison.OrdinalIgnoreCase)) provided = gameVersion;
                if (provided == null || Satisfies(required, provided)) return null;

                return new DependencyIssue { ModId = modId, Required = required ?? "", Installed = provided, Problem = DependencyProblem.GameTooOld };
            }

            if (target == null || target.Versions.Count == 0)
            {
                return new DependencyIssue { ModId = modId, Required = required ?? "", Problem = DependencyProblem.Missing };
            }

            if (!target.Enabled)
            {
                return new DependencyIssue { ModId = modId, Required = required ?? "", Installed = target.Selected?.Version, Problem = DependencyProblem.Disabled };
            }

            string version = target.Selected?.Version;
            if (Satisfies(required, version)) return null;

            return new DependencyIssue { ModId = modId, Required = required ?? "", Installed = version, Problem = DependencyProblem.TooOld };
        }

        // ---------------------------------------------------------------- planning

        private sealed class SimState
        {
            public bool Enabled;
            public int SelectedIndex;
        }

        /// <summary>
        /// What it takes to make every dependency of <paramref name="rootModId"/> load, transitively:
        /// <list type="number">
        /// <item>a requirement met by the profile as it stands (or as this plan leaves it) needs
        /// nothing, but that dependency's own dependencies are still followed;</item>
        /// <item>otherwise the NEWEST installed, non-blocked version that satisfies it is switched on
        /// (versions are newest first, and every requirement is a minimum, so the newest satisfying
        /// copy satisfies every other requirement any installed copy could);</item>
        /// <item>otherwise it is a download, with the highest requirement any dependent stated - its
        /// own dependencies are only known once the file is here, so the caller re-plans after
        /// installing.</item>
        /// </list>
        /// Game-provided requirements are reported in <see cref="DependencyPlan.Notes"/>, never acted on.
        /// The root mod's own version is never changed: a dependency that needs a different version of
        /// it (a cycle) is unfixable. Cycles are cut by a visited set keyed on the exact version; depth
        /// stops at <paramref name="maxDepth"/>, which is reported. Never throws.
        /// </summary>
        public static DependencyPlan Plan(string rootModId, IReadOnlyDictionary<string, DepMod> mods, string gameVersion, int maxDepth = MaxDepth)
        {
            DependencyPlan plan = new DependencyPlan();

            try
            {
                if (string.IsNullOrEmpty(rootModId) || mods == null) return plan;
                if (!mods.TryGetValue(rootModId, out DepMod root) || root == null || !root.Enabled || root.Selected == null) return plan;

                Dictionary<string, SimState> sim = new Dictionary<string, SimState>(StringComparer.OrdinalIgnoreCase);
                Dictionary<string, DependencyDownload> downloads = new Dictionary<string, DependencyDownload>(StringComparer.OrdinalIgnoreCase);
                HashSet<string> visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                HashSet<string> unfixable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                HashSet<string> notes = new HashSet<string>(StringComparer.Ordinal);
                bool depthReported = false;

                Queue<(string Owner, DepVersion Version, int Depth)> queue = new Queue<(string, DepVersion, int)>();
                visited.Add(root.Selected.Key ?? rootModId);
                queue.Enqueue((rootModId, root.Selected, 0));

                while (queue.Count > 0)
                {
                    (string owner, DepVersion ownerVersion, int depth) = queue.Dequeue();

                    foreach (KeyValuePair<string, string> dependency in ownerVersion.Dependencies)
                    {
                        string modId = dependency.Key;
                        string required = dependency.Value ?? "";
                        if (string.IsNullOrWhiteSpace(modId)) continue;

                        if (IsGameProvided(modId, mods))
                        {
                            DependencyIssue issue = Check(modId, required, mods, gameVersion);
                            if (issue != null)
                            {
                                string note = issue.Problem == DependencyProblem.CoreModDisabled
                                    ? owner + " needs " + modId + ", which is switched off"
                                    : owner + " needs " + modId + " " + required + " (this is " + (issue.Installed ?? "?") + ")";
                                if (notes.Add(note)) plan.Notes.Add(note);
                            }

                            continue;
                        }

                        if (unfixable.Contains(modId)) continue;

                        if (downloads.TryGetValue(modId, out DependencyDownload pending))
                        {
                            pending.Required = HigherRequirement(pending.Required, required);
                            continue;
                        }

                        mods.TryGetValue(modId, out DepMod target);

                        // Already met - by the profile, or by an earlier step of this plan.
                        DepVersion current = CurrentVersion(target, sim);
                        if (current != null && Satisfies(required, current.Version))
                        {
                            Follow(target, current, depth, maxDepth, visited, queue, plan, ref depthReported);
                            continue;
                        }

                        if (string.Equals(modId, rootModId, StringComparison.OrdinalIgnoreCase))
                        {
                            unfixable.Add(modId);
                            plan.Unfixable.Add(new KeyValuePair<string, string>(modId,
                                owner + " needs " + modId + " " + Display(required) + ", but this is the mod being fixed"));
                            continue;
                        }

                        int candidate = NewestSatisfying(target, required);
                        if (candidate >= 0)
                        {
                            sim[modId] = new SimState { Enabled = true, SelectedIndex = candidate };
                            DepVersion chosen = target.Versions[candidate];

                            plan.Enable.RemoveAll(e => string.Equals(e.Key, modId, StringComparison.OrdinalIgnoreCase));
                            plan.Enable.Add(new KeyValuePair<string, string>(modId, chosen.Key));

                            Follow(target, chosen, depth, maxDepth, visited, queue, plan, ref depthReported);
                            continue;
                        }

                        DependencyDownload download = new DependencyDownload { ModId = modId, Required = IsAny(required) ? "" : required };
                        if (target != null)
                        {
                            foreach (DepVersion version in target.Versions)
                            {
                                if (!string.IsNullOrEmpty(version?.Key)) download.ExistingKeys.Add(version.Key);
                            }
                        }

                        downloads[modId] = download;
                        plan.Download.Add(download);
                    }
                }
            }
            catch (Exception)
            {
                // Whatever was planned before the fault stands; the caller reports what is left.
            }

            return plan;
        }

        private static void Follow(DepMod target, DepVersion version, int depth, int maxDepth, HashSet<string> visited,
            Queue<(string, DepVersion, int)> queue, DependencyPlan plan, ref bool depthReported)
        {
            if (target == null || version == null) return;
            if (!visited.Add(version.Key ?? target.ModId)) return;

            if (depth + 1 >= maxDepth)
            {
                if (version.Dependencies.Count > 0 && !depthReported)
                {
                    depthReported = true;
                    plan.Notes.Add("dependencies deeper than " + maxDepth + " levels were not checked");
                }

                return;
            }

            queue.Enqueue((target.ModId, version, depth + 1));
        }

        /// <summary>The version the profile (as this plan has left it) would load for a mod, or null when it is off or absent.</summary>
        private static DepVersion CurrentVersion(DepMod target, Dictionary<string, SimState> sim)
        {
            if (target == null) return null;

            if (sim.TryGetValue(target.ModId, out SimState state))
            {
                return state.Enabled && state.SelectedIndex >= 0 && state.SelectedIndex < target.Versions.Count ? target.Versions[state.SelectedIndex] : null;
            }

            if (!target.Enabled) return null;

            DepVersion selected = target.Selected;
            return selected != null && !selected.Blocked ? selected : null;
        }

        private static int NewestSatisfying(DepMod target, string required)
        {
            if (target == null) return -1;

            for (int i = 0; i < target.Versions.Count; i++)
            {
                DepVersion version = target.Versions[i];
                if (version == null || version.Blocked || string.IsNullOrEmpty(version.Key)) continue;
                if (Satisfies(required, version.Version)) return i;
            }

            return -1;
        }

        /// <summary>A requirement as words: "any version" or the version itself.</summary>
        public static string Display(string required) => IsAny(required) ? "(any version)" : required;
    }
}
