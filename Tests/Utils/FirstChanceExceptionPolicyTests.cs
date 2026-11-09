using System.ComponentModel;
using VibeRails.Utils;
using Xunit;

namespace Tests.Utils;

public sealed class FirstChanceExceptionPolicyTests
{
    [Theory]
    [InlineData("at System.Diagnostics.Process.GetProcessHandlePairs(Func`3 filter)", true)]
    [InlineData("at System.Diagnostics.Process.GetChildProcesses(Process[] processes)", true)]
    [InlineData("at System.Diagnostics.Process.IsParentOf(SafeProcessHandle parent)", true)]
    [InlineData("at System.Diagnostics.Process.StartCore(ProcessStartInfo info)", false)]
    [InlineData("at System.Diagnostics.Process.Kill()", false)]
    public void FilterOnlyRecognizesRuntimeInspectionFrames(string stack, bool expected) =>
        Assert.Equal(expected, FirstChanceExceptionPolicy.IsRuntimeTreeInspection(stack));

    [Fact]
    public void ArbitraryAccessDenialAndNativeCrashesAreNotSuppressed()
    {
        Assert.False(FirstChanceExceptionPolicy.IsExpectedProcessInspection(new Win32Exception(5)));
        Assert.False(FirstChanceExceptionPolicy.IsExpectedProcessInspection(new AccessViolationException()));
    }
}
