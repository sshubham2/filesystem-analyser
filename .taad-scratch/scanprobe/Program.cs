using System.Diagnostics;
using FsAnalyser.Core.Scanning;

// Runs the app's own scanner (Scanner.Scan, default options) over one folder and prints what it found,
// with the System process's handle count before and after.
string root = args[0];
static int SystemHandles() { using var p = Process.GetProcessById(4); p.Refresh(); return p.HandleCount; }
int before = SystemHandles();
var sw = Stopwatch.StartNew();
ScanResult r = Scanner.Scan(root, new ScanProgress(), CancellationToken.None);
sw.Stop();
int after = SystemHandles();
Console.WriteLine($"root={root} status={r.Status} files={r.Totals.Files} folders={r.Totals.Folders} links={r.Totals.Links} unreadable={r.Unreadable.Count} ms={sw.ElapsedMilliseconds} pid4Handles {before}->{after} ({after - before:+#;-#;0})");
