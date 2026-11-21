using System.Diagnostics;
using System.Security;
using System.Text;
using MixerLive.Core.Logging;

namespace MixerLive.Core.Startup;

/// <summary>
/// Registers MixerLive as a Task Scheduler logon task for the current user.
///
/// Why not the Run registry key or the Startup folder? Windows 11 silently disables those
/// through Settings → Apps → Startup (a common reason Voicemeeter's own startup shortcut stops
/// working after an update). A logon task is not subject to that switch, can be delayed a few
/// seconds so audio devices are enumerated first, runs at normal priority and has no time limit.
/// </summary>
public static class StartupTask
{
    public const string TaskName = "MixerLive";

    public static bool IsInstalled() => IsInstalled(TaskName);

    public static bool IsInstalled(string taskName)
    {
        var (code, _, _) = RunSchtasks("/Query", "/TN", taskName);
        return code == 0;
    }

    public static void Install(string exePath, string arguments = "--minimized", int delaySeconds = 8)
        => Install(TaskName, exePath, arguments, delaySeconds);

    public static void Install(string taskName, string exePath, string arguments, int delaySeconds)
    {
        string user = $"{Environment.UserDomainName}\\{Environment.UserName}";
        string workDir = Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory;
        string xml = BuildXml(taskName, user, exePath, arguments, workDir, delaySeconds);

        string tmp = Path.Combine(Path.GetTempPath(), $"mixerlive-task-{Guid.NewGuid():N}.xml");
        // schtasks expects UTF-16LE with BOM for /XML.
        File.WriteAllText(tmp, xml, new UnicodeEncoding(false, true));
        try
        {
            var (code, stdout, stderr) = RunSchtasks("/Create", "/TN", taskName, "/XML", tmp, "/F");
            if (code != 0)
                throw new InvalidOperationException($"schtasks exited with {code}: {(stderr + stdout).Trim()}");
            Log.Info($"Logon task '{taskName}' registered for {user}: \"{exePath}\" {arguments}");
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* ignore */ }
        }
    }

    public static void Uninstall() => Uninstall(TaskName);

    public static void Uninstall(string taskName)
    {
        var (code, stdout, stderr) = RunSchtasks("/Delete", "/TN", taskName, "/F");
        if (code != 0 && IsInstalled(taskName))
            throw new InvalidOperationException($"schtasks exited with {code}: {(stderr + stdout).Trim()}");
        Log.Info($"Logon task '{taskName}' removed");
    }

    private static (int Code, string StdOut, string StdErr) RunSchtasks(params string[] args)
    {
        var psi = new ProcessStartInfo("schtasks.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start schtasks.exe");
        string so = p.StandardOutput.ReadToEnd();
        string se = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, so, se);
    }

    private static string BuildXml(string taskName, string user, string exe, string args, string workDir, int delaySeconds)
    {
        string u = SecurityElement.Escape(user);
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Starts the MixerLive audio mixer when you sign in.</Description>
                <URI>\{SecurityElement.Escape(taskName)}</URI>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{u}</UserId>
                  <Delay>PT{delaySeconds}S</Delay>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{u}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>LeastPrivilege</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>false</AllowHardTerminate>
                <StartWhenAvailable>true</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <DisallowStartOnRemoteAppSession>false</DisallowStartOnRemoteAppSession>
                <UseUnifiedSchedulingEngine>true</UseUnifiedSchedulingEngine>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>4</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{SecurityElement.Escape(exe)}</Command>
                  <Arguments>{SecurityElement.Escape(args)}</Arguments>
                  <WorkingDirectory>{SecurityElement.Escape(workDir)}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;
    }
}
