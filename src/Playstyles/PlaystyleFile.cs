using System.Collections.Generic;

namespace BetterMods.Playstyles
{
    /// <summary>
    /// One <c>.vsplaystyle</c> file as read from the Playstyles folder: plain data, already bounded
    /// and ASCII-cleaned by <see cref="PlaystyleFormat.Parse"/>. The world-config values are still
    /// RAW text here - whether a value is acceptable depends on the attribute an installed mod
    /// defines for its key, which is only known when the menu builds the playstyle list
    /// (<see cref="CustomPlaystyles"/>).
    /// </summary>
    public sealed class PlaystyleFile
    {
        /// <summary>Full path of the file on disk.</summary>
        public string Path;

        /// <summary>File name without the extension, exactly as on disk.</summary>
        public string Slug;

        /// <summary>The menu-side playstyle code, <see cref="PlaystyleFormat.CodePrefix"/> + lower-cased slug (made unique).</summary>
        public string Code;

        public string Name;
        public string Description;

        /// <summary>Code of the built-in playstyle this one overrides (e.g. <c>surviveandbuild</c>).</summary>
        public string BaseCode;

        /// <summary>The requested world height, unclamped; null when the file names none.</summary>
        public int? WorldHeight;

        /// <summary>Attribute code -> raw value text, in file order.</summary>
        public List<KeyValuePair<string, string>> Overrides = new List<KeyValuePair<string, string>>();

        /// <summary>Optional attribute code -> id of the mod that defines it (only used to explain skipped keys).</summary>
        public Dictionary<string, string> SettingMods = new Dictionary<string, string>();

        /// <summary>True for files a mod supplies: BetterMods never deletes or overwrites them.</summary>
        public bool ReadOnly;

        public string Author;
        public string CreatedBy;
        public string GameVersion;

        /// <summary>World-config entries dropped while parsing (non-primitive values, bad keys, over the cap).</summary>
        public int RejectedKeys;
    }
}
