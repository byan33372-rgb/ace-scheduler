using System.Diagnostics;
using System.Management;

namespace AceScheduler;

/// <summary>
/// ACE is identified by stable invariants, never a game list: service names
/// AntiCheatExpert*, scanner processes SGuard64/SGuardSvc64/ACE-Service64.
/// The SCM event 7045 ("a service was installed in the system", service name
/// AntiCheatExpert*) fires at every ACE game launch - it is the intended
/// zero-residency trigger for the installer variant (docs/design.md).
/// </summary>
public static class TargetDiscovery
{
    public static readonly string[] ScannerProcessNames =
    {
        "SGuard64",
        "SGuardSvc64",
        "ACE-Service64",
    };

    /// <summary>First running scanner process, or null when ACE is dormant.</summary>
    public static Process? FindScanner()
    {
        foreach (var name in ScannerProcessNames)
        {
            var processes = Process.GetProcessesByName(name);
            if (processes.Length > 0) return processes[0];
            foreach (var p in processes) p.Dispose();
        }
        return null;
    }

    /// <summary>True while any AntiCheatExpert* service is registered (installed even when stopped).</summary>
    public static bool AceServiceInstalled()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name FROM Win32_Service WHERE Name LIKE 'AntiCheatExpert%'");
            foreach (var o in searcher.Get())
            {
                return true;
            }
        }
        catch
        {
            // WMI unavailable - fall back to process discovery only
        }
        return false;
    }
}
