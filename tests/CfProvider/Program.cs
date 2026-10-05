using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace CfProvider;

/// <summary>
/// A cloud-files provider that never answers (SL-049). Given <c>&lt;stall|tree&gt; &lt;absoluteRoot&gt;</c> it
/// registers <c>absoluteRoot</c> as a sync root (population FULL, hydration PARTIAL, hardlinks ALLOWED, on-demand
/// population disabled on the root), connects, creates the scenario's placeholders and prints READY. It never
/// calls CfExecute, so every FETCH_PLACEHOLDERS and FETCH_DATA it is sent stays pending until cldflt gives up on
/// it. It exits when its stdin reaches end of file, and tears nothing down: killing it, unregistering the root
/// and deleting the folder are the test's job.
/// </summary>
/// <remarks>
/// <para>stdout, one flushed UTF-8 line per event: <c>REGISTERED</c> once the root is registered, <c>READY</c>
/// once setup is done, <c>CB &lt;TYPE&gt; pid=&lt;n&gt; path=&lt;full path&gt;</c> for each callback, with
/// <c> offset=&lt;o&gt; length=&lt;l&gt;</c> after a FETCH_DATA's path, and <c>ERR &lt;api&gt; hr=0x%08X</c>
/// from the first call that fails, after which it exits 2.</para>
/// <para>The scenario <c>silent</c> registers nothing and prints nothing on stdout until its stdin closes: the
/// shape of a provider that hangs before READY, for the test that the rig kills it and fails.</para>
/// <para><c>stop-seam &lt;absoluteRoot&gt; [NTSTATUS]</c> (SL-089, the window case N-006) is the one scenario that calls
/// CfExecute: it holds every FETCH_PLACEHOLDERS, logging each as <c>FETCH &lt;name&gt; pid=&lt;n&gt; utc=&lt;time&gt; held</c>,
/// until a line <c>release</c> on stdin, and then completes each with that failure status (STATUS_CLOUD_FILE_UNSUCCESSFUL
/// by default), and every later one at once, logging <c>failed status=0x.. hr=0x..</c> (see CreateStopSeam). It exits
/// when stdin ends. <c>unregister</c>, <c>probe</c> and <c>delete</c>, each with the root, are the case's teardown, run as
/// processes of their own once the provider is gone (see Teardown).</para>
/// <para>The structures and policies are the ones the SL-024 re-spike measured working
/// (.taad/slices/SL-024/build-slice-2-respike/Program.cs), with the requesting process read from
/// CF_CALLBACK_INFO.ProcessInfo (connect flag REQUIRE_PROCESS_INFO), as SL-049's spike did.</para>
/// </remarks>
internal static unsafe class Program
{
    private const int ExitError = 2;
    private const int E_INVALIDARG = unchecked((int)0x80070057);

    private const string ProviderName = "FsAnalyserCfTest";
    private static readonly Guid ProviderId = new("6f3b2c1e-9d47-4a58-b0e1-5c2a0f049a49");

    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;
    private const long MiB = 1024 * 1024;

    private const ushort CF_HYDRATION_POLICY_PARTIAL = 0;
    private const ushort CF_POPULATION_POLICY_FULL = 2;
    private const uint CF_HARDLINK_POLICY_ALLOWED = 1;
    private const uint CF_REGISTER_FLAG_DISABLE_ON_DEMAND_POPULATION_ON_ROOT = 2;
    private const uint CF_CONNECT_FLAG_REQUIRE_PROCESS_INFO = 2;
    private const uint CF_CONNECT_FLAG_REQUIRE_FULL_FILE_PATH = 4;
    private const uint CF_PLACEHOLDER_CREATE_FLAG_NONE = 0;
    private const uint CF_PLACEHOLDER_CREATE_FLAG_DISABLE_ON_DEMAND_POPULATION = 1;
    private const uint CF_CREATE_FLAG_STOP_ON_ERROR = 1;

    private const uint CF_CALLBACK_TYPE_FETCH_DATA = 0;
    private const uint CF_CALLBACK_TYPE_CANCEL_FETCH_DATA = 2;
    private const uint CF_CALLBACK_TYPE_FETCH_PLACEHOLDERS = 3;
    private const uint CF_CALLBACK_TYPE_CANCEL_FETCH_PLACEHOLDERS = 4;
    private const uint CF_CALLBACK_TYPE_NONE = 0xFFFFFFFF;

    private static readonly object Gate = new();
    private static readonly StreamWriter Out = new(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };

    private static int Main(string[] args)
    {
        if (args.Length is not (2 or 3) || args[0] is not ("stall" or "tree" or "silent" or "stop-seam" or "unregister" or "probe" or "delete")
            || !Path.IsPathFullyQualified(args[1]) || (args.Length == 3 && (args[0] != "stop-seam" || !TryParseStatus(args[2], out _))))
        {
            Line($"ERR usage hr=0x{E_INVALIDARG:X8} expected <stall|tree|silent|stop-seam|unregister|probe|delete> <absoluteRoot> [failure NTSTATUS, stop-seam only], got: {string.Join(' ', args)}");
            return ExitError;
        }

        string scenario = args[0];
        string root = Path.TrimEndingDirectorySeparator(args[1]);
        if (scenario is "unregister" or "probe" or "delete")
            return Teardown(scenario, root);
        _seam = scenario == "stop-seam";
        if (args.Length == 3)
            _failStatus = ParseStatus(args[2]);
        if (scenario == "silent")
        {
            Console.Error.WriteLine("silent: registering nothing and printing nothing on stdout until stdin closes");
            Console.In.ReadToEnd();
            return 0;
        }

        try
        {
            Register(root);
            Line("REGISTERED");
            Connect(root);
            if (scenario == "stall")
                CreateStall(root);
            else if (_seam)
                CreateStopSeam(root);
            else
                CreateTree(root);
        }
        catch (ProviderError e)
        {
            Line($"ERR {e.Api} hr=0x{e.Hr:X8}");
            return ExitError;
        }

        Line("READY");
        if (_seam)
        {
            // One command a line: 'release' fails every fetch still held, and every later one, with the failure status.
            while (Console.In.ReadLine() is { } command)
            {
                if (command.Trim() == "release")
                    Release();
            }
            return 0;
        }
        // End of file: the test closed our stdin, or the process that started us is gone.
        Console.In.ReadToEnd();
        return 0;
    }

    // 'ok' lists before 'stall' in NTFS name order, so a depth-first scan reads ok's three files, then blocks
    // in stall's listing: stall keeps on-demand population, and its FETCH_PLACEHOLDERS is never answered.
    private static void CreateStall(string root)
    {
        CreatePlaceholders(root,
            NewFolder("ok", populationDisabled: true),
            NewFolder("stall", populationDisabled: false));
        CreatePlaceholders(Path.Combine(root, "ok"),
            NewFile("f1.txt", 1_000),
            NewFile("f2.txt", 2_000),
            NewFile("f3.txt", 3_000));
    }

    // stop-seam (SL-089, the window case N-006): a and b populated, s, t and u not. a holds one file and is denied to the
    // scan by the case, with an NTFS ACE, once READY is printed. b holds BChildren dehydrated files whose names are
    // BNameLength characters, so the scanner's 64 KiB listing buffer takes about 355 of them a page and its third page is
    // a full one, which the listing failpoint b|3|59 fails (review m5). s, t and u keep on-demand population, so listing
    // any of them asks the provider for its children: those requests are held, each logged as it comes, until 'release'
    // on stdin fails them, and every one after that is failed at once (Fetched, Release).
    private const int BChildren = 1_200;
    private const int BNameLength = 46;

    private static void CreateStopSeam(string root)
    {
        CreatePlaceholders(root,
            NewFolder("a", populationDisabled: true),
            NewFolder("b", populationDisabled: true),
            NewFolder("s", populationDisabled: false),
            NewFolder("t", populationDisabled: false),
            NewFolder("u", populationDisabled: false));
        CreatePlaceholders(Path.Combine(root, "a"), NewFile("one.bin", 100));
        var children = new Placeholder[BChildren];
        for (int i = 0; i < children.Length; i++)
        {
            string name = $"b-{i:D4}-";
            children[i] = NewFile(name + new string('n', BNameLength - name.Length - 4) + ".bin", 100);
        }
        CreatePlaceholders(Path.Combine(root, "b"), children);
    }

    // Every folder has on-demand population disabled, so listing the tree raises no callback. Every file is a
    // dehydrated placeholder of the declared size; b\f4-link.bin is a second name for b\f4.bin.
    private static void CreateTree(string root)
    {
        CreatePlaceholders(root,
            NewFolder("a", populationDisabled: true),
            NewFolder("b", populationDisabled: true));
        CreatePlaceholders(Path.Combine(root, "a"),
            NewFile("f1.bin", 5 * MiB),
            NewFile("f2.bin", 12_345),
            NewFolder("sub", populationDisabled: true));
        CreatePlaceholders(Path.Combine(root, "a", "sub"),
            NewFile("f3.bin", 3 * MiB));
        CreatePlaceholders(Path.Combine(root, "b"),
            NewFile("f4.bin", 1 * MiB));

        string link = Path.Combine(root, "b", "f4-link.bin");
        if (!CreateHardLink(link, Path.Combine(root, "b", "f4.bin"), IntPtr.Zero))
            throw new ProviderError("CreateHardLinkW", HResultFromWin32(Marshal.GetLastPInvokeError()));
    }

    private static void Register(string root)
    {
        byte[] identity = Encoding.UTF8.GetBytes("fsa-cf-root");
        fixed (char* name = ProviderName)
        fixed (char* version = "1.0")
        fixed (byte* id = identity)
        {
            var registration = new CF_SYNC_REGISTRATION
            {
                StructSize = (uint)sizeof(CF_SYNC_REGISTRATION),
                ProviderName = name,
                ProviderVersion = version,
                SyncRootIdentity = id,
                SyncRootIdentityLength = (uint)identity.Length,
                ProviderId = ProviderId,
            };
            var policies = new CF_SYNC_POLICIES
            {
                StructSize = (uint)sizeof(CF_SYNC_POLICIES),
                HydrationPrimary = CF_HYDRATION_POLICY_PARTIAL,
                PopulationPrimary = CF_POPULATION_POLICY_FULL,
                HardLink = CF_HARDLINK_POLICY_ALLOWED,
            };
            Check("CfRegisterSyncRoot",
                CfRegisterSyncRoot(root, &registration, &policies, CF_REGISTER_FLAG_DISABLE_ON_DEMAND_POPULATION_ON_ROOT));
        }
    }

    private static void Connect(string root)
    {
        CF_CALLBACK_REGISTRATION* table = stackalloc CF_CALLBACK_REGISTRATION[5];
        table[0] = new CF_CALLBACK_REGISTRATION { Type = CF_CALLBACK_TYPE_FETCH_DATA, Callback = &OnFetchData };
        table[1] = new CF_CALLBACK_REGISTRATION { Type = CF_CALLBACK_TYPE_CANCEL_FETCH_DATA, Callback = &OnCancelFetchData };
        table[2] = new CF_CALLBACK_REGISTRATION { Type = CF_CALLBACK_TYPE_FETCH_PLACEHOLDERS, Callback = &OnFetchPlaceholders };
        table[3] = new CF_CALLBACK_REGISTRATION { Type = CF_CALLBACK_TYPE_CANCEL_FETCH_PLACEHOLDERS, Callback = &OnCancelFetchPlaceholders };
        table[4] = new CF_CALLBACK_REGISTRATION { Type = CF_CALLBACK_TYPE_NONE, Callback = null };
        long key;
        Check("CfConnectSyncRoot", CfConnectSyncRoot(root, table, null,
            CF_CONNECT_FLAG_REQUIRE_PROCESS_INFO | CF_CONNECT_FLAG_REQUIRE_FULL_FILE_PATH, &key));
    }

    private readonly record struct Placeholder(string Name, bool IsFolder, long Size, bool PopulationDisabled);

    private static Placeholder NewFolder(string name, bool populationDisabled) => new(name, true, 0, populationDisabled);

    private static Placeholder NewFile(string name, long size) => new(name, false, size, false);

    private static void CreatePlaceholders(string folder, params Placeholder[] placeholders)
    {
        var info = new CF_PLACEHOLDER_CREATE_INFO[placeholders.Length];
        var allocated = new List<IntPtr>();
        try
        {
            long now = DateTime.UtcNow.ToFileTimeUtc();
            for (int i = 0; i < placeholders.Length; i++)
            {
                Placeholder p = placeholders[i];
                IntPtr name = Marshal.StringToHGlobalUni(p.Name);
                allocated.Add(name);
                byte[] identity = Encoding.UTF8.GetBytes(Path.Combine(folder, p.Name));
                IntPtr id = Marshal.AllocHGlobal(identity.Length);
                allocated.Add(id);
                Marshal.Copy(identity, 0, id, identity.Length);
                info[i] = new CF_PLACEHOLDER_CREATE_INFO
                {
                    RelativeFileName = (char*)name,
                    CreationTime = now,
                    LastAccessTime = now,
                    LastWriteTime = now,
                    ChangeTime = now,
                    FileAttributes = p.IsFolder ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL,
                    FileSize = p.Size,
                    FileIdentity = (void*)id,
                    FileIdentityLength = (uint)identity.Length,
                    Flags = p.PopulationDisabled ? CF_PLACEHOLDER_CREATE_FLAG_DISABLE_ON_DEMAND_POPULATION : CF_PLACEHOLDER_CREATE_FLAG_NONE,
                };
            }

            uint processed;
            fixed (CF_PLACEHOLDER_CREATE_INFO* array = info)
                Check($"CfCreatePlaceholders({folder})", CfCreatePlaceholders(folder, array, (uint)info.Length, CF_CREATE_FLAG_STOP_ON_ERROR, &processed));
            for (int i = 0; i < info.Length; i++)
                Check($"CfCreatePlaceholders({Path.Combine(folder, placeholders[i].Name)})", info[i].Result);
        }
        finally
        {
            foreach (IntPtr block in allocated)
                Marshal.FreeHGlobal(block);
        }
    }

    // The callbacks only report; none answers. A callback must not throw into cldflt, so each swallows
    // whatever reporting it fails.

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void OnFetchData(CF_CALLBACK_INFO* info, byte* parameters) => Report("FETCH_DATA", info, parameters);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void OnCancelFetchData(CF_CALLBACK_INFO* info, byte* parameters) => Report("CANCEL_FETCH_DATA", info, null);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void OnFetchPlaceholders(CF_CALLBACK_INFO* info, byte* parameters)
    {
        Report("FETCH_PLACEHOLDERS", info, null);
        if (_seam)
            Fetched(info);
    }

    // ---- stop-seam's fetches (SL-089) ----

    // The NTSTATUS a failed fetch is completed with: STATUS_CLOUD_FILE_UNSUCCESSFUL unless the command line names another.
    private const int StatusCloudFileUnsuccessful = unchecked((int)0xC000CF12);
    private const uint CF_OPERATION_TYPE_TRANSFER_PLACEHOLDERS = 4;

    private static bool _seam;
    private static int? _failStatus;
    private static bool _released;
    private static readonly List<(long Connection, long Transfer, long Request, string Name, uint Pid)> Pending = [];

    private static int FailStatus => _failStatus ?? StatusCloudFileUnsuccessful;

    // Held until 'release', or failed at once after it. One line each: FETCH <name> pid=<n> utc=<time> held, or
    // FETCH <name> pid=<n> utc=<time> failed status=0x.. hr=0x.. with CfExecute's own result.
    private static void Fetched(CF_CALLBACK_INFO* info)
    {
        try
        {
            uint pid = info->ProcessInfo is null ? 0 : info->ProcessInfo->ProcessId;
            string path = info->NormalizedPath is null ? "" : new string(info->NormalizedPath).TrimEnd('\\');
            string name = path[(path.LastIndexOf('\\') + 1)..];
            lock (Gate)
            {
                if (!_released)
                {
                    Pending.Add((info->ConnectionKey, info->TransferKey, info->RequestKey, name, pid));
                    Out.WriteLine($"FETCH {name} pid={pid} utc={Utc()} held");
                    return;
                }
            }
            int hr = Fail(info->ConnectionKey, info->TransferKey, info->RequestKey);
            Line($"FETCH {name} pid={pid} utc={Utc()} failed status=0x{FailStatus:X8} hr=0x{hr:X8}");
        }
        catch
        {
            // A callback must not throw into cldflt.
        }
    }

    // Every held fetch failed with the failure status, in the order they came, and from now on every later one too.
    private static void Release()
    {
        List<(long Connection, long Transfer, long Request, string Name, uint Pid)> held;
        lock (Gate)
        {
            if (_released)
                return;
            _released = true;
            held = [.. Pending];
            Pending.Clear();
            Out.WriteLine($"RELEASE utc={Utc()} pending={held.Count}");
        }
        foreach ((long connection, long transfer, long request, string name, uint pid) in held)
        {
            int hr = Fail(connection, transfer, request);
            Line($"FETCH {name} pid={pid} utc={Utc()} failed status=0x{FailStatus:X8} hr=0x{hr:X8}");
        }
    }

    private static int Fail(long connection, long transfer, long request)
    {
        var operation = new CF_OPERATION_INFO
        {
            StructSize = (uint)sizeof(CF_OPERATION_INFO),
            Type = CF_OPERATION_TYPE_TRANSFER_PLACEHOLDERS,
            ConnectionKey = connection,
            TransferKey = transfer,
            RequestKey = request,
        };
        var parameters = new CF_OPERATION_PARAMETERS_TRANSFER_PLACEHOLDERS
        {
            ParamSize = (uint)sizeof(CF_OPERATION_PARAMETERS_TRANSFER_PLACEHOLDERS),
            CompletionStatus = FailStatus,
        };
        return CfExecute(&operation, &parameters);
    }

    private static string Utc() => DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);

    private static bool TryParseStatus(string text, out int status) =>
        int.TryParse(text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text,
            System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out status) && status < 0;

    private static int ParseStatus(string text) => TryParseStatus(text, out int status) ? status : StatusCloudFileUnsuccessful;

    // ---- teardown for the window case (SL-089, review M6) ----

    private const uint DELETE = 0x00010000;
    private const uint FILE_SHARE_ALL = 0x7;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private const uint FILE_FLAG_DELETE_ON_CLOSE = 0x04000000;

    // unregister: CfUnregisterSyncRoot, printing 'UNREGISTERED hr=...'. probe: CfGetSyncRootInfoByPath, printing
    // 'REGISTERED' or 'NOT-REGISTERED hr=...'. delete: the folder and all it holds, printing 'DELETED' or 'LEFT n ...'; what
    // the filter will not list or delete is deleted by opening its reparse point itself, as the cloud-hang spike had to.
    private static int Teardown(string what, string root)
    {
        switch (what)
        {
            case "unregister":
            {
                int hr = CfUnregisterSyncRoot(root);
                Line($"UNREGISTERED hr=0x{hr:X8}");
                return hr < 0 ? ExitError : 0;
            }
            case "probe":
            {
                byte* info = stackalloc byte[4096];
                uint returned;
                int hr = CfGetSyncRootInfoByPath(root, 0, info, 4096, &returned);
                Line(hr >= 0 ? "REGISTERED" : $"NOT-REGISTERED hr=0x{hr:X8}");
                return 0;
            }
            default:
            {
                var left = new List<string>();
                if (Directory.Exists(root))
                {
                    try
                    {
                        Directory.Delete(root, recursive: true);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        Line($"FALLBACK {e.GetType().Name}: {e.Message}");
                        DeleteThroughReparsePoints(root, left);
                    }
                }
                bool there = Directory.Exists(root);
                Line(there ? $"LEFT {Math.Max(1, left.Count)} {string.Join(" | ", left)}" : "DELETED");
                return there ? ExitError : 0;
            }
        }
    }

    private static void DeleteThroughReparsePoints(string folder, List<string> left)
    {
        string[] children;
        try
        {
            children = Directory.GetFileSystemEntries(folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            children = []; // a placeholder that can no longer be listed holds nothing on disk
        }
        foreach (string child in children)
        {
            if ((File.GetAttributes(child) & FileAttributes.Directory) != 0)
                DeleteThroughReparsePoints(child, left);
            else
                DeleteOne(child, left);
        }
        DeleteOne(folder, left);
    }

    private static void DeleteOne(string path, List<string> left)
    {
        using (Microsoft.Win32.SafeHandles.SafeFileHandle handle = CreateFileW(@"\\?\" + path, DELETE, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING,
                   FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_DELETE_ON_CLOSE, IntPtr.Zero))
        {
            if (handle.IsInvalid)
            {
                left.Add($"{path} ({Marshal.GetLastPInvokeError()})");
                return;
            }
        }
        if (Path.Exists(path))
            left.Add($"{path} (still there)");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void OnCancelFetchPlaceholders(CF_CALLBACK_INFO* info, byte* parameters) => Report("CANCEL_FETCH_PLACEHOLDERS", info, null);

    private static void Report(string type, CF_CALLBACK_INFO* info, byte* fetchData)
    {
        try
        {
            uint pid = info->ProcessInfo is null ? 0 : info->ProcessInfo->ProcessId;
            string volume = info->VolumeDosName is null ? "" : new string(info->VolumeDosName);
            string path = info->NormalizedPath is null ? "" : new string(info->NormalizedPath);
            string line = $"CB {type} pid={pid} path={volume}{path}";
            // CF_CALLBACK_PARAMETERS.FetchData: after ParamSize, the union starts at 8; Flags, then
            // RequiredFileOffset at 16 and RequiredLength at 24.
            if (fetchData is not null)
                line += $" offset={*(long*)(fetchData + 16)} length={*(long*)(fetchData + 24)}";
            Line(line);
        }
        catch
        {
            // Nothing a callback can do about a report it could not write.
        }
    }

    private static void Line(string text)
    {
        lock (Gate)
            Out.WriteLine(text);
    }

    private static void Check(string api, int hr)
    {
        if (hr < 0)
            throw new ProviderError(api, hr);
    }

    private static int HResultFromWin32(int error) => error <= 0 ? error : unchecked((int)(0x80070000 | (uint)(error & 0xFFFF)));

    private sealed class ProviderError(string api, int hr) : Exception($"{api} failed with 0x{hr:X8}")
    {
        public string Api { get; } = api;

        public int Hr { get; } = hr;
    }

    // ---- cldapi.h and kernel32 ----

    [StructLayout(LayoutKind.Sequential)]
    private struct CF_SYNC_REGISTRATION
    {
        public uint StructSize;
        public char* ProviderName;
        public char* ProviderVersion;
        public void* SyncRootIdentity;
        public uint SyncRootIdentityLength;
        public void* FileIdentity;
        public uint FileIdentityLength;
        public Guid ProviderId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CF_SYNC_POLICIES
    {
        public uint StructSize;
        public ushort HydrationPrimary;
        public ushort HydrationModifier;
        public ushort PopulationPrimary;
        public ushort PopulationModifier;
        public uint InSync;
        public uint HardLink;
        public uint PlaceholderManagement;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CF_CALLBACK_REGISTRATION
    {
        public uint Type;
        public delegate* unmanaged[Stdcall]<CF_CALLBACK_INFO*, byte*, void> Callback;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CF_CALLBACK_INFO
    {
        public uint StructSize;
        public long ConnectionKey;
        public void* CallbackContext;
        public char* VolumeGuidName;
        public char* VolumeDosName;
        public uint VolumeSerialNumber;
        public long SyncRootFileId;
        public void* SyncRootIdentity;
        public uint SyncRootIdentityLength;
        public long FileId;
        public long FileSize;
        public void* FileIdentity;
        public uint FileIdentityLength;
        public char* NormalizedPath;
        public long TransferKey;
        public byte PriorityHint;
        public void* CorrelationVector;
        public CF_PROCESS_INFO* ProcessInfo;
        public long RequestKey;
    }

    // Only the leading fields this reads; the platform owns the rest of the structure.
    [StructLayout(LayoutKind.Sequential)]
    private struct CF_PROCESS_INFO
    {
        public uint StructSize;
        public uint ProcessId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CF_PLACEHOLDER_CREATE_INFO
    {
        public char* RelativeFileName;
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public uint FileAttributes;
        public long FileSize;
        public void* FileIdentity;
        public uint FileIdentityLength;
        public uint Flags;
        public int Result;
        public long CreateUsn;
    }

    [DllImport("cldapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int CfRegisterSyncRoot(string syncRootPath, CF_SYNC_REGISTRATION* registration, CF_SYNC_POLICIES* policies,
        uint registerFlags);

    [DllImport("cldapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int CfConnectSyncRoot(string syncRootPath, CF_CALLBACK_REGISTRATION* callbackTable, void* callbackContext,
        uint connectFlags, long* connectionKey);

    [DllImport("cldapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int CfCreatePlaceholders(string baseDirectoryPath, CF_PLACEHOLDER_CREATE_INFO* placeholderArray, uint placeholderCount,
        uint createFlags, uint* entriesProcessed);

    [StructLayout(LayoutKind.Sequential)]
    private struct CF_OPERATION_INFO
    {
        public uint StructSize;
        public uint Type;
        public long ConnectionKey;
        public long TransferKey;
        public void* CorrelationVector;
        public void* SyncStatus;
        public long RequestKey;
    }

    // CF_OPERATION_PARAMETERS with its TransferPlaceholders member: ParamSize, then the union at 8.
    [StructLayout(LayoutKind.Sequential)]
    private struct CF_OPERATION_PARAMETERS_TRANSFER_PLACEHOLDERS
    {
        public uint ParamSize;
        public uint Padding;
        public uint Flags;
        public int CompletionStatus;
        public long PlaceholderTotalCount;
        public CF_PLACEHOLDER_CREATE_INFO* PlaceholderArray;
        public uint PlaceholderCount;
        public uint EntriesProcessed;
    }

    [DllImport("cldapi.dll", ExactSpelling = true)]
    private static extern int CfExecute(CF_OPERATION_INFO* operationInfo, CF_OPERATION_PARAMETERS_TRANSFER_PLACEHOLDERS* operationParameters);

    [DllImport("cldapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int CfUnregisterSyncRoot(string syncRootPath);

    [DllImport("cldapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int CfGetSyncRootInfoByPath(string filePath, int infoClass, void* infoBuffer, uint infoBufferLength, uint* returnedLength);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);
}
