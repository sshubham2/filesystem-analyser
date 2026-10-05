# Runs the published app against one root in report mode and prints what its window showed.
#
#   pwsh -NoProfile -ExecutionPolicy Bypass -File eng/read-window.ps1 -Root <dir> [-Exe <path>] [-TimeoutSec <n>]
#
# Prints `EXIT <code>` (or `TIMEOUT`, after killing the process it started) and then the report JSON,
# or `NO REPORT` when the run wrote none. It asserts nothing: the checking is the caller's. The report
# schema and the exit codes are documented in src/FsAnalyser.App/WindowReport.cs. Publish first:
#
#   pwsh -NoProfile -ExecutionPolicy Bypass -File eng/dotnet.ps1 publish src/FsAnalyser.App/FsAnalyser.App.csproj -c Release -o artifacts/publish
#
# The traps it exists for: Start-Process does not quote arguments, so both paths are quoted here, and a
# trailing backslash is doubled so that it does not escape the closing quote (C:\ would arrive as C:").
# Every run gets a new report path, so an older report can never be read as this run's. A WinExe started
# with a bare & returns before it exits, so the process is waited on, for at most -TimeoutSec.
#
# -DefaultErrorMode starts the app with CREATE_DEFAULT_ERROR_MODE instead of through Start-Process, so it does
# not inherit this shell's error mode (SL-077). A shell under the engine or a test host already has
# SEM_FAILCRITICALERRORS (0x1) set (0x8003 measured), and an app it starts can never show the "insert a
# disk" box, whatever its own code does; started from Explorer, the app has 0x0. The app is created
# suspended, its error mode (what GetErrorMode returns inside it) is printed as `ERRORMODE 0x<hex>` before
# the `EXIT` line, and then it runs. A box the app raises holds the call that raised it, so such a run ends
# in TIMEOUT and the box is left for a person to answer. Run it on a removable drive with no media.

#
# -Outcome (SL-095) adds one line after the report: `OUTCOME outcome=<o> refusal=<r> skippedStores=<n>
# opensUnderExcludedStore=<n> unreadable=<n>`, read from the report's own fields ("-" for one it does not hold), so a
# refused root (outcome=refused refusal=container-layer-store) and the container layer store left out of a scan
# (skippedStores=1, never counted among the unreadable) can be read without parsing the JSON.

param(
    [Parameter(Mandatory = $true)] [string] $Root,
    [string] $Exe = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\publish\FsAnalyser.App.exe'),
    [int] $TimeoutSec = 300,
    [switch] $DefaultErrorMode,
    [switch] $Outcome
)

$ErrorActionPreference = 'Stop'

if (-not [System.IO.Path]::IsPathRooted($Exe)) {
    $Exe = Join-Path (Get-Location).ProviderPath $Exe
}
if (-not (Test-Path -LiteralPath $Exe -PathType Leaf)) {
    Write-Error "eng/read-window.ps1: no app at $Exe. Publish it first (see the header of this script)."
    exit 1
}

$folder = Join-Path ([System.IO.Path]::GetTempPath()) 'fsa-window'
New-Item -ItemType Directory -Force -Path $folder | Out-Null
$report = Join-Path $folder ("{0}.json" -f [guid]::NewGuid().ToString('N'))

$rootArgument = $Root -replace '(\\+)$', '$1$1'
if ($DefaultErrorMode) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace FsaReadWindow
{
    public static class DefaultErrorModeLaunch
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFO
        {
            public int cb;
            public string lpReserved, lpDesktop, lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess, hThread;
            public int dwProcessId, dwThreadId;
        }

        private const uint CREATE_SUSPENDED = 0x00000004;
        private const uint CREATE_DEFAULT_ERROR_MODE = 0x04000000;
        private const int ProcessDefaultHardErrorMode = 12;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcessW(string applicationName, StringBuilder commandLine, IntPtr processAttributes,
            IntPtr threadAttributes, bool inheritHandles, uint creationFlags, IntPtr environment, string currentDirectory,
            ref STARTUPINFO startupInfo, out PROCESS_INFORMATION processInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint ResumeThread(IntPtr thread);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateProcess(IntPtr process, uint exitCode);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("ntdll.dll")]
        private static extern int NtQueryInformationProcess(IntPtr process, int infoClass, out uint info, int size, IntPtr returned);

        // The process's error mode as GetErrorMode reports it inside that process. The kernel keeps
        // SEM_FAILCRITICALERRORS inverted (its bit 0 means "show the box"), and GetErrorMode flips it back.
        public static uint ErrorModeOf(IntPtr process)
        {
            uint raw;
            int status = NtQueryInformationProcess(process, ProcessDefaultHardErrorMode, out raw, sizeof(uint), IntPtr.Zero);
            if (status != 0)
                throw new InvalidOperationException(string.Format("NtQueryInformationProcess(ProcessDefaultHardErrorMode) failed: 0x{0:X8}", status));
            return raw ^ 1;
        }

        // Starts the command line suspended with CREATE_DEFAULT_ERROR_MODE and reads its error mode. The caller
        // takes the process by id while it cannot yet have exited, then calls Resume with the thread handle.
        public static int StartSuspended(string exe, string commandLine, out IntPtr thread, out uint errorMode)
        {
            var startup = new STARTUPINFO { cb = Marshal.SizeOf(typeof(STARTUPINFO)) };
            PROCESS_INFORMATION info;
            if (!CreateProcessW(exe, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, false,
                    CREATE_SUSPENDED | CREATE_DEFAULT_ERROR_MODE, IntPtr.Zero, null, ref startup, out info))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcessW " + exe);
            try
            {
                errorMode = ErrorModeOf(info.hProcess);
            }
            catch
            {
                TerminateProcess(info.hProcess, 1);
                CloseHandle(info.hThread);
                throw;
            }
            finally
            {
                CloseHandle(info.hProcess);
            }
            thread = info.hThread;
            return info.dwProcessId;
        }

        public static void Resume(IntPtr thread)
        {
            try
            {
                if (ResumeThread(thread) == uint.MaxValue)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "ResumeThread");
            }
            finally
            {
                CloseHandle(thread);
            }
        }
    }
}
'@
    $thread = [IntPtr]::Zero
    $mode = [uint32]0
    $id = [FsaReadWindow.DefaultErrorModeLaunch]::StartSuspended($Exe, "`"$Exe`" `"$rootArgument`" --report `"$report`"", [ref] $thread, [ref] $mode)
    $process = [System.Diagnostics.Process]::GetProcessById($id)
    $null = $process.Handle # taken while the app is suspended, so its exit code can be read however soon it ends
    [FsaReadWindow.DefaultErrorModeLaunch]::Resume($thread)
    Write-Output ('ERRORMODE 0x{0:X}' -f $mode)
}
else {
    $process = Start-Process -FilePath $Exe -ArgumentList "`"$rootArgument`" --report `"$report`"" -PassThru
    $null = $process.Handle # keeps the handle, without which ExitCode reads empty after the wait
}

if ($process.WaitForExit($TimeoutSec * 1000)) {
    Write-Output "EXIT $($process.ExitCode)"
}
else {
    $process.Kill()
    $process.WaitForExit()
    Write-Output 'TIMEOUT'
}

if (Test-Path -LiteralPath $report -PathType Leaf) {
    $text = Get-Content -LiteralPath $report -Raw
    Write-Output $text
    Remove-Item -LiteralPath $report -Force
    if ($Outcome) {
        $r = $text | ConvertFrom-Json
        $field = { param($value) if ($null -eq $value) { '-' } else { "$value" } }
        Write-Output ('OUTCOME outcome={0} refusal={1} skippedStores={2} opensUnderExcludedStore={3} unreadable={4}' -f `
                (& $field $r.outcome), (& $field $r.refusal), (& $field $r.core.skippedStores), (& $field $r.core.opensUnderExcludedStore), (& $field $r.core.unreadable))
    }
}
else {
    Write-Output 'NO REPORT'
}
