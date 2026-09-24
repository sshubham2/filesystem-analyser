// SL-024 build attempt 3, precheck for O-018: can the Windows SMB client on this PC reach an SMBLibrary
// server on 127.0.0.1 on a port other than 445, through a non-persistent New-SmbMapping -TcpPort, with
// SMB signing as Windows 11 requires by default? Unelevated, no Defender change.
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using SMBLibrary;
using SMBLibrary.Authentication.GSSAPI;
using SMBLibrary.Authentication.NTLM;
using SMBLibrary.Server;
using SMBLibrary.Win32;

const string User = "fsatest";
const string Password = "Fsa-test-2026!";
const string Share = "fsa";

string log = Path.Combine(AppContext.BaseDirectory, "precheck.log");
File.Delete(log);
void Log(string s) { string l = $"[{DateTime.Now:HH:mm:ss.fff}] {s}"; Console.WriteLine(l); File.AppendAllText(log, l + Environment.NewLine); }

Log($"OS {Environment.OSVersion}, elevated={new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator)}");
string root = Path.Combine(Path.GetTempPath(), "taad-sl024-smb-share-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(Path.Combine(root, "a"));
File.WriteAllText(Path.Combine(root, "a", "one.txt"), "hello");
File.WriteAllText(Path.Combine(root, "top.txt"), "top");

int port = FreePort();
var shares = new SMBShareCollection();
shares.Add(new FileSystemShare(Share, new NTDirectoryFileSystem(root)));
var auth = new IndependentNTLMAuthenticationProvider(name => string.Equals(name, User, StringComparison.OrdinalIgnoreCase) ? Password : null!);
var server = new SMBServer(shares, new GSSProvider(auth));
server.LogEntryAdded += (_, e) => Log($"server {e.Severity}: {e.Message}");
MethodInfo start = typeof(SMBServer).GetMethod("Start", BindingFlags.NonPublic | BindingFlags.Instance,
    [typeof(IPAddress), typeof(SMBTransportType), typeof(int), typeof(bool), typeof(bool), typeof(bool), typeof(TimeSpan?)])!;
start.Invoke(server, [IPAddress.Loopback, SMBTransportType.DirectTCPTransport, port, false, true, true, null]);
Log($"server listening on 127.0.0.1:{port}, share {Share} -> {root}");

string remote = $@"\\127.0.0.1\{Share}";
int exit = 0;
try
{
    exit = Ps($"New-SmbMapping -RemotePath '{remote}' -TcpPort {port} -UserName '{User}' -Password '{Password}' -Persistent $false | Format-List *");
    Ps("Get-SmbConnection | Format-List *");
    try
    {
        var sw = Stopwatch.StartNew();
        string[] entries = Directory.GetFileSystemEntries(remote, "*", SearchOption.AllDirectories);
        Log($"listed {remote} in {sw.ElapsedMilliseconds} ms: {string.Join(", ", entries)}");
    }
    catch (Exception e) { Log($"listing {remote} FAILED: {e.GetType().Name} 0x{e.HResult:X8} {e.Message}"); }
}
finally
{
    Ps($"Remove-SmbMapping -RemotePath '{remote}' -Force -UpdateProfile -Confirm:$false; Get-SmbMapping | Format-Table -AutoSize");
    server.Stop();
    Directory.Delete(root, true);
    Log("server stopped, share folder deleted");
}
return exit;

int Ps(string command)
{
    var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
    foreach (string a in new[] { "-NoProfile", "-NonInteractive", "-Command", command })
        psi.ArgumentList.Add(a);
    var sw = Stopwatch.StartNew();
    using Process p = Process.Start(psi)!;
    string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
    p.WaitForExit();
    Log($"PS> {command.Replace(Password, "***")}\n    exit {p.ExitCode} in {sw.ElapsedMilliseconds} ms\n{output.Trim()}");
    return p.ExitCode;
}

static int FreePort()
{
    var l = new TcpListener(IPAddress.Loopback, 0);
    l.Start();
    int p = ((IPEndPoint)l.LocalEndpoint).Port;
    l.Stop();
    return p;
}
