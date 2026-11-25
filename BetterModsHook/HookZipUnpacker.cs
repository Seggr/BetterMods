using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace BetterMods.Hook
{
    /// <summary>
    /// Puts a zip-installed BetterMods into the game's unpack cache, under the exact folder name the
    /// game would use, so that the hook and the game later load the same <c>BetterMods.dll</c> path.
    /// <para>
    /// <b>Why the game's own folder.</b> The game unpacks a zip mod only at WORLD LOAD
    /// (<c>ModLoader.LoadMods</c> -> <c>ModContainer.Unpack</c>), into
    /// <c>Path.Combine(&lt;cache&gt;\unpack, &lt;zip file name incl. extension&gt; + "_" + &lt;first 6
    /// bytes of SHA-1(zip file), lower hex&gt;)</c>, and it <b>early-returns when that folder
    /// already exists</b>, never re-checking its contents (ModContainer.cs:126-185 on 1.23.0,
    /// :108-170 on 1.22.7; identical naming on both). Its file scan then lists the dlls in that
    /// folder and <c>ModAssemblyLoader.LoadFrom</c> (<c>Assembly.UnsafeLoadFrom</c>) loads them - the
    /// path the hook already loaded, so the game gets the same assembly back. Any other location
    /// would make the game's load a second assembly called BetterMods ->
    /// <c>ModError.ChangedVersion</c>. At the same world load <c>ClearCacheFolder</c> deletes every
    /// cache folder that is not an error-free mod's <c>FolderPath</c>; ours is exactly that path, so
    /// it stays, while a leftover temp folder of ours would go.
    /// </para>
    /// <para>
    /// <b>Why it must be right first time.</b> Because the game trusts an existing folder forever,
    /// a partial or different extraction would be permanent. Hence: the zip is read into memory once,
    /// so the bytes that name the folder are the bytes extracted; every entry is validated before
    /// anything is written (no rooted names, no drive letters or colons, no backslashes, no
    /// <c>.</c>/<c>..</c>/empty segments, no Windows device names, size and count limits); the
    /// extraction goes to a sibling temp folder, is checked file-by-file against the archive, and only
    /// then renamed into place (a same-volume directory rename, atomic). Files are written as the game
    /// writes them: one file per entry at <c>Path.Combine(folder, entry.FullName)</c>, later
    /// duplicates overwriting earlier ones, directory entries (<c>FullName</c> ending in <c>/</c>)
    /// created as folders, last-write time from the entry. An archive the game itself would refuse
    /// to extract is refused here too, and left to the game.
    /// </para>
    /// <para>
    /// An existing folder is used as-is when every file of the archive is in it at its archived size
    /// (the game's own extraction always is). An incomplete one - a crash mid-extract - is the one
    /// thing replaced, by the same temp-then-rename route: the old folder is renamed aside, the new
    /// one moved in, and on failure the old one is moved straight back.
    /// </para>
    /// </summary>
    internal static class HookZipUnpacker
    {
        /// <summary>BetterMods' zip is a few hundred KB; anything near these is not it.</summary>
        public const long MaxArchiveBytes = 32L * 1024 * 1024;
        public const long MaxUnpackedBytes = 128L * 1024 * 1024;
        public const int MaxEntries = 1000;
        private const int MaxEntryNameLength = 512;

        /// <summary>The hash suffix <c>ModContainer.Unpack</c> appends: 6 bytes of SHA-1 as 12 hex digits.</summary>
        private const int HashBytes = 6;

        private const string TempMarker = ".bettermods-tmp-";
        private const string AsideMarker = ".bettermods-old-";

        private static readonly StringComparer PathComparer =
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        private sealed class PlannedEntry
        {
            public ZipArchiveEntry Entry;
            public string RelativePath;
            public bool IsDirectory;
        }

        /// <summary>
        /// <c>&lt;zip file name&gt;_&lt;12 hex&gt;</c>: the cache folder name <c>ModContainer.Unpack</c>
        /// gives an archive with this file name and these bytes.
        /// </summary>
        public static string FolderName(string zipFileName, byte[] zipBytes)
        {
            byte[] hash = SHA1.HashData(zipBytes);

            StringBuilder name = new StringBuilder(zipFileName.Length + 1 + HashBytes * 2);
            name.Append(zipFileName).Append('_');
            for (int i = 0; i < HashBytes; i++) name.Append(hash[i].ToString("x2"));

            return name.ToString();
        }

        /// <summary>
        /// The <c>BetterMods.dll</c> inside the game's extraction of <paramref name="zipPath"/>,
        /// unpacking it first when it is missing and <paramref name="allowUnpack"/> is set. Null, with
        /// the reason logged, when there is none to use. Never throws.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static string Resolve(string zipPath, string unpackRoot, bool allowUnpack)
        {
            string folder = null;

            try
            {
                byte[] bytes = ReadArchive(zipPath);
                if (bytes == null)
                {
                    HookLog.Write("Did not unpack '" + zipPath + "': larger than " + MaxArchiveBytes / (1024 * 1024) +
                                  " MB, which BetterMods never is.");
                    return null;
                }

                folder = Path.GetFullPath(Path.Combine(unpackRoot, FolderName(Path.GetFileName(zipPath), bytes)));
                string dll = Path.Combine(folder, HookPaths.ModAssemblyFileName);

                using ZipArchive archive = new ZipArchive(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read, leaveOpen: false);

                List<PlannedEntry> plan = Plan(archive, out string problem);
                if (plan == null)
                {
                    HookLog.Write("Did not unpack '" + zipPath + "': " + problem + ".");
                    return UseExisting(folder, dll);
                }

                RemoveLeftovers(unpackRoot);

                bool repair = false;
                if (Directory.Exists(folder))
                {
                    string missing = FirstMismatch(folder, plan);
                    if (missing == null) return UseExisting(folder, dll);

                    if (!allowUnpack)
                    {
                        HookLog.Write("'" + folder + "' is incomplete (" + missing + ") but was left alone.");
                        return UseExisting(folder, dll);
                    }

                    HookLog.Write("'" + folder + "' is incomplete (" + missing + "); unpacking '" + zipPath + "' again.");
                    repair = true;
                }
                else if (!allowUnpack)
                {
                    HookLog.Write("Did nothing: '" + zipPath + "' has not been unpacked yet, and was not unpacked now.");
                    return null;
                }

                if (!HasRootFile(plan, HookPaths.ModAssemblyFileName))
                {
                    HookLog.Write("Did not unpack '" + zipPath + "': it has no " + HookPaths.ModAssemblyFileName + " at its root.");
                    return null;
                }

                string temp = Extract(plan, unpackRoot, Path.GetFileName(folder));
                try
                {
                    string extracted = FirstMismatch(temp, plan);
                    if (extracted != null) throw new IOException("The extraction does not match the archive: " + extracted + ".");

                    if (repair) Swap(temp, folder);
                    else Publish(temp, folder, plan);
                }
                finally
                {
                    TryDeleteDirectory(temp);
                }

                HookLog.Write((repair ? "Re-unpacked '" : "Unpacked '") + zipPath + "' into '" + folder + "' (" + plan.Count +
                              " entries) - the folder the game unpacks this archive to at world load, which it will now " +
                              "reuse as-is. BetterMods is loaded from there on this launch already.");

                return UseExisting(folder, dll);
            }
            catch (Exception e)
            {
                HookLog.Write("Could not unpack '" + zipPath + "'" + (folder != null ? " into '" + folder + "'" : "") +
                              ". Nothing was changed; the game unpacks it itself at the next world load.", e);
                return null;
            }
        }

        private static string UseExisting(string folder, string dll)
        {
            if (File.Exists(dll)) return dll;

            if (Directory.Exists(folder)) HookLog.Write("Did nothing: '" + folder + "' has no " + HookPaths.ModAssemblyFileName + ".");
            return null;
        }

        private static byte[] ReadArchive(string zipPath)
        {
            using FileStream stream = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > MaxArchiveBytes) return null;

            byte[] bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            return bytes;
        }

        // ---------------------------------------------------------------- validation

        /// <summary>
        /// Every entry with its relative path, or null and the first reason the archive is refused.
        /// Nothing is written until the whole archive has passed.
        /// </summary>
        private static List<PlannedEntry> Plan(ZipArchive archive, out string problem)
        {
            problem = null;

            if (archive.Entries.Count > MaxEntries)
            {
                problem = "it has " + archive.Entries.Count + " entries (limit " + MaxEntries + ")";
                return null;
            }

            List<PlannedEntry> plan = new List<PlannedEntry>(archive.Entries.Count);
            long total = 0;

            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                string name = entry.FullName;
                string relative = ValidateName(name, out bool isDirectory, out string reason);
                if (relative == null)
                {
                    problem = "entry '" + name + "' " + reason;
                    return null;
                }

                if (entry.Length < 0 || entry.Length > MaxUnpackedBytes || (total += entry.Length) > MaxUnpackedBytes)
                {
                    problem = "it unpacks to more than " + MaxUnpackedBytes / (1024 * 1024) + " MB";
                    return null;
                }

                if (isDirectory && entry.Length != 0)
                {
                    problem = "directory entry '" + name + "' has content";
                    return null;
                }

                plan.Add(new PlannedEntry { Entry = entry, RelativePath = relative, IsDirectory = isDirectory });
            }

            return plan;
        }

        /// <summary>
        /// The entry's path relative to the extraction folder, or null and why it is refused. Stricter
        /// than the game (which only rejects a path that leaves its folder after normalisation): every
        /// name that could resolve differently on another platform, or alias another file on Windows,
        /// is refused outright. BetterMods' own archive contains none of them.
        /// </summary>
        private static string ValidateName(string name, out bool isDirectory, out string reason)
        {
            isDirectory = false;
            reason = null;

            if (string.IsNullOrEmpty(name)) { reason = "has no name"; return null; }
            if (name.Length > MaxEntryNameLength) { reason = "has an over-long name"; return null; }

            foreach (char c in name)
            {
                if (c < 0x20 || c == ':' || c == '\\')
                {
                    reason = "contains a control character, a colon or a backslash";
                    return null;
                }
            }

            if (name[0] == '/' || Path.IsPathRooted(name)) { reason = "is an absolute path"; return null; }

            isDirectory = name.EndsWith('/');
            string body = isDirectory ? name.Substring(0, name.Length - 1) : name;

            string[] segments = body.Split('/');
            char[] invalid = Path.GetInvalidFileNameChars();

            foreach (string segment in segments)
            {
                if (segment.Length == 0 || segment == "." || segment == "..")
                {
                    reason = "has an empty, '.' or '..' path segment";
                    return null;
                }

                if (segment.IndexOfAny(invalid) >= 0 || segment.EndsWith('.') || segment.EndsWith(' ') || IsDeviceName(segment))
                {
                    reason = "has a path segment that is not a portable file name";
                    return null;
                }
            }

            return Path.Combine(segments);
        }

        private static bool IsDeviceName(string segment)
        {
            int dot = segment.IndexOf('.');
            string stem = (dot >= 0 ? segment.Substring(0, dot) : segment).TrimEnd(' ').ToUpperInvariant();

            switch (stem)
            {
                case "CON":
                case "PRN":
                case "AUX":
                case "NUL":
                case "CONIN$":
                case "CONOUT$":
                    return true;
            }

            return stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) &&
                   stem[3] >= '0' && stem[3] <= '9';
        }

        private static bool HasRootFile(List<PlannedEntry> plan, string fileName)
        {
            foreach (PlannedEntry planned in plan)
            {
                if (!planned.IsDirectory && string.Equals(planned.RelativePath, fileName, StringComparison.Ordinal)) return true;
            }

            return false;
        }

        /// <summary>
        /// Null if <paramref name="folder"/> holds every file of the archive at its archived size (for
        /// a path archived twice, the last copy - the one an extraction leaves behind) and every
        /// directory entry; otherwise the first thing that is missing or different.
        /// </summary>
        private static string FirstMismatch(string folder, List<PlannedEntry> plan)
        {
            Dictionary<string, long> files = new Dictionary<string, long>(PathComparer);
            List<string> directories = new List<string>();

            foreach (PlannedEntry planned in plan)
            {
                if (planned.IsDirectory) directories.Add(planned.RelativePath);
                else files[planned.RelativePath] = planned.Entry.Length;
            }

            foreach (KeyValuePair<string, long> file in files)
            {
                FileInfo info = new FileInfo(Path.Combine(folder, file.Key));
                if (!info.Exists) return "no " + file.Key;
                if (info.Length != file.Value) return file.Key + " is " + info.Length + " bytes, not " + file.Value;
            }

            foreach (string directory in directories)
            {
                if (!Directory.Exists(Path.Combine(folder, directory))) return "no folder " + directory;
            }

            return null;
        }

        // ---------------------------------------------------------------- writing

        /// <summary>Extracts the whole plan into a fresh temp folder beside the target and returns it.</summary>
        private static string Extract(List<PlannedEntry> plan, string unpackRoot, string folderName)
        {
            Directory.CreateDirectory(unpackRoot);

            string temp = Path.GetFullPath(Path.Combine(unpackRoot, folderName + TempMarker + Guid.NewGuid().ToString("N").Substring(0, 8)));
            Directory.CreateDirectory(temp);

            string prefix = temp + Path.DirectorySeparatorChar;

            try
            {
                foreach (PlannedEntry planned in plan)
                {
                    string target = Path.GetFullPath(Path.Combine(temp, planned.RelativePath));
                    if (!target.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        throw new IOException("Entry '" + planned.Entry.FullName + "' would leave the extraction folder.");
                    }

                    if (planned.IsDirectory)
                    {
                        Directory.CreateDirectory(target);
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(target));

                    using (Stream source = planned.Entry.Open())
                    using (FileStream destination = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        CopyExactly(source, destination, planned.Entry.Length, planned.Entry.FullName);
                        destination.Flush(flushToDisk: true);
                    }

                    try
                    {
                        File.SetLastWriteTime(target, planned.Entry.LastWriteTime.DateTime);
                    }
                    catch (Exception)
                    {
                        // ExtractToFile does this too; a file without it is still the same file.
                    }
                }
            }
            catch (Exception)
            {
                TryDeleteDirectory(temp);
                throw;
            }

            return temp;
        }

        /// <summary>
        /// Copies exactly the entry's declared size - an entry that inflates to more (a zip bomb) or
        /// fewer bytes than it declares is an error, not a truncated or oversized file.
        /// </summary>
        private static void CopyExactly(Stream source, Stream destination, long expected, string name)
        {
            byte[] buffer = new byte[81920];
            long written = 0;

            while (true)
            {
                int read = source.Read(buffer, 0, buffer.Length);
                if (read <= 0) break;

                written += read;
                if (written > expected) throw new InvalidDataException("Entry '" + name + "' inflates past its declared size.");

                destination.Write(buffer, 0, read);
            }

            if (written != expected) throw new InvalidDataException("Entry '" + name + "' is shorter than its declared size.");
        }

        /// <summary>
        /// Renames the finished temp folder to the target name. If another process created the target
        /// in the meantime (a second game starting at the same moment) and it is complete, that one is
        /// as good as ours.
        /// </summary>
        private static void Publish(string temp, string folder, List<PlannedEntry> plan)
        {
            try
            {
                Directory.Move(temp, folder);
            }
            catch (IOException) when (Directory.Exists(folder) && FirstMismatch(folder, plan) == null)
            {
                // Somebody else got there first, with the same content.
            }
        }

        /// <summary>Replaces an incomplete extraction: old aside, new in, old back on failure.</summary>
        private static void Swap(string temp, string folder)
        {
            string aside = folder + AsideMarker + Guid.NewGuid().ToString("N").Substring(0, 8);
            Directory.Move(folder, aside);

            try
            {
                Directory.Move(temp, folder);
            }
            catch (Exception)
            {
                try
                {
                    Directory.Move(aside, folder);
                }
                catch (Exception restore)
                {
                    HookLog.Write("Could not move '" + aside + "' back to '" + folder + "'.", restore);
                }

                throw;
            }

            TryDeleteDirectory(aside);
        }

        /// <summary>
        /// Best-effort removal of temp/aside folders an earlier launch could not finish with. The
        /// game's <c>ClearCacheFolder</c> would delete them at the next world load anyway.
        /// </summary>
        private static void RemoveLeftovers(string unpackRoot)
        {
            try
            {
                if (!Directory.Exists(unpackRoot)) return;

                foreach (string directory in Directory.GetDirectories(unpackRoot))
                {
                    string name = Path.GetFileName(directory);
                    if (name.Contains(TempMarker, StringComparison.Ordinal) || name.Contains(AsideMarker, StringComparison.Ordinal))
                    {
                        TryDeleteDirectory(directory);
                    }
                }
            }
            catch (Exception)
            {
                // Leftovers cost disk space, nothing else.
            }
        }

        private static void TryDeleteDirectory(string directory)
        {
            try
            {
                if (directory != null && Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            }
            catch (Exception)
            {
                // See RemoveLeftovers.
            }
        }
    }
}
