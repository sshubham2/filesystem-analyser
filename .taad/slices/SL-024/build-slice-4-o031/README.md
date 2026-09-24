# SL-024 build attempt 4: evidence for O-031, the App's volume-usage query

The fix is in `src/FsAnalyser.App/MainWindow.xaml.cs`. The scan runs alone on its LongRunning thread.
After it, `UnlessCancelled(() => VolumeInUse(scan), token)` never starts the query once Cancel was
requested. Otherwise it runs the query on its own thread and awaits it with `WaitAsync(token)`, so a
Cancel that arrives during the query ends the window's wait. The query itself finishes unobserved.

There is no App test project; that is a recorded project decision. The evidence is therefore a scratch
harness plus a report-mode run of the published App.

## 1. The App's own `UnlessCancelled`, called on the built assembly (`Program.cs`, `o031.csproj`)

```
<repo>\.dotnet\dotnet.exe build                              (in %TEMP%\taad-sl024-o031)
<repo>\.dotnet\dotnet.exe bin/Debug/net10.0-windows/win-x64/o031.dll <repo>\src\FsAnalyser.App\bin\Debug\net10.0-windows\win-x64\FsAnalyser.App.dll
```

Output, from `o031.log`:

```
PASS  cancelled before: result null, query started False, 17.7 ms
PASS  cancelled during: the wait ended 2.3 ms after Cancel with null, the query still running: True
PASS  not cancelled: result 42 after 205 ms
PASS  own thread: caller 5, query ran on 6
ALL PASS
```

The "during" case blocks the query for up to 30 s. That stands in for GetDiskFreeSpaceEx on a mapped
drive whose server stopped answering: 21 s at connect in spike 1, and 62 to 66 s on an established session
in build 3. The window's wait ended 2.3 ms after Cancel while the query was still blocked.

## 2. The ending without Cancel keeps the volume line (`readwindow-D.txt`)

```
pwsh -NoProfile -ExecutionPolicy Bypass -File eng/dotnet.ps1 publish src/FsAnalyser.App/FsAnalyser.App.csproj -c Release -o artifacts/publish
pwsh -NoProfile -ExecutionPolicy Bypass -File eng/read-window.ps1 -Root 'D:\' -TimeoutSec 900
```

EXIT 0. The status was Finished, and OutcomeDetail still shows "The volume reports 125.4 GB in use, so
779.2 MB of it was not seen by this scan ...". The query is still awaited when nobody cancels.

## Not measured

A real mapped drive letter stalled under the App was not tried. The App cannot be cancelled in report
mode without UI automation, and UI automation is out of bounds here. The harness above calls the exact
method the window awaits.
