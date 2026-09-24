// SL-024 build attempt 2: re-run of spike 2 (cloud-fetch-placeholders-cancel-not-empty-not-cloud-code)
// with the scanner's REAL listing calls in place of the FindFirstFileExW stand-in.
//
// Provider side is spike 2's harness unchanged in substance: a never-answering FETCH_PLACEHOLDERS
// provider in a SEPARATE process, CfRegisterSyncRoot flag 2 (DISABLE_ON_DEMAND_POPULATION_ON_ROOT),
// population FULL, hydration PARTIAL, directory placeholders created with flags NONE (on-demand
// population left on), unelevated. Only the provider name and id differ, so a stale spike-33
// registration cannot collide.
//
// Lister side is new. Four arms:
//   real-guarded      CreateFileW(\\?\dir, FILE_LIST_DIRECTORY, share all, OPEN_EXISTING,
//                     FILE_FLAG_BACKUP_SEMANTICS) then GetFileInformationByHandleEx with
//                     FileIdExtdDirectoryRestartInfo (20), then FileIdExtdDirectoryInfo (19), 64 KiB
//                     buffer: exactly DirectoryReader.Open (:66) and List (:96-113). Wrapped in the
//                     design's recorded-intent rule: the canceller sets the intent flag BEFORE each
//                     CancelSynchronousIo; the lister refuses a call once it is set and discards the
//                     result of any call during which it was set (Enter/Exit/Settle).
//   real-guarded-full the same with the fallback classes FileFullDirectoryRestartInfo (15) then 14.
//   real-unguarded    the same calls with today's List semantics: keep calling until 18/error.
//   scanner           FsAnalyser.Core's own public Scanner.Scan on the placeholder folder, as built
//                     from this worktree (no guard yet): what today's code does under the canceller.
// The canceller runs on the orchestrator thread (never the worker), OpenThread(THREAD_TERMINATE,
// worker tid), CancelSynchronousIo every 25 ms (the design's period) until the worker returns, or
// once only in the single-cancel arm.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using FsAnalyser.Core.Scanning;
using Microsoft.Win32.SafeHandles;

static unsafe class P
{
    // ---------- logging ----------
    static readonly Stopwatch SW = Stopwatch.StartNew();
    static readonly long StartEpochMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    static string LogPath;
    static readonly object LogLock = new object();
    static double Now { get { return SW.Elapsed.TotalSeconds; } }
    static void WriteLine(string line)
    {
        lock (LogLock)
        {
            for (int i = 0; i < 20; i++)
            {
                try { File.AppendAllText(LogPath, line + "\n"); return; } catch (IOException) { Thread.Sleep(10); }
            }
        }
    }
    static void Log(string s)
    {
        string line = "[" + Now.ToString("F3").PadLeft(8) + "] " + s;
        Console.WriteLine(line);
        WriteLine(line);
    }

    // ---------- cldapi (spike 2, unchanged) ----------
    [StructLayout(LayoutKind.Sequential)]
    struct CF_SYNC_REGISTRATION
    {
        public uint StructSize; public IntPtr ProviderName; public IntPtr ProviderVersion;
        public IntPtr SyncRootIdentity; public uint SyncRootIdentityLength;
        public IntPtr FileIdentity; public uint FileIdentityLength;
        public Guid ProviderId;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct CF_SYNC_POLICIES
    {
        public uint StructSize;
        public ushort HydrationPrimary; public ushort HydrationModifier;
        public ushort PopulationPrimary; public ushort PopulationModifier;
        public uint InSync; public uint HardLink; public uint PlaceholderManagement;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct CF_CALLBACK_REGISTRATION { public uint Type; public IntPtr Callback; }
    [StructLayout(LayoutKind.Sequential)]
    struct CF_PLACEHOLDER_CREATE_INFO
    {
        public IntPtr RelativeFileName;
        public long CreationTime, LastAccessTime, LastWriteTime, ChangeTime;
        public uint FileAttributes;
        public long FileSize;
        public IntPtr FileIdentity; public uint FileIdentityLength;
        public uint Flags; public int Result; public long CreateUsn;
    }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate void CfCallback(IntPtr info, IntPtr parameters);

    [DllImport("cldapi.dll", CharSet = CharSet.Unicode)]
    static extern int CfRegisterSyncRoot(string path, ref CF_SYNC_REGISTRATION reg, ref CF_SYNC_POLICIES pol, uint flags);
    [DllImport("cldapi.dll", CharSet = CharSet.Unicode)]
    static extern int CfUnregisterSyncRoot(string path);
    [DllImport("cldapi.dll", CharSet = CharSet.Unicode)]
    static extern int CfConnectSyncRoot(string path, CF_CALLBACK_REGISTRATION[] table, IntPtr ctx, uint flags, out long key);
    [DllImport("cldapi.dll")]
    static extern int CfDisconnectSyncRoot(long key);
    [DllImport("cldapi.dll", CharSet = CharSet.Unicode)]
    static extern int CfCreatePlaceholders(string basePath, [In, Out] CF_PLACEHOLDER_CREATE_INFO[] arr, uint count, uint flags, out uint processed);

    // ---------- kernel32: the scanner's calls, same arguments as Kernel32.cs / DirectoryReader.cs ----------
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetFileInformationByHandleEx(SafeFileHandle h, int infoClass, void* buffer, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenThread(uint access, bool inherit, uint tid);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CancelSynchronousIo(IntPtr thread);
    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")]
    static extern uint GetCurrentThreadId();

    const uint FILE_LIST_DIRECTORY = 0x0001, FILE_SHARE_ALL = 0x7, OPEN_EXISTING = 3, FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    const int BufferSize = 64 * 1024;

    // ---------- provider side: callbacks (spike 2, unchanged) ----------
    static readonly ManualResetEvent Shutdown = new ManualResetEvent(false);
    static CfCallback KeepA, KeepB;

    static void OnFetchPlaceholders(IntPtr info, IntPtr parms)
    {
        string path = ReadPath(info);
        Ev("FETCH_PLACEHOLDERS", path);
        bool sig = Shutdown.WaitOne(90000); // never answers: no CfExecute; sleeps 90 s (or until cleanup)
        Log("cb FETCH_PLACEHOLDERS handler returning (" + (sig ? "shutdown signalled" : "slept full 90 s") + ") path=" + path);
    }
    static void OnCancelFetchPlaceholders(IntPtr info, IntPtr parms)
    {
        Ev("CANCEL_FETCH_PLACEHOLDERS", ReadPath(info));
    }
    static string ReadPath(IntPtr info)
    {
        try { return Marshal.PtrToStringUni(Marshal.ReadIntPtr(info, 104)); } catch (Exception e) { return "<err " + e.Message + ">"; }
    }
    static void Ev(string kind, string path)
    {
        long ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        WriteLine("EV " + ms + " " + kind + " " + path);
        Console.WriteLine("[" + Now.ToString("F3").PadLeft(8) + "] cb " + kind + " path=" + path);
    }
    class CbEvent { public double T; public string Kind; public string Path; }
    static List<CbEvent> ReadEvents(string providerLog)
    {
        var l = new List<CbEvent>();
        for (int i = 0; i < 20; i++)
        {
            try
            {
                using (var fs = new FileStream(providerLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var sr = new StreamReader(fs))
                {
                    string line;
                    while ((line = sr.ReadLine()) != null)
                    {
                        if (!line.StartsWith("EV ")) continue;
                        var p = line.Split(new[] { ' ' }, 4);
                        if (p.Length < 4) continue;
                        l.Add(new CbEvent { T = (long.Parse(p[1]) - StartEpochMs) / 1000.0, Kind = p[2], Path = p[3] });
                    }
                }
                return l;
            }
            catch (IOException) { l.Clear(); Thread.Sleep(20); }
        }
        return l;
    }
    static List<CbEvent> EventsFor(string providerLog, string dirName, string kind)
    {
        return ReadEvents(providerLog).Where(e => e.Kind == kind && e.Path != null && e.Path.EndsWith("\\" + dirName, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    static byte[] Ident(string s) { return System.Text.Encoding.UTF8.GetBytes(s); }
    static readonly string[] Names = { "slow0", "slow1", "slow2", "slow3", "slow4", "slow5", "full1", "full2", "ung1", "scan1", "scan2", "once1" };

    static bool ProviderUp(string root, ushort pop, uint regFlags, out long key)
    {
        key = 0;
        IntPtr pName = Marshal.StringToHGlobalUni("TaadSL024Respike"), pVer = Marshal.StringToHGlobalUni("1.0");
        IntPtr pId = Marshal.AllocHGlobal(16); Marshal.Copy(Ident("root-id"), 0, pId, 7);
        var reg = new CF_SYNC_REGISTRATION
        {
            StructSize = (uint)Marshal.SizeOf<CF_SYNC_REGISTRATION>(),
            ProviderName = pName, ProviderVersion = pVer,
            SyncRootIdentity = pId, SyncRootIdentityLength = 7,
            ProviderId = new Guid("1f0c6a52-24d0-4c1e-9b7e-5d2a0c5a2402")
        };
        var pol = new CF_SYNC_POLICIES
        {
            StructSize = (uint)Marshal.SizeOf<CF_SYNC_POLICIES>(),
            HydrationPrimary = 0 /*PARTIAL*/, HydrationModifier = 0,
            PopulationPrimary = pop /*2 = FULL*/, PopulationModifier = 0,
            InSync = 0, HardLink = 0, PlaceholderManagement = 0
        };
        int hr = CfRegisterSyncRoot(root, ref reg, ref pol, regFlags);
        Log("CfRegisterSyncRoot pop=" + pop + " regFlags=" + regFlags + " hr=0x" + hr.ToString("X8"));
        if (hr < 0) return false;
        KeepA = new CfCallback(OnFetchPlaceholders);
        KeepB = new CfCallback(OnCancelFetchPlaceholders);
        var table = new[]
        {
            new CF_CALLBACK_REGISTRATION { Type = 3, Callback = Marshal.GetFunctionPointerForDelegate(KeepA) },
            new CF_CALLBACK_REGISTRATION { Type = 4, Callback = Marshal.GetFunctionPointerForDelegate(KeepB) },
            new CF_CALLBACK_REGISTRATION { Type = 0xFFFFFFFF, Callback = IntPtr.Zero },
        };
        hr = CfConnectSyncRoot(root, table, IntPtr.Zero, 4 /*REQUIRE_FULL_FILE_PATH*/, out key);
        Log("CfConnectSyncRoot hr=0x" + hr.ToString("X8") + " key=" + key);
        if (hr < 0) return false;

        var arr = new CF_PLACEHOLDER_CREATE_INFO[Names.Length];
        long ft = DateTime.UtcNow.ToFileTimeUtc();
        for (int i = 0; i < Names.Length; i++)
        {
            IntPtr pn = Marshal.StringToHGlobalUni(Names[i]);
            byte[] idb = Ident(Names[i]);
            IntPtr pi = Marshal.AllocHGlobal(16); Marshal.Copy(idb, 0, pi, idb.Length);
            arr[i] = new CF_PLACEHOLDER_CREATE_INFO
            {
                RelativeFileName = pn,
                CreationTime = ft, LastAccessTime = ft, LastWriteTime = ft, ChangeTime = ft,
                FileAttributes = 0x10, FileSize = 0,
                FileIdentity = pi, FileIdentityLength = (uint)idb.Length,
                Flags = 0 /* NONE: on-demand population left enabled */
            };
        }
        uint processed;
        hr = CfCreatePlaceholders(root, arr, (uint)arr.Length, 1 /*STOP_ON_ERROR*/, out processed);
        Log("CfCreatePlaceholders hr=0x" + hr.ToString("X8") + " processed=" + processed);
        if (hr < 0) return false;
        foreach (var n in Names)
        {
            var a = File.GetAttributes(Path.Combine(root, n));
            Log("placeholder " + n + " attrs=0x" + ((int)a).ToString("X"));
        }
        return true;
    }
    static void ProviderDown(string root, long key, bool connected, bool registered)
    {
        Shutdown.Set();
        Thread.Sleep(1500);
        int hr;
        if (connected) { hr = CfDisconnectSyncRoot(key); Log("CfDisconnectSyncRoot hr=0x" + hr.ToString("X8")); }
        if (registered) { hr = CfUnregisterSyncRoot(root); Log("CfUnregisterSyncRoot hr=0x" + hr.ToString("X8")); }
    }

    // ---------- lister side ----------
    // Stage of the worker: 0 idle, 1 in CreateFileW, 100+k in the k-th GetFileInformationByHandleEx,
    // 3 in CloseHandle, 5 inside Scanner.Scan (stage not visible).
    static volatile int Stage;
    // The design's recorded intent: set by the canceller BEFORE each CancelSynchronousIo.
    static volatile int Intent;

    class ListResult
    {
        public string Outcome = "";
        public int Win32; public string Exc;
        public int Entries, NonDot; public string Names = "";
        public double Start, End, CloseSec = -1;
        public List<string> Calls = new List<string>();
    }

    static ListResult ListReal(string dir, bool fullClass, bool guarded)
    {
        var r = new ListResult { Start = Now };
        if (guarded && Intent != 0) { r.Outcome = "refused-before-open (Settle=995)"; r.Win32 = 995; r.End = Now; return r; }
        Stage = 1;
        double t0 = Now;
        SafeFileHandle h = CreateFileW(@"\\?\" + dir, FILE_LIST_DIRECTORY, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        int openErr = h.IsInvalid ? Marshal.GetLastWin32Error() : 0;
        bool openIssued = Intent != 0;
        Stage = 0;
        r.Calls.Add("CreateFileW " + (Now - t0).ToString("F3") + "s err=" + openErr + (openIssued ? " [cancel issued during call]" : ""));
        if (h.IsInvalid)
        {
            r.Outcome = guarded && openIssued ? "aborted-open (Settle=995, os err " + openErr + ")" : "open-error";
            r.Win32 = guarded && openIssued ? 995 : openErr;
            r.End = Now;
            return r;
        }
        byte* buf = (byte*)NativeMemory.AlignedAlloc(BufferSize, 8);
        try
        {
            if (guarded && openIssued) { r.Outcome = "aborted-open (Settle=995, os returned a handle; discarded)"; r.Win32 = 995; return r; }
            int cls = fullClass ? 15 : 20;
            for (int k = 1; k <= 10000; k++)
            {
                if (guarded && Intent != 0) { r.Outcome = "refused-list#" + k + " (Settle=995)"; r.Win32 = 995; r.Entries = 0; r.NonDot = 0; r.Names = ""; break; }
                Stage = 100 + k;
                t0 = Now;
                bool ok = GetFileInformationByHandleEx(h, cls, buf, BufferSize);
                int e = ok ? 0 : Marshal.GetLastWin32Error();
                bool issued = Intent != 0;
                Stage = 0;
                int n = 0, nd = 0; string names = "";
                if (ok) Count(buf, fullClass, ref n, ref nd, ref names);
                r.Calls.Add("GFIBHEx#" + k + " class=" + cls + " " + (Now - t0).ToString("F3") + "s ok=" + ok + " err=" + e + " entries=" + n + " names=" + names + (issued ? " [cancel issued during call]" : ""));
                if (guarded && issued)
                {
                    r.Outcome = "aborted-list#" + k + " (Settle=995; os said " + (ok ? "SUCCESS entries=" + n : "err " + e) + "; discarded)";
                    r.Win32 = 995; r.Entries = 0; r.NonDot = 0; r.Names = "";
                    break;
                }
                if (!ok)
                {
                    if (e == 18) r.Outcome = "success";
                    else if (k == 1 && e == 2) r.Outcome = "success (err 2 on first call = empty)";
                    else { r.Outcome = "list-error#" + k; r.Win32 = e; }
                    break;
                }
                r.Entries += n; r.NonDot += nd; r.Names += names;
                cls = fullClass ? 14 : 19;
            }
        }
        finally
        {
            NativeMemory.AlignedFree(buf);
            Stage = 3;
            double c0 = Now;
            h.Dispose();
            r.CloseSec = Now - c0;
            Stage = 0;
            r.End = Now;
        }
        return r;
    }

    static void Count(byte* p, bool fullClass, ref int n, ref int nonDot, ref string names)
    {
        while (true)
        {
            uint next = *(uint*)p;
            int len = (int)(*(uint*)(p + 60) / 2);
            string name = new string((char*)(p + (fullClass ? 68 : 88)), 0, len);
            n++; names += name + "|";
            if (name != "." && name != "..") nonDot++;
            if (next == 0) return;
            p += next;
        }
    }

    static ListResult ListScanner(string dir)
    {
        var r = new ListResult { Start = Now };
        Stage = 5;
        try
        {
            ScanResult s = Scanner.Scan(dir, new ScanProgress(), CancellationToken.None);
            r.Outcome = "Scanner.Scan status=" + s.Status + " rootError=" + s.RootError + " unreadable=" + s.Unreadable.Count
                + (s.Unreadable.Count > 0 ? " (" + string.Join(",", s.Unreadable.Select(u => u.ToString())) + ")" : "")
                + " files=" + s.Totals.Files + " folders=" + s.Totals.Folders + " storeCount=" + s.Store.Count;
            r.Win32 = s.RootError;
        }
        catch (Exception e) { r.Outcome = "exception"; r.Exc = e.GetType().FullName + ": " + e.Message; }
        Stage = 0;
        r.End = Now;
        return r;
    }

    static string Describe(ListResult r)
    {
        string s = r.Outcome;
        if (r.Win32 != 0) s += " win32=" + r.Win32;
        if (r.Exc != null) s += " " + r.Exc;
        s += " entries=" + r.Entries + " nonDot=" + r.NonDot + " names=" + r.Names;
        if (r.CloseSec >= 0) s += " closeHandle=" + r.CloseSec.ToString("F3") + "s";
        return s;
    }

    class Trial
    {
        public string Dir, Arm; public ListResult Res; public double ThreadStart;
        public int CancelCalls, CancelTrue, CancelFalse; public int LastFalseErr;
        public double FirstCancelAt = -1, FirstTrueAt = -1;
        public bool BlockedAtCancel;
        public string StagesAtTrue = "";
    }

    static Trial RunTrial(string root, string dirName, string arm, Func<string, ListResult> lister, double cancelAfter, bool once, double maxWait)
    {
        var t = new Trial { Dir = dirName, Arm = arm };
        var started = new ManualResetEvent(false);
        var done = new ManualResetEvent(false);
        uint tid = 0;
        Intent = 0; Stage = 0;
        var th = new Thread(() =>
        {
            tid = GetCurrentThreadId();
            t.ThreadStart = Now;
            started.Set();
            try { t.Res = lister(Path.Combine(root, dirName)); }
            catch (Exception e) { t.Res = new ListResult { Outcome = "exception", Exc = "OUTER " + e, End = Now }; }
            done.Set();
        });
        th.IsBackground = true;
        th.Name = "scan-" + dirName;
        th.Start();
        started.WaitOne();
        Log("trial " + dirName + " [" + arm + "]: worker started tid=" + tid + " cancelAfter=" + cancelAfter + " s " + (once ? "ONCE" : "loop @25ms"));
        IntPtr h = OpenThread(0x0001 /*THREAD_TERMINATE*/, false, tid);
        if (h == IntPtr.Zero) Log("OpenThread failed err=" + Marshal.GetLastWin32Error());

        bool early = done.WaitOne((int)(cancelAfter * 1000));
        t.BlockedAtCancel = !early;
        Log("trial " + dirName + ": " + cancelAfter + " s elapsed, worker returned already? " + early + " stage=" + Stage);
        if (!early)
        {
            t.FirstCancelAt = Now;
            while (!done.WaitOne(0))
            {
                int stage = Stage;
                Intent = 1; // recorded before the cancel is delivered
                bool ok = CancelSynchronousIo(h);
                int err = ok ? 0 : Marshal.GetLastWin32Error();
                t.CancelCalls++;
                if (ok)
                {
                    t.CancelTrue++; t.StagesAtTrue += stage + ",";
                    if (t.FirstTrueAt < 0) { t.FirstTrueAt = Now; Log("trial " + dirName + ": CancelSynchronousIo TRUE (first), worker stage=" + stage); }
                }
                else { t.CancelFalse++; t.LastFalseErr = err; }
                if (once)
                {
                    if (!done.WaitOne((int)(maxWait * 1000)))
                    {
                        Log("trial " + dirName + ": STILL BLOCKED " + maxWait + " s after the single cancel, stage=" + Stage + "; now looping to free it");
                        once = false;
                        continue;
                    }
                    break;
                }
                if (Now - t.FirstCancelAt > maxWait) { Log("trial " + dirName + ": still blocked " + maxWait + " s after cancelling began; giving up"); break; }
                done.WaitOne(25);
            }
        }
        if (done.WaitOne(0)) th.Join();
        if (h != IntPtr.Zero) CloseHandle(h);
        if (t.Res != null)
        {
            foreach (var c in t.Res.Calls) Log("trial " + dirName + ":   call " + c);
            Log("trial " + dirName + ": RESULT " + Describe(t.Res) + " workerElapsed=" + (t.Res.End - t.ThreadStart).ToString("F3") + " s"
                + (t.FirstCancelAt >= 0 ? " sinceFirstCancel=" + (t.Res.End - t.FirstCancelAt).ToString("F3") + " s" : "")
                + " cancelCalls=" + t.CancelCalls + " true=" + t.CancelTrue + " false=" + t.CancelFalse + (t.CancelFalse > 0 ? " lastFalseErr=" + t.LastFalseErr : "")
                + " stagesAtTrue=" + t.StagesAtTrue);
        }
        else Log("trial " + dirName + ": worker NEVER RETURNED; cancelCalls=" + t.CancelCalls + " true=" + t.CancelTrue + " false=" + t.CancelFalse);
        return t;
    }

    static string Rep(string providerLog, Trial t)
    {
        var fetch = EventsFor(providerLog, t.Dir, "FETCH_PLACEHOLDERS");
        var canc = EventsFor(providerLog, t.Dir, "CANCEL_FETCH_PLACEHOLDERS");
        string res = t.Res == null ? "NO RETURN" : Describe(t.Res);
        double el = t.Res == null ? -1 : t.Res.End - t.ThreadStart;
        double since = (t.Res != null && t.FirstCancelAt >= 0) ? t.Res.End - t.FirstCancelAt : -1;
        return string.Format("{0,-6} {1,-18} blockedAtCancel={2} elapsed={3,7:F3}s sinceFirstCancel={4,7:F3}s result=[{5}] fetchCb={6}@{7} cancelCb={8}@{9} cancelCalls={10}/true={11}",
            t.Dir, t.Arm, t.BlockedAtCancel, el, since, res, fetch.Count, string.Join(",", fetch.Select(e => e.T.ToString("F3"))),
            canc.Count, string.Join(",", canc.Select(e => e.T.ToString("F3"))), t.CancelCalls, t.CancelTrue);
    }

    static int ProviderMain(string[] a)
    {
        string root = a[1]; ushort pop = ushort.Parse(a[2]); string stop = a[3], ready = a[4];
        Directory.CreateDirectory(root);
        Log("PROVIDER pid=" + Environment.ProcessId + " root=" + root);
        uint rf = a.Length > 5 ? uint.Parse(a[5]) : 2u;
        long key; bool ok = ProviderUp(root, pop, rf, out key);
        if (!ok) { File.WriteAllText(ready, "FAILED"); ProviderDown(root, key, key != 0, true); return 10; }
        File.WriteAllText(ready, "READY");
        while (!File.Exists(stop)) Thread.Sleep(100);
        Log("PROVIDER stop flag seen");
        ProviderDown(root, key, true, true);
        return 0;
    }

    static int RunMain(string[] a)
    {
        string scratch = Directory.GetCurrentDirectory();
        string providerLog = Path.Combine(scratch, "provider.log");
        string stop = Path.Combine(scratch, "stop.flag"), ready = Path.Combine(scratch, "ready.flag");
        foreach (var f in new[] { providerLog, stop, ready }) if (File.Exists(f)) File.Delete(f);
        string root = Path.Combine(scratch, "syncroot-" + Guid.NewGuid().ToString("N"));
        Log("ORCH root=" + root + " provider is a SEPARATE process, pop=2 regFlags=2; host=" + Environment.ProcessPath + " runtime=" + Environment.Version);
        Log("Core dll=" + typeof(Scanner).Assembly.Location);
        var psi = new ProcessStartInfo(Environment.ProcessPath, "\"" + Path.Combine(AppContext.BaseDirectory, "cfrespike.dll") + "\" provider \"" + root + "\" 2 \"" + stop + "\" \"" + ready + "\" 2")
        { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = scratch };
        var prov = Process.Start(psi);
        var w = Stopwatch.StartNew();
        while (!File.Exists(ready) && w.Elapsed.TotalSeconds < 30 && !prov.HasExited) Thread.Sleep(100);
        string rd = File.Exists(ready) ? File.ReadAllText(ready) : "(none)";
        Log("provider ready flag=" + rd + " exited=" + prov.HasExited);
        var summary = new List<string>();
        var trials = new List<Trial>();
        if (rd == "READY")
        {
            Func<string, ListResult> guarded = d => ListReal(d, false, true);
            Func<string, ListResult> guardedFull = d => ListReal(d, true, true);
            Func<string, ListResult> unguarded = d => ListReal(d, false, false);

            // CONTROL: the real call must still be blocked 22 s in (VOID otherwise), then the loop frees it.
            Log("=== CONTROL + LATE CANCEL: slow0, real calls, unguarded, cancel loop from 22 s ===");
            var control = RunTrial(root, "slow0", "control-late22s", unguarded, 22, false, 30);
            trials.Add(control); Thread.Sleep(3000); summary.Add(Rep(providerLog, control));

            Log("=== CANCEL ARM: slow1..slow5, real calls (class 20/19), design's guard, loop from 2 s ===");
            foreach (var n in new[] { "slow1", "slow2", "slow3", "slow4", "slow5" })
            {
                var t = RunTrial(root, n, "real-guarded", guarded, 2, false, 30);
                trials.Add(t); Thread.Sleep(3000); summary.Add(Rep(providerLog, t));
            }
            Log("=== FALLBACK CLASS: full1..full2, real calls (class 15/14), design's guard, loop from 2 s ===");
            foreach (var n in new[] { "full1", "full2" })
            {
                var t = RunTrial(root, n, "real-guarded-full", guardedFull, 2, false, 30);
                trials.Add(t); Thread.Sleep(3000); summary.Add(Rep(providerLog, t));
            }
            Log("=== TODAY'S LIST SEMANTICS: ung1, real calls, no guard (keeps calling until 18), loop from 2 s ===");
            {
                var t = RunTrial(root, "ung1", "real-unguarded", unguarded, 2, false, 30);
                trials.Add(t); Thread.Sleep(3000); summary.Add(Rep(providerLog, t));
            }
            Log("=== TODAY'S SCANNER: scan1..scan2, FsAnalyser.Core Scanner.Scan (no guard yet), loop from 2 s ===");
            foreach (var n in new[] { "scan1", "scan2" })
            {
                var t = RunTrial(root, n, "scanner", ListScanner, 2, false, 30);
                trials.Add(t); Thread.Sleep(3000); summary.Add(Rep(providerLog, t));
            }
            Log("=== SINGLE CANCEL: once1, real calls, design's guard, ONE cancel at 2 s, 10 s wait ===");
            {
                var t = RunTrial(root, "once1", "real-guarded-once", guarded, 2, true, 10);
                trials.Add(t); Thread.Sleep(3000); summary.Add(Rep(providerLog, t));
            }
        }
        Log("=== CLEANUP ===");
        File.WriteAllText(stop, "stop");
        if (!prov.WaitForExit(30000)) { Log("provider did not exit; killing"); prov.Kill(true); }
        Log("provider exit code=" + (prov.HasExited ? prov.ExitCode.ToString() : "?"));
        try { Directory.Delete(root, true); Log("sync root folder deleted: " + !Directory.Exists(root)); }
        catch (Exception e) { Log("sync root folder delete failed: " + e.Message); }

        Log("---- SUMMARY ----");
        foreach (var s in summary) Log(s);

        // ---- VERDICT against the rebuild order ----
        var c = trials.FirstOrDefault(x => x.Arm.StartsWith("control"));
        bool valid = c != null && c.BlockedAtCancel;
        Log("VALIDITY: control blocked >= 22 s before its cancel: " + valid + (valid ? "" : " -> VOID"));
        var gated = trials.Where(x => x.Arm == "real-guarded" || x.Arm == "real-guarded-full").ToList();
        foreach (var x in gated)
        {
            double since = x.Res == null ? double.PositiveInfinity : x.Res.End - x.FirstCancelAt;
            bool pass = x.BlockedAtCancel && x.Res != null && since <= 2.0 && x.Res.Win32 == 995;
            Log("GATE " + x.Dir + " [" + x.Arm + "]: blockedAtCancel=" + x.BlockedAtCancel + " returnedWithin2s=" + (since <= 2.0) + " (" + since.ToString("F3") + " s) settledTo995=" + (x.Res != null && x.Res.Win32 == 995) + " closeHandle=" + (x.Res == null ? "n/a" : x.Res.CloseSec.ToString("F3") + " s") + " -> " + (pass ? "PASS" : "FAIL"));
        }
        bool go = valid && gated.Count > 0 && gated.All(x => x.BlockedAtCancel && x.Res != null && x.Res.End - x.FirstCancelAt <= 2.0 && x.Res.Win32 == 995);
        Log("VERDICT: " + (valid ? (go ? "GO" : "NO-GO") : "VOID"));
        return 0;
    }

    static int Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0] : "run";
        string scratch = Directory.GetCurrentDirectory();
        LogPath = Path.Combine(scratch, mode == "provider" ? "provider.log" : mode + ".log");
        if (mode != "provider" && File.Exists(LogPath)) File.Delete(LogPath);
        Log("mode=" + mode + " OS=" + Environment.OSVersion + " user=" + Environment.UserName + " elevated=" + new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator));
        try
        {
            if (mode == "provider") return ProviderMain(args);
            return RunMain(args);
        }
        catch (Exception e) { Log("EXCEPTION " + e); return 99; }
    }
}
