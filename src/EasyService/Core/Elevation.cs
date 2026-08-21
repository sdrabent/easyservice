using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace EasyService.Core;

/// <summary>
/// Who is allowed to do what.
///
/// The application manifest asks for asInvoker, not requireAdministrator, because most of
/// what EasyService does is reading: the monitoring commands read HKLM and the state files
/// under ProgramData, both of which any account may read. Only creating, changing and
/// removing a service needs elevation, and only those paths ask for it.
///
/// The alternative - demanding elevation for the whole process - makes a monitoring agent
/// running under a restricted account useless and turns "easyservice --version" into a UAC
/// prompt.
/// </summary>
public static class Elevation
{
    /// <summary>Exit code for a command that was refused because the process is not elevated.</summary>
    public const int ExitCodeRequired = 5;

    private static readonly Lazy<bool> Elevated = new(() =>
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;   // no token to ask: treat as not elevated and let the call fail loudly
        }
    });

    public static bool IsElevated => Elevated.Value;

    /// <summary>
    /// Starts the same executable again through the shell with the runas verb, which is what
    /// raises the UAC prompt. Returns false when the user declines it.
    /// </summary>
    public static bool RelaunchAsAdmin(IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo
        {
            FileName = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "easyservice.exe",
            UseShellExecute = true,
            Verb = "runas",
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);

        try
        {
            Process.Start(info);
            return true;
        }
        catch (Win32Exception)
        {
            return false;   // ERROR_CANCELLED: the prompt was dismissed
        }
    }
}
