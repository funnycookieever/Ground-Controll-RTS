using System.Diagnostics;
using BepInEx.Logging;

namespace GroundControlRts;

/// <summary>
/// The mod's log. A build with <c>-p:NoLogs=true</c> leaves <c>GC_LOGS</c> undefined, and the compiler
/// then removes every call to these methods, arguments included, so the strings are never built.
/// That means an argument must never do work the game needs: it does not run in that build.
/// </summary>
internal sealed class CommanderLog
{
    private readonly ManualLogSource source;

    internal CommanderLog(ManualLogSource source)
    {
        this.source = source;
    }

    [Conditional("GC_LOGS")]
    internal void LogInfo(object data)
    {
        source.LogInfo(data);
    }

    [Conditional("GC_LOGS")]
    internal void LogWarning(object data)
    {
        source.LogWarning(data);
    }

    [Conditional("GC_LOGS")]
    internal void LogError(object data)
    {
        source.LogError(data);
    }
}
