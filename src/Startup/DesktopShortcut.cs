using System;
using System.Collections.Generic;
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
    /// <c>/d /v:on /c set "H=&lt;hook&gt;" &amp; (if exist "!H!" set "DOTNET_STARTUP_HOOKS=!H!") &amp; start "" "&lt;game exe&gt;"</c>
    /// (see <see cref="CmdArguments"/>), minimised, in the game folder, with the game's icon. The
    /// <c>if exist</c> guard is deliberate and load-bearing: a <c>DOTNET_STARTUP_HOOKS</c> entry naming a missing file stops the game launching
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
        /// .lnk that runs <c>cmd.exe</c> and names <paramref name="hookPath"/> in any of the forms of
        /// <see cref="HookSignatures"/> (one the player renamed or made by hand). Returns the one found
        /// in <paramref name="found"/>.
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

                string[][] signatures = HookSignatures(hookPath, DataPathArgument());

                foreach (string file in Directory.EnumerateFiles(desktop, "*.lnk"))
                {
                    if (!LooksLikeOurs(file, signatures)) continue;

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

        /// <summary>
        /// The ways a shortcut can name <paramref name="hookPath"/>; a .lnk naming it holds every string
        /// of one of these sets. The literal path (shortcuts from before the variables, and hand-made
        /// ones), the path with <c>%APPDATA%</c> for its AppData folder, and - with a data path - the
        /// <c>D</c> value plus the hook written relative to it, exactly as <see cref="CmdArguments"/>
        /// writes them.
        /// </summary>
        private static string[][] HookSignatures(string hookPath, string dataPath)
        {
            CmdValues(hookPath, dataPath, out string data, out string hook);
            string appData = WithAppDataVariable(hookPath);

            List<string[]> signatures = new List<string[]> { new[] { hookPath } };
            if (!string.Equals(appData, hookPath, StringComparison.OrdinalIgnoreCase)) signatures.Add(new[] { appData });
            if (data != null && hook.StartsWith("!D!", StringComparison.Ordinal)) signatures.Add(new[] { "\"D=" + data + "\"", hook });

            return signatures.ToArray();
        }

        private static bool LooksLikeOurs(string file, string[][] signatures)
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

                bool namesHook = false;
                foreach (string[] signature in signatures)
                {
                    bool all = true;
                    foreach (string part in signature)
                    {
                        if (even.Contains(part, StringComparison.OrdinalIgnoreCase) || odd.Contains(part, StringComparison.OrdinalIgnoreCase)) continue;

                        all = false;
                        break;
                    }

                    if (!all) continue;

                    namesHook = true;
                    break;
                }

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
        /// The most characters of a .lnk's arguments Windows passes on when the shortcut is started.
        /// Longer ones are cut there (the shell reads back 259 too), so the command would break.
        /// </summary>
        public const int MaxShortcutArguments = 259;

        /// <summary>
        /// The cmd.exe arguments the shortcut runs:
        /// <c>/d /v:on /c [set "D=&lt;data&gt;" &amp; ]set "H=&lt;hook&gt;" &amp; (if exist "!H!" set "DOTNET_STARTUP_HOOKS=!H!") &amp; start "" "&lt;exe&gt;"[ --dataPath "!D!"]</c>.
        /// <para>
        /// The parentheses are load-bearing: without them cmd makes everything after the <c>if</c>
        /// part of it, and a missing hook file would start nothing. The paths sit in variables
        /// (read with delayed expansion, <c>!H!</c>) so each long path is written once, the hook is
        /// written relative to <c>D</c> when it lives there, and a leading AppData folder is written
        /// as <c>%APPDATA%</c> - all to stay within <see cref="MaxShortcutArguments"/>. If the line is
        /// still too long, the game is named <c>.\Vintagestory.exe</c>, relative to the shortcut's
        /// working directory (the game folder).
        /// </para>
        /// Null (with a reason) when a path holds a character cmd.exe would reinterpret even inside
        /// quotes - <c>%</c>, <c>!</c> and <c>^</c> (variable expansion) or <c>"</c> - or when the line
        /// would still be too long.
        /// </summary>
        public static string CmdArguments(string hookPath, string gameExe, string dataPath, out string error)
        {
            error = null;

            if (string.IsNullOrEmpty(hookPath) || string.IsNullOrEmpty(gameExe))
            {
                error = "the startup hook or game path is unknown";
                return null;
            }

            foreach (string path in new[] { hookPath, gameExe, dataPath })
            {
                if (path == null) continue;
                if (path.IndexOfAny(CmdUnsafe) >= 0)
                {
                    error = "the path '" + path + "' contains a character a shortcut cannot pass through cmd.exe (% ! ^ or \")";
                    return null;
                }
            }

            CmdValues(hookPath, dataPath, out string data, out string hook);

            string arguments = CmdLine(data, hook, WithAppDataVariable(gameExe));

            // Still too long: name the game relative to the shortcut's working directory, which
            // TryCreate sets to the game folder (/d keeps an AutoRun script from changing it).
            if (arguments.Length > MaxShortcutArguments) arguments = CmdLine(data, hook, ".\\" + Path.GetFileName(gameExe));

            if (arguments.Length > MaxShortcutArguments)
            {
                error = "the shortcut command would be " + arguments.Length + " characters, more than the " + MaxShortcutArguments +
                        " Windows passes on (the data folder path is too long)";
                return null;
            }

            return arguments;
        }

        private static string CmdLine(string data, string hook, string gameExe)
        {
            StringBuilder line = new StringBuilder("/d /v:on /c ");
            if (data != null) line.Append("set \"D=").Append(data).Append("\" & ");
            line.Append("set \"H=").Append(hook).Append("\" & (if exist \"!H!\" set \"DOTNET_STARTUP_HOOKS=!H!\") & start \"\" \"")
                .Append(gameExe).Append('"');
            if (data != null) line.Append(" --dataPath \"!D!\"");

            return line.ToString();
        }

        private static readonly char[] CmdUnsafe = { '%', '"', '!', '^' };

        /// <summary>
        /// What <see cref="CmdArguments"/> writes for <c>D</c> (null without a data path) and
        /// <c>H</c>: the hook as <c>!D!\...</c> when it lives under the data path, a leading AppData
        /// folder as <c>%APPDATA%</c>.
        /// </summary>
        private static void CmdValues(string hookPath, string dataPath, out string data, out string hook)
        {
            data = dataPath == null ? null : WithAppDataVariable(dataPath);

            if (dataPath != null && hookPath.Length > dataPath.Length &&
                hookPath.StartsWith(dataPath, StringComparison.OrdinalIgnoreCase) &&
                hookPath[dataPath.Length] == Path.DirectorySeparatorChar)
            {
                hook = "!D!" + hookPath.Substring(dataPath.Length);
            }
            else
            {
                hook = WithAppDataVariable(hookPath);
            }
        }

        /// <summary>
        /// <paramref name="path"/> with a leading AppData folder written as <c>%APPDATA%</c>, which
        /// cmd expands when the shortcut starts. Only when the <c>APPDATA</c> variable and the shell's
        /// AppData folder agree; any other path is returned as it is.
        /// </summary>
        private static string WithAppDataVariable(string path)
        {
            try
            {
                string variable = Environment.GetEnvironmentVariable("APPDATA");
                string folder = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                if (string.IsNullOrEmpty(variable) || string.IsNullOrEmpty(folder)) return path;

                variable = Path.TrimEndingDirectorySeparator(variable);
                folder = Path.TrimEndingDirectorySeparator(folder);
                if (!string.Equals(variable, folder, StringComparison.OrdinalIgnoreCase)) return path;

                if (path.Length > folder.Length &&
                    path.StartsWith(folder, StringComparison.OrdinalIgnoreCase) &&
                    path[folder.Length] == Path.DirectorySeparatorChar)
                {
                    return "%APPDATA%" + path.Substring(folder.Length);
                }
            }
            catch (Exception)
            {
                // The literal path always works.
            }

            return path;
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
