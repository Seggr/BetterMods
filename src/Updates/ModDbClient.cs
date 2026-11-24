using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace BetterMods.Updates
{
    /// <summary>A mod DB request that failed in a way worth telling the player about, ASCII only.</summary>
    public sealed class ModDbException : Exception
    {
        public ModDbException(string message)
            : base(message)
        {
        }
    }

    /// <summary>
    /// The network half of the update feature: the <c>install-information</c> query and the file
    /// download. Background threads only; nothing here touches the game.
    /// <para>
    /// Deliberately not <c>ModDbUtil</c>/<c>VSWebClient</c>: <c>FetchModInstallInformation</c> throws
    /// the whole answer away on an HTTP 400 and cannot be chunked, <c>VSWebClient.DownloadAsync</c>
    /// never looks at the HTTP status (an error page is written into the <c>.zip</c>) and reports
    /// timeouts as plain errors, and <c>DownloadModAsync</c> writes the server's file name unsanitised
    /// and leaves a truncated file behind on a dropped connection. See NOTES, "Mod DB".
    /// </para>
    /// </summary>
    internal static class ModDbClient
    {
        /// <summary>Per query. The answer is a few kilobytes.</summary>
        private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(20);

        /// <summary>Per file, start to finish. Large content mods run to a few hundred megabytes.</summary>
        private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(15);

        /// <summary>Hard cap on a single download, whatever the server announces.</summary>
        private const long MaxDownloadBytes = 1024L * 1024L * 1024L;

        private static readonly Lazy<HttpClient> Http = new Lazy<HttpClient>(CreateClient, LazyThreadSafetyMode.ExecutionAndPublication);

        private static HttpClient CreateClient()
        {
            // SocketsHttpHandler never follows an https -> http redirect, so the transport cannot be
            // downgraded behind our back; the final address is still checked after every download.
            SocketsHttpHandler handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 5,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5)
            };

            HttpClient client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("BetterMods-VintageStory-ModManager");
            return client;
        }

        /// <summary>
        /// One <c>install-information</c> request. Throws <see cref="ModDbException"/> for anything but
        /// a readable 2xx answer, and <see cref="OperationCanceledException"/> only when
        /// <paramref name="cancel"/> fired.
        /// </summary>
        public static async Task<Dictionary<string, ModDbEntry>> QueryAsync(
            string modDbUrl, IReadOnlyList<string> specs, string gameVersion, CancellationToken cancel)
        {
            Uri uri = ApiUri(modDbUrl, "v2/mods/install-information?" + ModUpdateRules.BuildQuery(specs, gameVersion));

            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(QueryTimeout);

            try
            {
                using HttpResponseMessage response = await Http.Value.GetAsync(uri, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    throw new ModDbException("the mod DB answered HTTP " + (int)response.StatusCode);
                }

                string body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                return ModUpdateRules.ParseInstallInformation(body) ?? throw new ModDbException("the mod DB answer could not be read");
            }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
            {
                throw new ModDbException("the mod DB did not answer in time");
            }
            catch (HttpRequestException e)
            {
                throw new ModDbException("the mod DB could not be reached (" + ModUpdateRules.AsciiLine(e.Message, 80) + ")");
            }
        }

        /// <summary>
        /// One v1 <c>api/mod/&lt;modid&gt;</c> request, for the mod page button. Returns the body, or null
        /// when the DB answered 404 for the id. Throws <see cref="ModDbException"/> for anything else
        /// that is not a readable 2xx answer.
        /// </summary>
        public static async Task<string> FetchModAsync(string modDbUrl, string modId, CancellationToken cancel)
        {
            Uri uri = ApiUri(modDbUrl, "mod/" + Uri.EscapeDataString(modId ?? ""));

            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(QueryTimeout);

            try
            {
                using HttpResponseMessage response = await Http.Value.GetAsync(uri, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.NotFound) return null;
                if (!response.IsSuccessStatusCode)
                {
                    throw new ModDbException("the mod DB answered HTTP " + (int)response.StatusCode);
                }

                return await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
            {
                throw new ModDbException("the mod DB did not answer in time");
            }
            catch (HttpRequestException e)
            {
                throw new ModDbException("the mod DB could not be reached (" + ModUpdateRules.AsciiLine(e.Message, 80) + ")");
            }
        }

        /// <summary>
        /// One <c>api/gameversions</c> request, for the tier-2 update check. Returns the body. Throws
        /// <see cref="ModDbException"/> for anything that is not a readable 2xx answer.
        /// </summary>
        public static async Task<string> FetchGameVersionsAsync(string modDbUrl, CancellationToken cancel)
        {
            Uri uri = ApiUri(modDbUrl, "gameversions");

            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(QueryTimeout);

            try
            {
                using HttpResponseMessage response = await Http.Value.GetAsync(uri, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    throw new ModDbException("the mod DB answered HTTP " + (int)response.StatusCode);
                }

                return await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
            {
                throw new ModDbException("the mod DB did not answer in time");
            }
            catch (HttpRequestException e)
            {
                throw new ModDbException("the mod DB could not be reached (" + ModUpdateRules.AsciiLine(e.Message, 80) + ")");
            }
        }

        /// <summary>
        /// Streams <paramref name="uri"/> into <paramref name="destination"/>, which must not exist yet.
        /// Rejects a non-2xx status, a final (post-redirect) address that is not an allowed https
        /// host, an announced or actual size over the cap, and a body shorter than its announced
        /// length. The caller owns deleting the file on failure.
        /// </summary>
        public static async Task DownloadAsync(Uri uri, string destination, CancellationToken cancel)
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(DownloadTimeout);

            try
            {
                using HttpResponseMessage response = await Http.Value.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    throw new ModDbException("the download answered HTTP " + (int)response.StatusCode);
                }

                // The /download/... address redirects to the CDN; verified live, but it is the FINAL
                // address that decides who we actually took bytes from.
                if (!ModUpdateRules.IsAllowedDownloadUri(response.RequestMessage?.RequestUri))
                {
                    throw new ModDbException("the download was redirected away from the official mod DB");
                }

                long? announced = response.Content.Headers.ContentLength;
                if (announced > MaxDownloadBytes) throw new ModDbException("the download is unreasonably large");

                long written = 0;
                using (Stream source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
                using (FileStream target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    byte[] buffer = new byte[81920];
                    int read;
                    while ((read = await source.ReadAsync(buffer, 0, buffer.Length, timeout.Token).ConfigureAwait(false)) > 0)
                    {
                        written += read;
                        if (written > MaxDownloadBytes) throw new ModDbException("the download is unreasonably large");

                        await target.WriteAsync(buffer, 0, read, timeout.Token).ConfigureAwait(false);
                    }

                    await target.FlushAsync(timeout.Token).ConfigureAwait(false);
                    target.Flush(flushToDisk: true);
                }

                if (announced.HasValue && written != announced.Value)
                {
                    throw new ModDbException("the download was cut short");
                }

                if (written == 0) throw new ModDbException("the download was empty");
            }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
            {
                throw new ModDbException("the download timed out");
            }
            catch (HttpRequestException e)
            {
                throw new ModDbException("the download failed (" + ModUpdateRules.AsciiLine(e.Message, 80) + ")");
            }
            catch (IOException e)
            {
                throw new ModDbException("the download could not be written (" + ModUpdateRules.AsciiLine(e.Message, 80) + ")");
            }
        }

        /// <summary>
        /// The API address under the configured mod DB root - <c>ModDbUtil</c>'s own
        /// <c>new Uri(modDbUrl, "api/")</c>, made tolerant of a root without its trailing slash.
        /// </summary>
        private static Uri ApiUri(string modDbUrl, string action)
        {
            string root = string.IsNullOrWhiteSpace(modDbUrl) ? "https://mods.vintagestory.at/" : modDbUrl.Trim();
            if (!root.EndsWith("/", StringComparison.Ordinal)) root += "/";

            if (!Uri.TryCreate(root, UriKind.Absolute, out Uri baseUri)
                || (baseUri.Scheme != Uri.UriSchemeHttps && baseUri.Scheme != Uri.UriSchemeHttp))
            {
                throw new ModDbException("the configured mod DB address is not a web address");
            }

            return new Uri(new Uri(baseUri, "api/"), action);
        }
    }
}
