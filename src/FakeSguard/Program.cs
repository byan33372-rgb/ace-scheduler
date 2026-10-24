using System.IO;
using System.Threading;
// Field-test stand-in scanner: loop "enumerate + read + hash" while the
// control file says run. Built as SGuard64.exe so TargetDiscovery adopts it.
var targetFile = args.Length > 0 ? args[0] : "target.bin";
var controlFile = args.Length > 1 ? args[1] : "sguard.ctl";
var buf = new byte[4 * 1024 * 1024];
using var sha = System.Security.Cryptography.SHA256.Create();
while (true)
{
    string cmd;
    try { cmd = File.ReadAllText(controlFile).Trim(); }
    catch { cmd = ""; }
    if (cmd != "run") { Thread.Sleep(200); continue; }
    try
    {
        using var fs = File.Open(targetFile, FileMode.Open, FileAccess.Read, FileShare.Read);
        int n;
        while ((n = fs.Read(buf, 0, buf.Length)) > 0) _ = sha.ComputeHash(buf, 0, n);
    }
    catch (IOException) { Thread.Sleep(500); }
}
