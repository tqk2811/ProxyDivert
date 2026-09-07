using System;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace ProxyDivert.Wpf.Services;

/// <summary>
/// Starting the tool when the user signs in.
/// </summary>
/// <remarks>
/// A scheduled task rather than the obvious <c>Run</c> registry key, because the manifest asks for
/// <c>requireAdministrator</c>: Windows silently skips a Run entry whose executable needs
/// elevation, so that arrangement never actually started anything. A logon task registered with
/// <c>HighestAvailable</c> runs elevated with no prompt at all, which is the only combination that
/// both starts and stays quiet.
/// <para>
/// Driven through <c>schtasks.exe</c>, not the Task Scheduler COM API: the XML below is the whole
/// interface either way, and the command-line tool needs no interop assembly and no marshalling of
/// a dozen COM objects to say the same thing.
/// </para>
/// </remarks>
public static class StartupRegistration
{
    /// <summary>Task name, and the value name the old Run-key implementation used.</summary>
    public const string TaskName = "ProxyDivert";

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    // schtasks is not slow, but it talks to a service; a hung call must not take the settings tab
    // down with it.
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Whether the logon task exists. Read rather than remembered, so a task the user
    /// deleted by hand shows as off instead of the checkbox insisting otherwise.</summary>
    public static bool IsEnabled()
        => RunSchTasks("/Query", "/TN", TaskName) == 0;

    /// <summary>Registers (or replaces) the logon task. Returns false if it could not be created.</summary>
    public static bool Enable()
    {
        string? xmlPath = null;
        try
        {
            xmlPath = Path.Combine(Path.GetTempPath(), $"{TaskName}-{Guid.NewGuid():N}.xml");

            // UTF-16 with a byte-order mark: schtasks /XML rejects a UTF-8 file outright, with a
            // message about the XML being "incorrectly formatted" that says nothing about encoding.
            File.WriteAllText(xmlPath, BuildTaskXml(), new UnicodeEncoding(bigEndian: false, byteOrderMark: true));

            return RunSchTasks("/Create", "/TN", TaskName, "/XML", xmlPath, "/F") == 0;
        }
        catch
        {
            // A locked-down machine is the user's environment, not an error worth a dialog. The
            // checkbox re-reads IsEnabled afterwards, so it ends up telling the truth regardless.
            return false;
        }
        finally
        {
            if (xmlPath != null)
            {
                try { File.Delete(xmlPath); } catch { }
            }
        }
    }

    /// <summary>Removes the logon task. Missing is success: the point is that it is gone.</summary>
    public static bool Disable()
        => RunSchTasks("/Delete", "/TN", TaskName, "/F") == 0 || !IsEnabled();

    /// <summary>
    /// Deletes the <c>Run</c> value left behind by the earlier attempt at this feature. Called once
    /// at startup: a machine that ticked the box before this change has a dead entry sitting in the
    /// registry, and leaving it there means the user sees the tool listed under startup apps while
    /// it never starts.
    /// </summary>
    public static void RemoveLegacyRunKey()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            key?.DeleteValue(TaskName, throwOnMissingValue: false);
        }
        catch
        {
        }
    }

    /// <summary>
    /// The task definition, exactly as it is handed to the scheduler. Public so a test can check
    /// what this tool asks for without registering anything on the machine running the test.
    /// </summary>
    public static string BuildTaskXml()
    {
        string user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name) ?? string.Empty;
        string command = SecurityElement.Escape(Environment.ProcessPath ?? string.Empty) ?? string.Empty;
        string workingDirectory = SecurityElement.Escape(AppContext.BaseDirectory.TrimEnd('\\')) ?? string.Empty;

        // Battery, idle and network conditions are all switched off deliberately: this is a network
        // filter, and every one of those defaults exists to postpone work that can wait. This
        // cannot — traffic that leaves before it starts leaves unredirected.
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Starts ProxyDivert hidden in the notification area when {user} signs in.</Description>
                <URI>\{TaskName}</URI>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{user}</UserId>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{user}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>false</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{command}</Command>
                  <Arguments>{AppArguments.MinimizedFlag}</Arguments>
                  <WorkingDirectory>{workingDirectory}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    // -1 for "could not even be run", which every caller treats the same as a non-zero exit.
    private static int RunSchTasks(params string[] arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo("schtasks.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                // Read back rather than inherited: schtasks writes to the console on both success
                // and failure, and this process has no console to write to.
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);

            using Process? process = Process.Start(startInfo);
            if (process is null) return -1;

            // Drained before waiting: a full pipe would block schtasks forever, and then us.
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();

            if (!process.WaitForExit(CommandTimeout)) return -1;
            return process.ExitCode;
        }
        catch
        {
            return -1;
        }
    }
}
