using System.ComponentModel;
using System.Diagnostics;

namespace VibeRails.Utils;

/// <summary>Filters only known, caught process-inspection probes, never arbitrary access denial.</summary>
internal static class FirstChanceExceptionPolicy
{
    public static bool IsExpectedProcessInspection(Exception exception) =>
        exception is Win32Exception { NativeErrorCode: 5 } &&
        // At first chance the exception's saved stack contains only its throw site. The
        // live stack still contains the inspection caller which will catch this exception.
        IsRuntimeTreeInspection(new StackTrace(1, fNeedFileInfo: false).ToString());

    internal static bool IsRuntimeTreeInspection(string? stack) => stack is not null &&
        (stack.Contains("at System.Diagnostics.Process.GetProcessHandlePairs(", StringComparison.Ordinal) ||
         stack.Contains("at System.Diagnostics.Process.GetChildProcesses(", StringComparison.Ordinal) ||
         stack.Contains("at System.Diagnostics.Process.IsParentOf(", StringComparison.Ordinal));
}
