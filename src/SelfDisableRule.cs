using System;
using System.Collections.Generic;
using System.Text.Json;

namespace BetterMods
{
    /// <summary>
    /// Pure decision behind <see cref="PatchBootstrap"/>'s "has the player switched THIS BetterMods
    /// off?" check, kept free of game types so it can be exercised on its own.
    /// <para>
    /// <c>ModLoader.DisableMods</c> disables a mod when the disabled set holds exactly
    /// <c>Info.ModID + "@" + Info.Version</c> or the bare <c>Info.ModID</c> (NOTES, "Mod
    /// enable/disable persistence"). With two BetterMods versions installed, an entry for the OTHER
    /// version says nothing about the copy that is running, so only the bare id and this copy's own
    /// key count.
    /// </para>
    /// </summary>
    internal static class SelfDisableRule
    {
        /// <summary>
        /// True if <paramref name="disabledMods"/> switches off the copy whose version is
        /// <paramref name="ownVersion"/>: a bare <paramref name="modId"/>, or
        /// <c>modId@ownVersion</c>. Compared trimmed and case-insensitively (the game is ordinal and
        /// exact; the leniency only ever errs towards honouring a switch-off the player made).
        /// <para>
        /// <paramref name="ownVersion"/> null means the running copy's version could not be
        /// determined: then only the bare id counts. Guessing which <c>@version</c> entry is ours
        /// would bring back exactly the "another version's entry switches us off" bug, and a missed
        /// switch-off is recoverable (the player can disable the bare id, or remove the copy) while
        /// a wrongly patch-less session is the silent failure this check must never produce.
        /// </para>
        /// </summary>
        public static bool IsSwitchedOff(IEnumerable<string> disabledMods, string modId, string ownVersion)
        {
            if (disabledMods == null || string.IsNullOrEmpty(modId)) return false;

            string ownKey = ownVersion == null ? null : modId + "@" + ownVersion.Trim();

            foreach (string entry in disabledMods)
            {
                if (string.IsNullOrWhiteSpace(entry)) continue;

                string trimmed = entry.Trim();
                if (trimmed.Equals(modId, StringComparison.OrdinalIgnoreCase)) return true;
                if (ownKey != null && trimmed.Equals(ownKey, StringComparison.OrdinalIgnoreCase)) return true;
            }

            return false;
        }

        /// <summary>
        /// The <c>version</c> a <c>modinfo.json</c> declares, read the way Newtonsoft fills
        /// <c>ModInfo</c> (and the way the startup hook's <c>HookModLocator.TryParseModInfo</c> does):
        /// property names case-insensitively, the last match wins, a number or boolean taken as its
        /// raw text, a missing <c>version</c> is <c>""</c>. Null when the text is not a JSON object or
        /// the value is not a scalar - i.e. "unknown", never a throw.
        /// </summary>
        public static string ReadVersion(string modInfoJson)
        {
            if (modInfoJson == null) return null;

            try
            {
                using JsonDocument document = JsonDocument.Parse(
                    modInfoJson.TrimStart('﻿'),
                    new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });

                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return null;

                string version = "";
                foreach (JsonProperty property in root.EnumerateObject())
                {
                    if (!property.Name.Equals("version", StringComparison.OrdinalIgnoreCase)) continue;

                    switch (property.Value.ValueKind)
                    {
                        case JsonValueKind.String: version = property.Value.GetString(); break;
                        case JsonValueKind.Number:
                        case JsonValueKind.True:
                        case JsonValueKind.False: version = property.Value.GetRawText(); break;
                        case JsonValueKind.Null: version = ""; break;
                        default: return null;
                    }
                }

                return version;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
