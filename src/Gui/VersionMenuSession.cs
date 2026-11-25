using System;
using System.Collections.Generic;
using BetterMods.Profiles;
using BetterMods.Updates;

namespace BetterMods.Gui
{
    /// <summary>
    /// One opening of a mod card's version picker: the rows it shows (installed versions plus every
    /// release the mod DB lists, merged by <see cref="ModReleaseRules.BuildRows"/>) and what each row
    /// means when clicked. Shared by the main-menu screen and the in-game dialog; each host decides
    /// what a click DOES (<see cref="RowAt"/>).
    /// <para>
    /// Opening one asks <see cref="ModReleaseCatalog"/> for the mod's release list (lazily, once per
    /// session); until it lands the picker shows the installed versions and a "Loading versions..."
    /// line, and the popup redraws itself when it arrives (<see cref="Stamp"/>).
    /// </para>
    /// <para>Main thread only.</para>
    /// </summary>
    internal sealed class VersionMenuSession
    {
        private readonly ModVersionGroup group;
        private readonly bool canInstall;
        private bool showAll;

        /// <summary>In game: the not-installed version last clicked, which now says where to install it.</summary>
        private string mainMenuOnlyNote;

        private List<VersionRow> rows = new List<VersionRow>();

        public VersionMenuSession(ModVersionGroup group, bool canInstall)
        {
            this.group = group;
            this.canInstall = canInstall;
        }

        /// <summary>
        /// True when a card gets the version trigger at all: more than one version installed, or a mod
        /// the update check found on the DB whose release list (once loaded) has a version that is not
        /// installed. Before the list is loaded such a mod gets the trigger on trust; one that turns
        /// out to have nothing else loses it at the next rebuild.
        /// </summary>
        public static bool HasMenu(ModVersionGroup group)
        {
            if (group == null) return false;
            if (group.HasVersionChoice) return true;

            try
            {
                if (!ModReleaseCatalog.IsEligible(group.ModId)) return false;
                if (ModReleaseCatalog.StateOf(group.ModId) == ReleaseListState.NotOnDb) return false;

                return ModReleaseRules.OffersOtherVersions(group.VersionNames(), ModReleaseCatalog.ReleasesOf(group.ModId));
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>What the open picker polls: a change means the content is worth re-reading.</summary>
        public static int Stamp() => ModReleaseCatalog.Stamp;

        /// <summary>Starts the release list request (a no-op when cached, in flight, or the mod is not on the DB).</summary>
        public void Begin()
        {
            ModReleaseCatalog.Request(group?.ModId);
        }

        /// <summary>The row behind picker index <paramref name="index"/>, as of the last <see cref="Build"/>.</summary>
        public VersionRow RowAt(int index)
        {
            return index >= 0 && index < rows.Count ? rows[index] : null;
        }

        /// <summary>"Show all N versions": drop the cap. The caller keeps the popup open.</summary>
        public void ExpandAll()
        {
            showAll = true;
        }

        /// <summary>In game: a not-installed row was clicked. It now says "install from the main menu".</summary>
        public void NoteMainMenuOnly(string version)
        {
            mainMenuOnlyNote = version;
        }

        /// <summary>The picker's content now. Never throws: on failure, the installed versions alone.</summary>
        public VersionMenuContent Build()
        {
            try
            {
                string modId = group.ModId;
                string[] installed = group.VersionNames();

                ReleaseListState state = ModReleaseCatalog.IsEligible(modId) ? ModReleaseCatalog.StateOf(modId) : ReleaseListState.NotRequested;

                UpdateOffer offer = ModUpdateService.OfferFor(group);
                string retracted = offer?.Retracted == true ? ModUpdateRules.NewestComparable(installed) : null;

                rows = ModReleaseRules.BuildRows(installed, group.SelectedIndex, state, ModReleaseCatalog.ReleasesOf(modId),
                    ModUpdateService.GameVersionChecked, showAll, retracted, version => ModUpdateService.IsReleaseBlocked(modId, version));
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not list the mod DB versions in the version picker: " + e.Message);
                rows = InstalledOnly();
            }

            return ToContent();
        }

        private List<VersionRow> InstalledOnly()
        {
            List<VersionRow> fallback = new List<VersionRow>();
            string[] names = group.VersionNames();
            for (int i = 0; i < names.Length; i++)
            {
                fallback.Add(new VersionRow { Kind = VersionRowKind.Installed, Version = names[i], InstalledIndex = i, Selected = i == group.SelectedIndex });
            }

            return fallback;
        }

        private VersionMenuContent ToContent()
        {
            string installing = ModUpdateService.InstallingVersion(group.ModId);

            VersionMenuContent content = new VersionMenuContent { Entries = new VersionMenuEntry[rows.Count] };
            for (int i = 0; i < rows.Count; i++)
            {
                VersionRow row = rows[i];
                if (row.Selected) content.SelectedIndex = i;

                content.Entries[i] = row.Kind switch
                {
                    VersionRowKind.Installed => new VersionMenuEntry
                    {
                        Text = row.Version,
                        Hint = row.Retracted ? "(Retracted)" : null,
                        HintIsWarning = true
                    },
                    VersionRowKind.Available => AvailableEntry(row, installing),
                    VersionRowKind.ShowAll => new VersionMenuEntry { Text = "Show all " + row.TotalCount + " versions", Muted = true },
                    VersionRowKind.Loading => Status("Loading versions..."),
                    VersionRowKind.NotOnDb => Status("Not on the mod DB"),
                    _ => Status("Mod DB versions unavailable")
                };
            }

            return content;
        }

        private VersionMenuEntry AvailableEntry(VersionRow row, string installing)
        {
            VersionMenuEntry entry = new VersionMenuEntry { Text = row.Version, Muted = true, Download = true };

            if (installing != null && string.Equals(installing, row.Version, StringComparison.OrdinalIgnoreCase))
            {
                entry.Hint = "(installing...)";
                entry.Clickable = false;
            }
            else if (row.Blocked)
            {
                entry.Hint = "(blocked)";
                entry.HintIsWarning = true;
                entry.Download = false;
                entry.Clickable = false;
            }
            else if (!canInstall && string.Equals(mainMenuOnlyNote, row.Version, StringComparison.OrdinalIgnoreCase))
            {
                entry.Hint = "install from the main menu";
            }
            else if (!row.Compatible)
            {
                entry.Hint = "(" + row.GameVersionHint + ")";
                entry.HintIsWarning = true;
            }

            return entry;
        }

        private static VersionMenuEntry Status(string text) => new VersionMenuEntry { Text = text, Muted = true, Clickable = false };
    }
}
