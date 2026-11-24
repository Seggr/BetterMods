using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BetterMods.Profiles;
using Vintagestory.API.Util;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace BetterMods.Updates
{
    /// <summary>
    /// Where a mod's page on the mod DB is, for the card's "Open mod page" button.
    /// <para>
    /// The update check's <c>install-information</c> answer carries no page address, and the one
    /// endpoint that lists every mod at once (<c>api/mods</c>) is 4+ MB, so the address is resolved
    /// ON CLICK with one v1 <c>api/mod/&lt;modid&gt;</c> request and cached for the session. The
    /// button is only offered for mods the check already found on the DB
    /// (<see cref="ModUpdateService.IsKnownToModDb"/>), so a click normally resolves.
    /// </para>
    /// <para>
    /// Opening: 1.23's own <c>IGuiAPI.OpenLink</c> asks "Open below external link in a browser?" every
    /// time and remembers nothing, so BetterMods keeps its own, tightly scoped trust list
    /// (<c>bettermods.json</c>, <see cref="BetterModsConfig.TrustedLinkHosts"/>). The surfaces ask
    /// <see cref="Classify"/>: a <see cref="ModPageTrust.Trusted"/> page opens straight away
    /// (<see cref="OpenTrusted"/>), an <see cref="ModPageTrust.Untrusted"/> one gets BetterMods' own
    /// three-way question (<see cref="OpenOnce"/> / <see cref="TrustAndOpen"/> / cancel), and an
    /// <see cref="ModPageTrust.Untrustable"/> one goes through the game's <c>OpenLink</c> exactly as
    /// before. Every entry point re-validates the address at the moment it opens it.
    /// </para>
    /// <para>
    /// All state is main-thread only; the request runs on the pool and posts back.
    /// </para>
    /// </summary>
    public static class ModPageLinks
    {
        private static readonly Dictionary<string, string> Resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> Pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// True when the configured mod DB root is https - the only kind of root the button links
        /// under. Main thread (reads <c>ClientSettings</c>).
        /// </summary>
        public static bool Available()
        {
            try
            {
                return ModUpdateRules.HttpsModDbRoot(ClientSettings.ModDbUrl) != null;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Resolves <paramref name="modId"/>'s page and hands it to <paramref name="open"/> on the main
        /// thread - immediately queued when cached, after one request otherwise. A second click while
        /// the first is still resolving is ignored. Failures are logged and open nothing. Main thread
        /// only. Never throws.
        /// </summary>
        public static void Open(string modId, Action<string> open)
        {
            try
            {
                if (string.IsNullOrEmpty(modId) || open == null) return;

                if (Resolved.TryGetValue(modId, out string cached))
                {
                    Post(() => open(cached));
                    return;
                }

                if (!Pending.Add(modId)) return;

                string modDbUrl = ClientSettings.ModDbUrl;
                if (ModUpdateRules.HttpsModDbRoot(modDbUrl) == null)
                {
                    Pending.Remove(modId);
                    BetterModsModSystem.Logger?.Warning("[BetterMods] Not opening a mod page: the configured mod DB address is not https.");
                    return;
                }

                Task.Run(() => ModDbClient.FetchModAsync(modDbUrl, modId, CancellationToken.None))
                    .ContinueWith(task => Post(() => Finish(modId, modDbUrl, task, open)), TaskScheduler.Default);
            }
            catch (Exception e)
            {
                Pending.Remove(modId ?? "");
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not look up a mod page: " + e.Message);
            }
        }

        private static void Finish(string modId, string modDbUrl, Task<string> task, Action<string> open)
        {
            Pending.Remove(modId);

            if (task.IsFaulted || task.IsCanceled)
            {
                string reason = task.Exception?.GetBaseException() is ModDbException known ? known.Message : "unexpected error";
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not look up the mod DB page of " + modId + ": " + reason);
                return;
            }

            if (!ModUpdateRules.ParseModPage(task.Result, out int assetId, out string urlAlias))
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] The mod DB has no page for " + modId + ".");
                return;
            }

            string url = ModUpdateRules.BuildModPageUrl(modDbUrl, urlAlias, assetId);
            if (url == null)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] The mod DB page of " + modId + " has no usable address.");
                return;
            }

            Resolved[modId] = url;
            open(url);
        }

        // ---------------------------------------------------------------- link trust

        /// <summary>
        /// How a resolved page address may be opened, and - unless <see cref="ModPageTrust.Untrustable"/> -
        /// the exact host the trust list is keyed on. Main thread (reads <c>ClientSettings</c>). Never
        /// throws.
        /// <para>
        /// Untrustable unless the address is one THIS class resolved this session (so no other link can
        /// borrow the trust) and <see cref="ModUpdateRules.TrustableModPageHost"/> accepts it against
        /// the CURRENT mod DB root.
        /// </para>
        /// </summary>
        public static ModPageTrust Classify(string url, out string host)
        {
            host = null;

            try
            {
                host = ValidatedHost(url);
                if (host == null) return ModPageTrust.Untrustable;

                return ProfileStore.IsTrustedLinkHost(host) ? ModPageTrust.Trusted : ModPageTrust.Untrusted;
            }
            catch (Exception e)
            {
                host = null;
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not check a mod page address: " + e.Message);
                return ModPageTrust.Untrustable;
            }
        }

        /// <summary>
        /// Opens <paramref name="url"/> in the browser with no question asked - only when it still
        /// validates and its host is on the trust list. False (nothing opened) otherwise. Never throws.
        /// </summary>
        public static bool OpenTrusted(string url)
        {
            return Classify(url, out _) == ModPageTrust.Trusted && OpenValidated(url);
        }

        /// <summary>
        /// The "Open" answer to BetterMods' question: opens <paramref name="url"/> once, remembering
        /// nothing - only when it still validates. False (nothing opened) otherwise. Never throws.
        /// </summary>
        public static bool OpenOnce(string url)
        {
            return Classify(url, out _) != ModPageTrust.Untrustable && OpenValidated(url);
        }

        /// <summary>
        /// The "Always trust &lt;host&gt;" answer: stores <paramref name="host"/> and opens - only when
        /// <paramref name="url"/> still validates AND its host is exactly the one the player was shown.
        /// False (nothing stored, nothing opened) otherwise. Never throws.
        /// </summary>
        public static bool TrustAndOpen(string url, string host)
        {
            try
            {
                if (Classify(url, out string current) == ModPageTrust.Untrustable) return false;
                if (!string.Equals(current, host, StringComparison.Ordinal)) return false;
                if (!ProfileStore.TrustLinkHost(current)) return false;

                BetterModsModSystem.Logger?.Notification("[BetterMods] Mod pages on " + current + " now open without asking (remove it from TrustedLinkHosts in bettermods.json to undo).");
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not remember a trusted site: " + e.Message);
                return false;
            }

            return OpenValidated(url);
        }

        /// <summary>The trust host of a page address this class resolved, or null.</summary>
        private static string ValidatedHost(string url)
        {
            if (string.IsNullOrEmpty(url) || !Resolved.ContainsValue(url)) return null;
            return ModUpdateRules.TrustableModPageHost(url, ClientSettings.ModDbUrl);
        }

        /// <summary>
        /// The browser launch vanilla's own <c>OpenLink</c> makes once the player confirms
        /// (<c>NetUtil.OpenUrlInBrowser</c>), re-validated immediately before. The canonical form is
        /// passed: no quotes, spaces or shell metacharacters survive <c>Uri</c> escaping, which matters
        /// because <c>OpenUrlInBrowser</c> quotes the address into a command line.
        /// </summary>
        private static bool OpenValidated(string url)
        {
            try
            {
                if (ValidatedHost(url) == null || !Uri.TryCreate(url, UriKind.Absolute, out Uri uri)) return false;

                NetUtil.OpenUrlInBrowser(uri.AbsoluteUri);
                return true;
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not open a mod page in the browser: " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// Next frame, main thread. The queue invokes actions bare, so a throw would take the game
        /// down - hence the wrapper.
        /// </summary>
        private static void Post(Action action)
        {
            ScreenManager.EnqueueMainThreadTask(() =>
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    BetterModsModSystem.Logger?.Error("[BetterMods] Opening a mod page failed.");
                    BetterModsModSystem.Logger?.Error(e);
                }
            });
        }
    }

    /// <summary>How a resolved mod page address may be opened (<see cref="ModPageLinks.Classify"/>).</summary>
    public enum ModPageTrust
    {
        /// <summary>Not a page address BetterMods may vouch for: the game's own confirm-every-time path.</summary>
        Untrustable,

        /// <summary>A valid page on a host the player has not trusted: BetterMods asks, with "always trust".</summary>
        Untrusted,

        /// <summary>A valid page on a trusted host: opens without a question.</summary>
        Trusted
    }
}
