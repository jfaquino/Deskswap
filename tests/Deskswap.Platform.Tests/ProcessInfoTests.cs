using System.Diagnostics;

namespace Deskswap.Platform.Tests;

/// <summary>Design §6.4: windows of apps at a higher integrity level are left alone.</summary>
public class ProcessInfoTests
{
    [Fact]
    public void Own_process_is_not_more_trusted_than_itself()
    {
        Assert.False(ProcessInfo.IsMoreTrustedThanUs((uint)Environment.ProcessId));
        Assert.True(ProcessInfo.CurrentIntegrityLevel > 0);
    }

    [Fact]
    public void A_system_process_is_more_trusted_unless_tests_run_as_system()
    {
        var lsass = Process.GetProcessesByName("lsass").FirstOrDefault();
        Assert.SkipWhen(lsass is null, "lsass is not visible on this machine.");
        Assert.SkipWhen(ProcessInfo.CurrentIntegrityLevel >= 0x4000, "Tests run at system integrity.");

        Assert.True(ProcessInfo.IsMoreTrustedThanUs((uint)lsass!.Id));
    }

    [Fact]
    public void Start_time_is_stable_for_a_process()
    {
        Assert.True(ProcessInfo.TryGetStartTicks((uint)Environment.ProcessId, out long first));
        Assert.True(ProcessInfo.TryGetStartTicks((uint)Environment.ProcessId, out long second));
        Assert.Equal(first, second);
    }
}
