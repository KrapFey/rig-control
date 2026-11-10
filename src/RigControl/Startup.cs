using System.Diagnostics;
using System.Security;

namespace RigControl;

/// <summary>
/// "Start with Windows" as a logon scheduled task with highest privileges: the only way an admin app starts at
/// logon without a UAC prompt. The app is already elevated, so creating it needs no prompt either.
/// </summary>
static class Startup
{
    const string TaskName = "RigControl";

    public static bool IsEnabled() => Run("/query", "/tn", TaskName) == 0;

    public static void Enable()
    {
        var exe = Environment.ProcessPath!;
        var user = SecurityElement.Escape($"{Environment.UserDomainName}\\{Environment.UserName}");
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>Starts RigControl (fan and RGB control) at logon.</Description></RegistrationInfo>
              <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{user}</UserId></LogonTrigger></Triggers>
              <Principals><Principal id="Author"><UserId>{user}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>5</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{SecurityElement.Escape(exe)}</Command>
                  <Arguments>--tray</Arguments>
                  <WorkingDirectory>{SecurityElement.Escape(Path.GetDirectoryName(exe)!)}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;
        var file = Path.Combine(Path.GetTempPath(), $"rigcontrol-task-{Guid.NewGuid():N}.xml");
        File.WriteAllText(file, xml, System.Text.Encoding.Unicode);
        try
        {
            if (Run("/create", "/tn", TaskName, "/xml", file, "/f") != 0) throw new InvalidOperationException("schtasks couldn't create the task.");
        }
        finally { File.Delete(file); }
    }

    public static void Disable() => Run("/delete", "/tn", TaskName, "/f");

    static int Run(params string[] args)
    {
        var psi = new ProcessStartInfo("schtasks.exe") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit(10_000);
        return p.ExitCode;
    }
}
