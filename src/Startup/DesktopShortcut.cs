using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Vintagestory.API.Config;

namespace BetterMods.Startup
{
    /// <summary>
    /// The "launch with the startup hook" command, and - on Windows - the desktop shortcut that runs it.
    /// <para>
    /// The shortcut is <c>cmd.exe</c> with
    /// <c>/c if exist "&lt;hook&gt;" (set "DOTNET_STARTUP_HOOKS=&lt;hook&gt;") &amp; start "" "&lt;game exe&gt;"</c>,
    /// minimised, in the game folder, with the game's icon. The <c>if exist</c> guard is deliberate and
    /// load-bearing: a <c>DOTNET_STARTUP_HOOKS</c> entry naming a missing file stops the game launching
    /// at all, so the variable is only set when the file is really there - delete
    /// <c>ModConfig\BetterMods\</c> and the shortcut still starts a plain game. The variable is set in
    /// that one cmd process and inherited by the game only; nothing machine-wide is touched.
    /// </para>
    /// </summary>
    public static class DesktopShortcut
    {
        public const string ShortcutName = "Vintage Story (BetterMods)";

        /// <summary>Largest file treated as a candidate shortcut when looking for ours. Real .lnk files are a few KB.</summary>
        private const int MaxShortcutBytes = 64 * 1024;

        public static bool IsSupported => OperatingSystem.IsWindows();

        /// <summary>The desktop, as the shell sees it - OneDrive-redirected where it is. Null if there is none.</summary>
        public static string DesktopDirectory
        {
            get
            {
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                if (string.IsNullOrEmpty(desktop)) desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                return string.IsNullOrEmpty(desktop) ? null : desktop;
            }
        }

        public static string ShortcutPath
        {
            get
            {
                string desktop = DesktopDirectory;
                return desktop == null ? null : Path.Combine(desktop, ShortcutName + ".lnk");
            }
        }

        /// <summary>
        /// The desktop already has our shortcut: a file named <see cref="ShortcutName"/>.lnk, or any
        /// .lnk that runs <c>cmd.exe</c> and names <paramref name="hookPath"/> (one the player renamed
        /// or made by hand). Returns the one found in <paramref name="found"/>.
        /// <para>
        /// Reads the .lnk files' bytes rather than resolving each one through the shell: the target
        /// path is stored as text (ANSI in the link info, UTF-16 in the string data and the ID list)
        /// and the arguments as UTF-16, so a byte search answers "cmd.exe + our hook path" without a
        /// COM call per file. Called once per card rebuild, never per frame. Never throws.
        /// </para>
        /// </summary>
        public static bool Exists(string hookPath, out string found)
        {
            found = null;

            try
            {
                string desktop = DesktopDirectory;
                if (desktop == null || !Directory.Exists(desktop)) return false;

                string named = Path.Combine(desktop, ShortcutName + ".lnk");
                if (File.Exists(named))
                {
                    found = named;
                    return true;
                }

                if (string.IsNullOrEmpty(hookPath)) return false;

                foreach (string file in Directory.EnumerateFiles(desktop, "*.lnk"))
                {
                    if (!LooksLikeOurs(file, hookPath)) continue;

                    found = file;
                    return true;
                }
            }
            catch (Exception e)
            {
                StartupLog.Warning("[BetterMods] Could not look for the desktop shortcut: " + e.Message);
            }

            return false;
        }

        private static bool LooksLikeOurs(string file, string hookPath)
        {
            try
            {
                FileInfo info = new FileInfo(file);
                if (!info.Exists || info.Length > MaxShortcutBytes) return false;

                byte[] bytes = File.ReadAllBytes(file);

                // UTF-16 strings in a .lnk are not necessarily 2-byte aligned within the file, so both
                // alignments are decoded. The ANSI copy of the target lives in the link info.
                string even = Encoding.Unicode.GetString(bytes);
                string odd = bytes.Length > 1 ? Encoding.Unicode.GetString(bytes, 1, bytes.Length - 1) : "";
                string ansi = Encoding.Latin1.GetString(bytes);

                bool namesHook = even.Contains(hookPath, StringComparison.OrdinalIgnoreCase) ||
                                 odd.Contains(hookPath, StringComparison.OrdinalIgnoreCase);
                if (!namesHook) return false;

                return even.Contains("cmd.exe", StringComparison.OrdinalIgnoreCase) ||
                       odd.Contains("cmd.exe", StringComparison.OrdinalIgnoreCase) ||
                       ansi.Contains("cmd.exe", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ---------------------------------------------------------------- the launch

        /// <summary>
        /// The game executable to start: the running process when it is the game's own launcher
        /// (<c>Vintagestory.exe</c> / <c>Vintagestory</c>), else that file beside
        /// <see cref="GamePaths.Binaries"/>. Null when neither exists. Never throws.
        /// </summary>
        public static string GameExecutable()
        {
            try
            {
                string name = OperatingSystem.IsWindows() ? "Vintagestory.exe" : "Vintagestory";

                string process = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(process) &&
                    string.Equals(Path.GetFileName(process), name, StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(process))
                {
                    return Path.GetFullPath(process);
                }

                string beside = Path.Combine(GamePaths.Binaries, name);
                if (File.Exists(beside)) return Path.GetFullPath(beside);
            }
            catch (Exception)
            {
                // Fall through to "unknown".
            }

            return null;
        }

        /// <summary>
        /// This launch's <c>--dataPath</c>, made absolute, or null when it had none. Carried into the
        /// shortcut and the command: the hook lives under that data path, and BetterMods with it.
        /// </summary>
        public static string DataPathArgument()
        {
            try
            {
                string[] args = Environment.GetCommandLineArgs();
                for (int i = 1; i < args.Length; i++)
                {
                    string arg = args[i];
                    if (string.IsNullOrEmpty(arg)) continue;

                    string value = null;
                    if (arg.StartsWith("--dataPath=", StringComparison.OrdinalIgnoreCase)) value = arg.Substring("--dataPath=".Length);
                    else if (arg.Equals("--dataPath", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) value = args[i + 1];

                    if (string.IsNullOrWhiteSpace(value)) continue;

                    // Same root the game resolves it against: the binaries directory is the working
                    // directory from ScreenManager construction onwards (NOTES, startup order).
                    string full = Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(GamePaths.Binaries, value));
                    return Path.TrimEndingDirectorySeparator(full);
                }
            }
            catch (Exception)
            {
                // No pass-through rather than a wrong one.
            }

            return null;
        }

        /// <summary>
        /// The POSIX-shell launch command for Linux/macOS, where no shortcut is made:
        /// <c>DOTNET_STARTUP_HOOKS='&lt;hook&gt;' '&lt;game&gt;'</c>. Null when the game executable
        /// cannot be found.
        /// </summary>
        public static string ShellCommand(string hookPath)
        {
            string game = PosixGameCommand();
            if (game == null) return null;

            string command = "DOTNET_STARTUP_HOOKS=" + ShellQuote(hookPath) + " " + game;

            string dataPath = DataPathArgument();
            if (dataPath != null) command += " --dataPath " + ShellQuote(dataPath);

            return command;
        }

        private static string PosixGameCommand()
        {
            string exe = GameExecutable();
            if (exe != null) return ShellQuote(exe);

            // Launched as "dotnet Vintagestory.dll".
            try
            {
                string process = Environment.ProcessPath;
                string dll = Path.Combine(GamePaths.Binaries, "Vintagestory.dll");
                if (!string.IsNullOrEmpty(process) && File.Exists(dll) &&
                    string.Equals(Path.GetFileNameWithoutExtension(process), "dotnet", StringComparison.OrdinalIgnoreCase))
                {
                    return ShellQuote(process) + " " + ShellQuote(Path.GetFullPath(dll));
                }
            }
            catch (Exception)
            {
                // Unknown.
            }

            return null;
        }

        private static string ShellQuote(string value)
        {
            return "'" + (value ?? "").Replace("'", "'\\''") + "'";
        }

        /// <summary>
        /// The cmd.exe arguments the shortcut runs. Null (with a reason) when a path holds a character
        /// cmd.exe would reinterpret even inside quotes: <c>%</c> (variable expansion on a
        /// <c>/c</c> line) or <c>"</c>.
        /// </summary>
        public static string CmdArguments(string hookPath, string gameExe, string dataPath, out string error)
        {
            error = null;

            foreach (string path in new[] { hookPath, gameExe, dataPath })
            {
                if (path == null) continue;
                if (path.IndexOf('%') >= 0 || path.IndexOf('"') >= 0)
                {
                    error = "the path '" + path + "' contains a character a shortcut cannot pass through cmd.exe (% or \")";
                    return null;
                }
            }

            string arguments = "/c if exist \"" + hookPath + "\" (set \"DOTNET_STARTUP_HOOKS=" + hookPath + "\") & start \"\" \"" + gameExe + "\"";
            if (dataPath != null) arguments += " --dataPath \"" + dataPath + "\"";

            return arguments;
        }

        // ---------------------------------------------------------------- the shortcut (Windows)

        /// <summary>
        /// Writes <see cref="ShortcutName"/>.lnk on the desktop. The caller has already checked that
        /// no shortcut of ours exists; a file of that name that is NOT ours is never overwritten
        /// (it counts as "exists" upstream, and is refused here as a backstop). Never throws.
        /// </summary>
        public static bool TryCreate(string hookPath, out string createdPath, out string error)
        {
            createdPath = null;
            error = null;

            if (!OperatingSystem.IsWindows())
            {
                error = "desktop shortcuts are only made on Windows";
                return false;
            }

            try
            {
                string path = ShortcutPath;
                if (path == null)
                {
                    error = "this account has no desktop folder";
                    return false;
                }

                if (File.Exists(path))
                {
                    error = "'" + path + "' already exists";
                    return false;
                }

                string gameExe = GameExecutable();
                if (gameExe == null)
                {
                    error = "could not find Vintagestory.exe";
                    return false;
                }

                string arguments = CmdArguments(hookPath, gameExe, DataPathArgument(), out error);
                if (arguments == null) return false;

                string cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");

                WriteShortcut(path, cmd, arguments, Path.GetDirectoryName(gameExe), gameExe + ",0");

                if (!File.Exists(path))
                {
                    error = "the shell reported success but no file was written";
                    return false;
                }

                createdPath = path;
                return true;
            }
            catch (Exception e)
            {
                error = e.Message;
                StartupLog.Error("[BetterMods] Could not create the desktop shortcut.");
                StartupLog.Error(e);
                return false;
            }
        }

        /// <summary>
        /// The shell's own shortcut writer, <c>WScript.Shell.CreateShortcut</c>, late-bound. Verified
        /// working from a .NET 10 process on MTA and STA threads alike (throwaway harness); the game's
        /// runtimeconfig keeps built-in COM interop on.
        /// </summary>
        [SupportedOSPlatform("windows")]
        private static void WriteShortcut(string path, string target, string arguments, string workingDirectory, string icon)
        {
            Type shellType = Type.GetTypeFromProgID("WScript.Shell", throwOnError: true);
            object shell = Activator.CreateInstance(shellType);
            object link = null;

            try
            {
                dynamic shellObject = shell;
                link = shellObject.CreateShortcut(path);

                dynamic linkObject = link;
                linkObject.TargetPath = target;
                linkObject.Arguments = arguments;
                linkObject.WorkingDirectory = workingDirectory;
                linkObject.IconLocation = icon;
                linkObject.WindowStyle = 7; // SW_SHOWMINNOACTIVE: the cmd window never takes focus.
                linkObject.Description = "Vintage Story with BetterMods' startup hook (profiles on the main menu from the start)";
                linkObject.Save();
            }
            finally
            {
                if (link != null) Marshal.FinalReleaseComObject(link);
                Marshal.FinalReleaseComObject(shell);
            }
        }
    }
}
