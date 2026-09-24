# SL-024 build attempt 2: cloud-placeholder cancel re-spike with the scanner's real listing call

Ordered by the rebuild note on build-slice attempt 1: measure first, build only on a go.

Verdict: **GO**, both runs. The control was valid (blocked 22 s) and all 7 of 7 gated trials passed in each run.

## What ran

This is spike 2's harness (`%APPDATA%\taad3\spikes\8\spike-33\cloud-fetch-placeholders-cancel-not-empty-not-cloud-code`), copied to `%TEMP%\taad-sl024-respike`, with the provider side left as it was:

- The provider runs in a separate process and never answers FETCH_PLACEHOLDERS.
- CfRegisterSyncRoot uses population FULL, hydration PARTIAL, and flag 2 (DISABLE_ON_DEMAND_POPULATION_ON_ROOT).
- The directory placeholders keep on-demand population on.
- It runs unelevated.

Two things differ from spike 2. The provider name and id changed, so a stale spike registration cannot collide. The sync root is created inside the temp folder and deleted afterwards.

The lister now makes the scanner's own calls in place of FindFirstFileExW:

- `CreateFileW(\\?\dir, FILE_LIST_DIRECTORY, share all, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS)` (DirectoryReader.cs:66)
- `GetFileInformationByHandleEx` with FileIdExtdDirectoryRestartInfo (20), then 19, into a 64 KiB buffer (DirectoryReader.cs:96-113). The fallback classes 15 and 14 are also run.
- The public `Scanner.Scan` from this worktree's `FsAnalyser.Core.dll`, built at HEAD 5da23ca. That build has no guard. SHA256 1A0FF16DFC05F4125618115992BA4B7DB014BE19177852119F957996090D94FE.

The canceller runs on another thread. It calls `OpenThread(THREAD_TERMINATE, worker tid)` and then `CancelSynchronousIo` every 25 ms. The `real-guarded` arms apply the design's recorded-intent rule: the intent is set before each cancel, a call during which it was set is discarded as 995, and no call starts once it is set.

## Commands (from the repository root, then from the temp folder)

The harness source is `Program.cs` and `cfrespike.csproj` in this folder. To run it again, copy both into a
scratch folder outside the repository and build them there, so that bin/ and obj/ never land in the slice.

```
pwsh: $core = (Resolve-Path src/FsAnalyser.Core/bin/Debug/net10.0-windows/FsAnalyser.Core.dll).Path
cd $env:TEMP\taad-sl024-respike
<repo>\.dotnet\dotnet.exe build cfrespike.csproj -c Release -nologo "-p:FsCoreDll=$core"     -> Build succeeded, 0 warnings, 0 errors
<repo>\.dotnet\dotnet.exe bin/Release/net10.0-windows/cfrespike.dll run                     -> exit 0, run1.log / provider1.log
<repo>\.dotnet\dotnet.exe bin/Release/net10.0-windows/cfrespike.dll run                     -> exit 0, run2.log / provider2.log
```

## Results (run1; run2 is the same to within milliseconds)

| arm | trials | blocked before cancel | returned after the first cancel | what the OS returned | outcome |
|---|---|---|---|---|---|
| control, cancel from 22 s, unguarded | slow0 | yes, 22.0 s in GFIBHEx#1 | 0.032 s | #1 SUCCESS with only `.` and `..`; #2 ERROR_NO_MORE_FILES (18) in 0.031 s | the listing reads as empty |
| real-guarded, class 20/19 | slow1-5 | yes, ~2 s in GFIBHEx#1 | <= 1 ms, 1 cancel | SUCCESS with only `.` and `..` | discarded, Settle = 995. PASS 5 of 5 |
| real-guarded-full, class 15/14 | full1-2 | yes, in GFIBHEx#1 | <= 1 ms, 1 cancel | SUCCESS with only `.` and `..` | discarded, Settle = 995. PASS 2 of 2 |
| real-unguarded (today's List) | ung1 | yes | 0.033 s | as control | the folder reads as empty |
| today's Scanner.Scan | scan1-2 | yes | 0.035 s | n/a | **status=Completed, unreadable=0, files=0**: the silent empty folder the design's discard rule exists to stop |
| real-guarded, ONE cancel only | once1 | yes | <= 1 ms | SUCCESS with only `.` and `..` | discarded. A single cancel was enough |

CloseHandle on the directory handle after the cancelled listing took 0.000 s in every trial. CfRegisterSyncRoot, CfConnectSyncRoot, CfCreatePlaceholders, CfDisconnectSyncRoot and CfUnregisterSyncRoot all returned hr=0 in both runs, and the sync root folder was deleted.

## What this settles and what it does not

- Settled (O-023, the design's first decidable disagreement): CancelSynchronousIo frees the scanner's real listing call on a populating cloud placeholder within 2 s. It returns SUCCESS with only `.` and `..`, the spike-2 shape, not 995. The discard-by-intent rule is therefore required, and it is sufficient. After the cancel the next listing call returns 18 in about 30 ms, so the call does not block again.
- Different from the stand-in: FindFirstFileExW needed about 3 cancels and never returned after one. The real call returned after one. The design's repeat loop stays, because it costs nothing and the stand-in showed a call that re-blocks.
- Not settled: a stalled, established SMB session (spike 3) is still unmeasured.
