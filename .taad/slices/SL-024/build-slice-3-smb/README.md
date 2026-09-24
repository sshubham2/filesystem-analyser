# SL-024 build attempt 3: O-018, a share that stops answering once its session is established

The rebuild note ordered two checks before the test was written: that Defender allows SMBLibrary, and
that the Windows 11 client connects to it on a port other than 445, with signing required by default.
The run was unelevated, with no Defender change and no impacket.

## Check 1: Defender lets SMBLibrary load. PASSED

The packages came from NuGet: SMBLibrary 1.5.8.1 and SMBLibrary.Win32 1.5.8, both LGPL-3.0-or-later.

- `[IO.File]::ReadAllBytes` returned the full file for all six package DLLs: 426,496 / 427,008 / 427,520 bytes for SMBLibrary.dll (net20, net40, netstandard2.0) and 27,136 bytes for SMBLibrary.Win32.dll. Defender blocks a read of a file it flags, so none of them was flagged.
- The precheck program then loaded and ran both libraries.

## Check 2: the client connects on another port. PASSED

The precheck ran twice: `precheck-Program.cs` against `precheck-smbcheck.csproj`, built in `%TEMP%\taad-sl024-smb` with the repo-local SDK:

```
<repo>\.dotnet\dotnet.exe build            (in %TEMP%\taad-sl024-smb)
<repo>\.dotnet\dotnet.exe bin/Debug/net10.0/smbcheck.dll
```

- An SMBLibrary server listened on 127.0.0.1:63243 (run 1) and 127.0.0.1:63269 (run 2). It had one share and one test user (NTLM, IndependentNTLMAuthenticationProvider), and a real temp folder behind it (NTDirectoryFileSystem).
- `New-SmbMapping -RemotePath '\\127.0.0.1\fsa' -TcpPort <port> -UserName fsatest -Password *** -Persistent $false` exited 0 with Status OK.
- The server logged `Session Setup: User 'fsatest' authenticated successfully (... OS version: '10.0.26100')` and `Tree Connect ... connected to 'fsa'`.
- `Directory.GetFileSystemEntries` listed the share recursively in 16 ms.
- `Remove-SmbMapping` exited 0. See `precheck.log`.

The client's signing settings were left at their Windows 11 defaults and were not read or changed.

## Faults met while building the fixture, all fixed in the test code

- SMBLibrary calls `INTFileStore.DeviceIOControl` with a null handle (FSCTL_VALIDATE_NEGOTIATE_INFO). The wrapper's cast threw on the server's receive thread and crashed the test host.
- The Windows client passes FileIdExtdDirectoryInformation (class 60) through to the server. SMBLibrary throws NotImplementedException on it. The store now answers STATUS_INVALID_INFO_CLASS, as a server without the class does, and the scanner falls back to FILE_FULL_DIR_INFO.
- The crashed host left a non-persistent mapping `\\127.0.0.1\fsa9d4edac0` behind. The next `New-SmbMapping` then failed first with "The network path was not found" (error 53), then with "Multiple SMB mappings to shared resource(s) on a server, using more than one port for a given transport, are not allowed". Those runs' outputs are not kept here. I removed that mapping, which was mine. The fixture now removes only its own pattern (`\\127.0.0.1\fsa` followed by 8 hex characters) before it maps. No other mapping was touched.

## The test and what it measured

`FsAnalyser.Core.Tests.StalledShareTests.CancellingAScanOfAShareThatStoppedAnsweringNeverHangsInTheClose` is opt-in through `Trait("Network","SMB-LOOPBACK")`. The share has `a`, `hung` and `z`. The store answers normally until the listing of `hung`, then answers nothing more: no close, no cancel, no other request.

| run | Cancel() on the calling thread | scan returned after Cancel | CancelSynchronousIo | result |
|---|---|---|---|---|
| 2 s bound (`test-run5.txt`) | 0.7 ms | still blocked at 2 s | 1 attempt, 0 delivered after 2 s: the call itself was blocked | FAILED at the 2 s bound |
| 150 s measurement (`test-run6-measure.txt`) | 1.0 ms | 65,987 ms | 1 attempt, delivered after about 66 s | Cancelled, no unreadable folder, `hung` not committed, close handed off and not pending |
| final test, 90 s bound (`test-run7.txt`) | 0.9 ms | 61,865 ms | 1 attempt, 1 delivered | PASSED |

Reading: on a stalled, established session, the SMB client holds the unanswered QUERY_DIRECTORY, and the CancelSynchronousIo aimed at it, until it gives up on the server, at about 60 s plus a few seconds. Cancel() stays prompt, because it only starts the canceller. The close after the abort never runs on the scan thread. The stalled folder is never passed off as an empty read. Criterion (1)'s 2 s does not hold for this shape, and nothing in-process can make it hold: only abandoning the scan thread would return sooner.
