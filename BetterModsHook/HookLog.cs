using System;
using System.Globalization;
using System.IO;

namespace BetterMods.Hook
{
    /// <summary>
    /// The hook's only diagnostic channel. No game logger exists before <c>Main</c>, and by the time
    /// one does the hook has usually already finished, so failures go to
    /// <c>&lt;dataPath&gt;\Logs\bettermods-hook.log</c> instead.
    /// <para>
    /// Appends, never truncates, and never throws — a logging failure must not be able to take the
    /// game down with it.
    /// </para>
    /// </summary>
    internal static class HookLog
    {
        private static readonly object Lock = new object();

        public static void Write(string message)
        {
            try
            {
                string path = HookPaths.LogFilePath;
                if (string.IsNullOrWhiteSpace(path)) return;

                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)
                              + " [BetterModsHook] " + message + Environment.NewLine;

                lock (Lock)
                {
                    File.AppendAllText(path, line);
                }
            }
            catch (Exception)
            {
                // Nothing to report to, and nothing worth crashing the game over.
            }
        }

        public static void Write(string message, Exception e)
        {
            Write(message + Environment.NewLine + e);
        }
    }
}
