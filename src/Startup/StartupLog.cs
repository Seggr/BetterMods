using System;
using Vintagestory.API.Common;

namespace BetterMods.Startup
{
    /// <summary>
    /// Logging for the startup-hook code, which promises never to throw - so a log call must not be
    /// able to either. <see cref="BetterModsModSystem.Logger"/> catches its own failures, but not one
    /// raised while the runtime is still resolving the game types it names (it reaches into
    /// <c>ScreenManager</c>); that would surface here, at the call, and is swallowed.
    /// </summary>
    internal static class StartupLog
    {
        public static void Notification(string message) => Write(logger => logger.Notification(message));

        public static void Warning(string message) => Write(logger => logger.Warning(message));

        public static void Error(string message) => Write(logger => logger.Error(message));

        public static void Error(Exception e) => Write(logger => logger.Error(e));

        private static void Write(Action<ILogger> write)
        {
            try
            {
                ILogger logger = BetterModsModSystem.Logger;
                if (logger != null) write(logger);
            }
            catch (Exception)
            {
                // Nowhere to report it, and nothing worth failing over.
            }
        }
    }
}
