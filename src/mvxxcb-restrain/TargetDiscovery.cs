using System.Diagnostics;

namespace MvxxcbRestrain;

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

    /// <summary>All running scanner processes (ACE runs several at once).</summary>
    public static List<Process> FindScanners()
    {
        var found = new List<Process>();
        var seen = new HashSet<int>();
        foreach (var name in ScannerProcessNames)
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                if (seen.Add(p.Id)) found.Add(p);
                else p.Dispose();
            }
        }
        return found;
    }
}
