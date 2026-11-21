using MixerLive.Core.Startup;
using Xunit;

namespace MixerLive.Tests;

public class StartupTaskTests
{
    /// <summary>
    /// Opt-in integration test (set MIXERLIVE_TASK_TEST=1): registers a throw-away logon task
    /// through schtasks and removes it again, proving the task XML is accepted by this Windows build.
    /// </summary>
    [Fact]
    public void Can_register_and_remove_a_logon_task()
    {
        if (Environment.GetEnvironmentVariable("MIXERLIVE_TASK_TEST") != "1") return;

        const string name = "MixerLive (unit test)";
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("no process path");
        try
        {
            StartupTask.Install(name, exe, "--minimized", 5);
            Assert.True(StartupTask.IsInstalled(name));
        }
        finally
        {
            StartupTask.Uninstall(name);
        }
        Assert.False(StartupTask.IsInstalled(name));
    }
}
