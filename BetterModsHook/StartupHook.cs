using System;
using BetterMods.Hook;

/// <summary>
/// .NET startup hook entry point for BetterMods. Optional, and entirely separate from the mod:
/// pointing <c>DOTNET_STARTUP_HOOKS</c> at this assembly makes BetterMods' main-menu patches apply
/// on a cold launch, instead of only after the player has entered and left a world once.
/// <para>
/// THE CONTRACT IS FIXED BY THE .NET HOST AND MUST NOT CHANGE. <c>System.StartupHookProvider</c>
/// does <c>assembly.GetType("StartupHook", throwOnError: true)</c> — so the type must be named
/// exactly <c>StartupHook</c> and live in the <b>global namespace</b> — and then looks for a
/// parameterless static <c>Initialize</c> returning <c>void</c>, rejecting anything else with
/// "The signature of the startup hook '{0}' in assembly '{1}' was invalid. It must be
/// 'public static void Initialize()'." (string verified in the installed
/// System.Private.CoreLib for .NET 10). Every one of those failures is fatal: the host tears the
/// process down before <c>Main</c>, i.e. the game does not start at all.
/// </para>
/// <para>
/// For the same reason <see cref="Initialize"/> must never throw. An exception here is an
/// unlaunchable game, so everything is wrapped and the worst case is a line in the log file.
/// </para>
/// </summary>
internal static class StartupHook
{
    /// <summary>
    /// Runs before <c>ClientProgram.Main</c>. The game is not loaded yet — not even
    /// <c>AssemblyResolver.AssemblyResolve</c> is registered, that is the first statement of
    /// <c>Main</c> — so this does the one thing that is safe here and nothing else: subscribe to
    /// <see cref="AppDomain.AssemblyLoad"/> and wait for the game to come up.
    /// </summary>
    public static void Initialize()
    {
        try
        {
            HookBootstrap.Install();
        }
        catch (Exception e)
        {
            // Never rethrow: a throwing startup hook is a process that will not start.
            HookLog.Write("Startup hook failed to install.", e);
        }
    }
}
