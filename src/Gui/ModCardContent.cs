using System;
using System.Collections.Generic;
using System.Text;
using BetterMods.Profiles;
using BetterMods.Startup;
using BetterMods.Updates;
using Vintagestory.API.Common;
using Vintagestory.Client.NoObf;
using Vintagestory.Common;

namespace BetterMods.Gui
{
    /// <summary>What a surface lets a card do, and the dependency state of the rows it is building.</summary>
    internal sealed class ModCardActions
    {
        /// <summary>Installs a tier-1 update. Null in game, where nothing is installed.</summary>
        public Action<ModVersionGroup> Install;

        /// <summary>Installs a tier-2 update (a release for an older game version) after asking. Null in game.</summary>
        public Action<ModVersionGroup> InstallOlderRelease;

        /// <summary>Opens a mod id's DB page.</summary>
        public Action<string> OpenPage;

        /// <summary>Fixes a mod's dependencies in the viewed profile.</summary>
        public Action<ModVersionGroup> FixDependencies;

        /// <summary>Missing dependencies can be downloaded here (main menu).</summary>
        public bool CanDownload;

        /// <summary>Unmet dependencies of the rows being built; null for none known.</summary>
        public DependencyView Dependencies;

        /// <summary>
        /// BetterMods' own card's "Add Shortcut": the surface runs <see cref="Startup.StartupOffer.AddShortcut"/>
        /// and rebuilds (deferred) so the button shows its new state. Null for no button.
        /// </summary>
        public Action AddShortcut;
    }

    /// <summary>
    /// What goes ON a mod card and what goes in its hover tooltip, for both surfaces, so the main-menu
    /// screen and the in-game dialog say the same thing. Runs once per row, before the cell is
    /// composed, and reads only cached state (the session's update check, the installed mod) - never
    /// a request.
    /// <para>
    /// ASCII only throughout: the card and the tooltip both draw through Cairo's toy font API, which
    /// does no glyph fallback. Third-party text is also VTML-escaped for the tooltip.
    /// </para>
    /// </summary>
    internal static class ModCardContent
    {
        /// <summary>Longest a description may run in the tooltip, characters.</summary>
        private const int MaxDescriptionChars = 600;

        /// <summary>Most description lines the tooltip shows - its height is unbounded (NOTES).</summary>
        private const int MaxDescriptionLines = 8;

        /// <summary>Most dependencies the tooltip lists before summarising the rest.</summary>
        private const int MaxDependencies = 12;

        private const string ErrorColorHex = "#ff8080";
        private const string SuccessColorHex = "#80ff80";

        /// <summary><c>GuiStyle.WarningTextColor</c> (242, 202, 131) as VTML wants it.</summary>
        private const string WarningColorHex = "#f2ca83";

        /// <summary>
        /// Opens the header's smaller trailer - "(Code, Universal side)" - at 80% of the tooltip font,
        /// dimmed. VTML <c>&lt;font scale="N%"&gt;</c> sizes relative to the enclosing font
        /// (1.23 VtmlUtil.getFont); mixed sizes on one line share a baseline (RichTextComponentBase
        /// defaults to <c>EnumVerticalAlign.Bottom</c>, which GuiElementRichtext aligns by ascent).
        /// </summary>
        private const string SmallDimOpen = "<font scale=\"80%\" opacity=\"0.6\">";

        /// <summary>
        /// What separates the footer (dependencies, problems, update notes) from everything above it:
        /// one blank line plus a half-height one. Each text line advances by its OWN font's height
        /// (TextDrawUtil.Lineize), so a newline inside a half-size <c>&lt;font&gt;</c> is a half line -
        /// a gap visibly larger than the one blank line between authors and description. The footer's
        /// first line then starts with its own "\n", as every line does. Also the marker
        /// <see cref="AppendTooltipWarning"/> looks for to know a footer exists.
        /// </summary>
        private const string FooterGap = "\n<font scale=\"50%\">\n</font>";

        /// <summary>
        /// Dresses one row. <paramref name="group"/> may be null for a row BetterMods could not group
        /// (no usable mod id). <paramref name="actions"/> says what this surface can do; in game there
        /// is no install, and dependency fixes only switch on installed versions.
        /// <para>
        /// With the card unavailable on this game build the row stays vanilla - full detail text, no
        /// buttons - and the update and dependency state is appended to that text as plain lines
        /// instead, which is all a vanilla row can show.
        /// </para>
        /// </summary>
        public static void Apply(ModCellEntry cell, ModVersionGroup group, ModCardActions actions)
        {
            if (cell == null) return;
            actions ??= new ModCardActions();

            try
            {
                UpdateOffer offer = group != null ? ModUpdateService.OfferFor(group) : null;
                bool canInstall = actions.Install != null;
                List<DependencyIssue> issues = group != null && actions.Dependencies != null ? actions.Dependencies.IssuesFor(group) : new List<DependencyIssue>();

                if (!ModCellLayout.CardLayoutAvailable)
                {
                    AppendVanillaNotes(cell, offer, canInstall, issues);
                    return;
                }

                ModContainer mod = cell.Mod;
                string modId = InstalledModScanner.ModIdOf(mod);
                string status = IsFailed(mod) ? cell.DetailText : "";

                cell.HoverText = BuildTooltip(mod, cell.Title, status, offer, canInstall, issues, actions.CanDownload, modId);
                cell.DetailText = status;

                bool ownCard = actions.AddShortcut != null && StartupOffer.IsOwnMod(mod);
                if (ownCard)
                {
                    // Once per rebuild: this row is dressed once per compose, never per frame.
                    StartupOffer.Refresh();
                    cell.HoverText += "\n\n" + StartupOffer.TooltipLine();
                }

                // Every row's version, not only a picker's: the title is elided against wherever the
                // version slot starts, so an exotic version string would otherwise squeeze it away.
                // The full string is in the tooltip header and, on a picker row, in the picker.
                if (!string.IsNullOrEmpty(cell.RightTopText)) cell.RightTopText = ModCellLayout.ElideVersionLabel(cell.RightTopText, cell.DetailTextFont);

                string target = offer?.TargetVersion != null ? ModUpdateRules.AsciiLine(offer.TargetVersion, 40) : null;

                // Any offer, either tier; the tooltip tells them apart.
                ModCard.SetUpdateMarker(cell, target != null && !offer.Installing);

                // Top line, right after the version: the installed release was retracted. The reason
                // is in the tooltip.
                ModCard.SetVersionNote(cell, offer != null && offer.Retracted ? "(Retracted)" : null);

                // The button row is right-aligned in the order added below - Fix dependencies, Open mod
                // page, the update action - so the update keeps the corner. On a card too narrow for
                // all of them the mod page link drops first and the update action never does
                // (ModCardButtonPriority).
                bool fixable = false;
                foreach (DependencyIssue issue in issues) fixable |= !issue.GameProvided;

                // BetterMods' own card only, added first so it is the leftmost and the update action
                // (if one ever appeared - BetterMods is left out of the update check) keeps the corner.
                // Normal priority: it outlasts the mod page link, goes before the update action.
                // Disabled once our shortcut is on the desktop, and always off Windows, where the
                // tooltip carries the launch command instead.
                if (ownCard)
                {
                    Action addShortcut = actions.AddShortcut;
                    ModCard.AddButton(cell, StartupOffer.CanAdd
                        ? new ModCardButton(StartupOffer.ButtonLabel, ModCardButtonPriority.Normal, ModCardButtonStyle.Button, addShortcut)
                        : new ModCardButton(StartupOffer.ButtonLabel, ModCardButtonPriority.Normal, ModCardButtonStyle.DisabledButton));
                }

                if (fixable && actions.FixDependencies != null && group != null)
                {
                    ModCard.AddButton(cell, string.Equals(DependencyFixService.RunningModId, group.ModId, StringComparison.OrdinalIgnoreCase)
                        ? new ModCardButton("Fixing...", ModCardButtonPriority.Normal, ModCardButtonStyle.Info)
                        : new ModCardButton("Fix dependencies", ModCardButtonPriority.Normal, ModCardButtonStyle.WarningButton, () => actions.FixDependencies(group)));
                }

                // The mod's DB page. Only for mods the check found on the DB, which also rules out core
                // mods, the bundled ones and anything under the game install - the check never asks
                // about those.
                if (actions.OpenPage != null && !string.IsNullOrEmpty(modId) && ModUpdateService.IsKnownToModDb(modId) && ModPageLinks.Available())
                {
                    Action<string> openPage = actions.OpenPage;
                    ModCard.AddButton(cell, new ModCardButton("Open mod page", ModCardButtonPriority.Optional, ModCardButtonStyle.Button, () => openPage(modId)));
                }

                // Last, so rightmost: the update action, or what there is to know about updates.
                if (offer == null) return;

                if (offer.Installing)
                {
                    ModCard.AddButton(cell, new ModCardButton("Updating...", ModCardButtonPriority.Essential, ModCardButtonStyle.Info));
                }
                else if (target != null && offer.IsOlderGameVersionOffer)
                {
                    // Tier 2: a release tagged only for an older game version. Warning-coloured label,
                    // and the click asks first.
                    // Major.minor is enough when the older version is a previous minor ("1.22" on
                    // 1.23.x); within the running minor (1.23.0 on 1.23.1) only the full version
                    // tells the player it is not their own.
                    string olderShort = ModUpdateRules.MajorMinor(offer.OlderGameVersion);
                    if (olderShort == ModUpdateRules.MajorMinor(ModUpdateService.GameVersionChecked)) olderShort = offer.OlderGameVersion;
                    string label = target + " (" + ModUpdateRules.AsciiLine(olderShort, 12) + ")";
                    ModCard.AddButton(cell, canInstall && actions.InstallOlderRelease != null
                        ? new ModCardButton("Update to " + label, ModCardButtonPriority.Essential, ModCardButtonStyle.WarningButton, () => actions.InstallOlderRelease(group))
                        : new ModCardButton(label + " available", ModCardButtonPriority.Essential, ModCardButtonStyle.Warning));
                }
                else if (target != null)
                {
                    Action<ModVersionGroup> install = actions.Install;
                    ModCard.AddButton(cell, canInstall
                        ? new ModCardButton("Update to " + target, ModCardButtonPriority.Essential, ModCardButtonStyle.Button, () => install(group))
                        : new ModCardButton(target + " available", ModCardButtonPriority.Essential, ModCardButtonStyle.Info));
                }
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not dress a mod card: " + e.Message);
            }
        }

        /// <summary>
        /// Adds a warning line to a card's tooltip after it was built - for what only the cell's own
        /// constructor knows (vanilla's version-mismatch sentence). ASCII-cleaned and escaped like the
        /// rest. Never throws.
        /// </summary>
        public static void AppendTooltipWarning(ModCellEntry cell, string text)
        {
            try
            {
                string line = Plain(text, 200);
                if (cell == null || line.Length == 0) return;

                string existing = cell.HoverText ?? "";
                if (existing.Contains(line, StringComparison.Ordinal)) return;

                // A problem, so it belongs to the footer: straight under it when there is one,
                // otherwise it opens the footer with the same gap BuildTooltip uses.
                string lead = existing.Length == 0 ? "" : existing.Contains(FooterGap, StringComparison.Ordinal) ? "\n" : FooterGap + "\n";
                cell.HoverText = existing + lead + Color(WarningColorHex, line);
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not extend a mod card's tooltip: " + e.Message);
            }
        }

        /// <summary>A row the game could not load, or whose <c>modinfo.json</c> failed: its detail text is an error.</summary>
        private static bool IsFailed(ModContainer mod)
        {
            return mod == null || mod.Info == null || mod.Error.HasValue;
        }

        /// <summary>The pre-card behaviour: update state as extra lines under vanilla's detail text.</summary>
        private static void AppendVanillaNotes(ModCellEntry cell, UpdateOffer offer, bool canInstall, List<DependencyIssue> issues)
        {
            List<string> notes = new List<string>();

            // A vanilla row has no buttons, so dependency problems are information only here.
            foreach (DependencyIssue issue in issues ?? new List<DependencyIssue>())
            {
                notes.Add("Dependency " + IssueText(issue));
            }

            if (offer == null)
            {
                if (notes.Count > 0) cell.DetailText = string.IsNullOrEmpty(cell.DetailText) ? string.Join("\n", notes) : cell.DetailText + "\n" + string.Join("\n", notes);
                return;
            }

            string target = offer.TargetVersion != null ? ModUpdateRules.AsciiLine(offer.TargetVersion, 40) : null;

            if (offer.Installing) notes.Add("Updating...");
            else if (target != null && offer.IsOlderGameVersionOffer) notes.Add("Release for game " + ModUpdateRules.AsciiLine(offer.OlderGameVersion, 20) + " available: " + target + " (untested here, not part of Update all)");
            else if (target != null) notes.Add(canInstall ? "Update available: " + target + " (Update all, above)" : "Update available: " + target);

            if (offer.Retracted)
            {
                notes.Add(string.IsNullOrEmpty(offer.RetractionReason)
                    ? "The installed release was retracted on the mod DB."
                    : "The installed release was retracted on the mod DB: " + offer.RetractionReason);
            }

            if (canInstall && !offer.Installing && offer.InstallError != null && target != null)
            {
                notes.Add("Last update attempt failed: " + ModUpdateRules.AsciiLine(offer.InstallError, 120));
            }

            if (notes.Count == 0) return;

            string extra = string.Join("\n", notes);
            cell.DetailText = string.IsNullOrEmpty(cell.DetailText) ? extra : cell.DetailText + "\n" + extra;
        }

        // ---------------------------------------------------------------- tooltip

        /// <summary>
        /// The card's tooltip, in VTML, in three groups:
        /// <list type="bullet">
        /// <item>header - one line: bold name, "(modid)", "vX", then "(Type, Side side)" smaller and
        /// dimmed; it wraps only when the tooltip is too narrow;</item>
        /// <item>body - authors and contributors, a blank line, the description;</item>
        /// <item>footer - after <see cref="FooterGap"/>, every line directly together: dependencies,
        /// the load error, dependency problems and fix results, then the update and retraction
        /// notes.</item>
        /// </list>
        /// Capped in length, because a hover panel taller than the screen loses its top rows (NOTES,
        /// singleplayer hover tooltips). Its height hugs the content; its width is the host's fixed
        /// wrap width.
        /// </summary>
        internal static string BuildTooltip(ModContainer mod, string cardTitle, string status, UpdateOffer offer, bool canInstall,
            List<DependencyIssue> issues, bool canDownload, string modId)
        {
            ModInfo info = mod?.Info;
            StringBuilder text = new StringBuilder();

            string name = info?.Name;
            if (string.IsNullOrWhiteSpace(name)) name = cardTitle;
            if (string.IsNullOrWhiteSpace(name)) name = mod?.FileName;

            // Header. The separating spaces may sit at a component's edge: RichTextComponent turns
            // edge spaces into padding rather than dropping them.
            text.Append("<strong>").Append(Plain(name, 80)).Append("</strong>");
            if (!string.IsNullOrWhiteSpace(modId)) text.Append(" ").Append(Dim("(" + Plain(modId, 60) + ")"));
            if (!string.IsNullOrWhiteSpace(info?.Version)) text.Append(" ").Append(Dim("v" + Plain(info.Version, 40)));
            if (info != null) text.Append(" ").Append(SmallDimOpen).Append("(" + info.Type + ", " + info.Side + " side)").Append("</font>");

            // Body.
            if (info != null)
            {
                if (info.Authors != null && info.Authors.Count > 0)
                {
                    Line(text, Dim("Authors: ") + Plain(string.Join(", ", info.Authors), 200));
                }

                if (info.Contributors != null && info.Contributors.Count > 0)
                {
                    Line(text, Dim("Contributors: ") + Plain(string.Join(", ", info.Contributors), 200));
                }

                string description = Paragraph(info.Description);
                if (description.Length > 0)
                {
                    text.Append("\n");
                    Line(text, description);
                }
            }

            // Footer: opened by the first line that goes into it, so a mod with nothing to say there
            // ends on its description with no trailing gap.
            bool footerOpen = false;
            void Note(string line)
            {
                if (string.IsNullOrEmpty(line)) return;

                if (!footerOpen)
                {
                    text.Append(FooterGap);
                    footerOpen = true;
                }

                Line(text, line);
            }

            string dependencies = info != null ? Dependencies(info) : null;
            if (dependencies != null) Note(Dim("Dependencies: ") + dependencies);

            if (!string.IsNullOrWhiteSpace(status))
            {
                Note(Color(ErrorColorHex, Plain(status, 300)));
            }

            if (issues != null && issues.Count > 0)
            {
                bool fixable = false;
                bool needsDownload = false;
                int shown = 0;
                foreach (DependencyIssue issue in issues)
                {
                    fixable |= !issue.GameProvided;
                    needsDownload |= issue.Problem == DependencyProblem.Missing || issue.Problem == DependencyProblem.TooOld;

                    if (shown == MaxDependencies) continue;
                    shown++;
                    Note(Color(issue.GameProvided ? WarningColorHex : ErrorColorHex, "Dependency " + Escape(IssueText(issue))));
                }

                if (issues.Count > shown) Note(Dim("and " + (issues.Count - shown) + " more dependency problems"));

                if (fixable)
                {
                    Note(Dim(canDownload || !needsDownload
                        ? "Fix dependencies switches on what is installed and downloads what is not, for this profile only."
                        : "Fix dependencies switches on what is installed; anything that has to be downloaded is done from the Mods screen in the main menu."));
                }
            }

            IReadOnlyList<string> lastProblems = DependencyFixService.LastProblemsFor(modId);
            if (lastProblems.Count > 0)
            {
                Note(Color(WarningColorHex, "Last dependency fix could not do everything:"));
                int listed = 0;
                foreach (string problem in lastProblems)
                {
                    if (listed++ == 6)
                    {
                        Note(Dim("and " + (lastProblems.Count - 6) + " more (see the log)"));
                        break;
                    }

                    Note(Color(WarningColorHex, "  " + Plain(problem, 200)));
                }
            }

            if (offer != null)
            {
                string target = offer.TargetVersion != null ? Plain(offer.TargetVersion, 40) : null;

                if (offer.Installing)
                {
                    Note(Color(SuccessColorHex, "Updating..."));
                }
                else if (target != null && offer.IsOlderGameVersionOffer)
                {
                    string older = Plain(offer.OlderGameVersion, 30);
                    Note(Color(WarningColorHex, "Update available for an older game version: " + target) +
                         Dim(" - tagged for " + older + ", untested on " + Plain(ModUpdateService.GameVersionChecked, 30) + "."));
                }
                else if (target != null)
                {
                    Note(Color(SuccessColorHex, "Update available: " + target) +
                         Dim(" - tagged for " + Plain(ModUpdateService.GameVersionChecked, 30) + "."));
                }

                if (offer.Retracted)
                {
                    string reason = Plain(offer.RetractionReason, 160);
                    Note(Color(WarningColorHex, "The installed release was retracted on the mod DB" + (reason.Length > 0 ? ": " + reason : ".")));
                }

                if (canInstall && !offer.Installing && offer.InstallError != null && target != null)
                {
                    Note(Color(ErrorColorHex, "Last update attempt failed: " + Plain(offer.InstallError, 120)));
                }
            }

            return text.ToString();
        }

        /// <summary>
        /// One unmet dependency as plain ASCII (not escaped): what is needed and what is wrong. The
        /// requirement is a minimum - <c>ModLoader.SatisfiesVersion</c> is <c>provided &gt;= requested</c>.
        /// </summary>
        private static string IssueText(DependencyIssue issue)
        {
            string id = ModUpdateRules.AsciiLine(issue.ModId, 60);
            string needed = DependencyRules.IsAny(issue.Required) ? id : id + " " + ModUpdateRules.AsciiLine(issue.Required, 40) + " or newer";
            string installed = ModUpdateRules.AsciiLine(issue.Installed, 40);

            return issue.Problem switch
            {
                DependencyProblem.Missing => needed + ": not installed",
                DependencyProblem.Disabled => needed + ": switched off in this profile",
                DependencyProblem.TooOld => needed + ": " + (installed.Length > 0 ? installed : "the selected version") + " is too old",
                DependencyProblem.GameTooOld => needed + ": this game provides " + (installed.Length > 0 ? installed : "an older version") + " (cannot be fixed here)",
                DependencyProblem.CoreModDisabled => id + ": a core mod, switched off in this profile (not fixed here)",
                _ => needed
            };
        }

        /// <summary>
        /// "modid version" per dependency, ", "-separated; a missing or <c>*</c> version is "any".
        /// Null when there are none.
        /// </summary>
        private static string Dependencies(ModInfo info)
        {
            IReadOnlyList<ModDependency> dependencies = info.Dependencies;
            if (dependencies == null || dependencies.Count == 0) return null;

            List<string> parts = new List<string>();
            foreach (ModDependency dependency in dependencies)
            {
                if (dependency == null || string.IsNullOrWhiteSpace(dependency.ModID)) continue;
                if (parts.Count == MaxDependencies)
                {
                    parts.Add("and " + (dependencies.Count - MaxDependencies) + " more");
                    break;
                }

                string version = string.IsNullOrWhiteSpace(dependency.Version) || dependency.Version.Trim() == "*"
                    ? "any"
                    : dependency.Version.Trim();

                parts.Add(Plain(dependency.ModID, 60) + " " + Plain(version, 40));
            }

            return parts.Count == 0 ? null : string.Join(", ", parts);
        }

        /// <summary>
        /// A free-text description as VTML: line breaks kept (blank runs collapsed), each line made
        /// <see cref="Plain"/>, the whole capped by characters and by lines.
        /// </summary>
        private static string Paragraph(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";

            List<string> lines = new List<string>();
            int used = 0;
            bool truncated = false;

            foreach (string raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                string line = ModUpdateRules.AsciiLine(raw, int.MaxValue);
                if (line.Length == 0) continue;

                if (lines.Count == MaxDescriptionLines || used >= MaxDescriptionChars)
                {
                    truncated = true;
                    break;
                }

                int remaining = MaxDescriptionChars - used;
                if (line.Length > remaining)
                {
                    truncated = true;
                    if (remaining < 10) break;

                    // AsciiLine supplies the ellipsis.
                    line = ModUpdateRules.AsciiLine(line, remaining);
                }

                used += line.Length;
                lines.Add(Escape(line));
                if (truncated) break;
            }

            string result = string.Join("\n", lines);
            if (truncated && !result.EndsWith("...", StringComparison.Ordinal)) result += " ...";
            return result;
        }

        private static void Line(StringBuilder text, string line)
        {
            if (string.IsNullOrEmpty(line)) return;
            text.Append("\n").Append(line);
        }

        private static string Dim(string vtml) => "<font opacity=\"0.6\">" + vtml + "</font>";

        private static string Color(string hex, string vtml) => "<font color=\"" + hex + "\">" + vtml + "</font>";

        /// <summary>
        /// Third-party text made safe for one tooltip line: ASCII-only and whitespace-collapsed via
        /// <see cref="ModUpdateRules.AsciiLine"/>, then VTML-escaped - the parser decodes
        /// <c>&amp;lt;</c>/<c>&amp;gt;</c> in text tokens, so a mod name containing '&lt;' shows as
        /// written instead of opening a tag.
        /// </summary>
        private static string Plain(string text, int maxLength)
        {
            return Escape(ModUpdateRules.AsciiLine(text, maxLength));
        }

        private static string Escape(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return text.Replace("<", "&lt;").Replace(">", "&gt;");
        }
    }
}
