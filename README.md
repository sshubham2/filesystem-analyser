# Filesystem Analyser

Point it at a drive or a folder and it shows where the disk space went.

Filesystem Analyser is a Windows desktop app. It scans as the account you are
signed in as, lists the largest folders first while it is still reading, and
ends with a folder tree, the largest files, a breakdown by kind of file and a
list of the folders it could not read. It never deletes, moves or changes
anything: Explorer is where you act on what it finds.

**Status: not released.** There is no download yet. The app builds and its
test suite passes; the first release waits on runs against a real system
drive, a real OneDrive folder and a real network share, which a person makes
at the desk.

## What it does

- **Scans a drive or a folder.** Pick a drive from the list, choose a folder,
  or pass a path on the command line.
- **Shows results while it scans.** The top-level folders fill in largest
  first, with running counts of files, folders and bytes.
- **Stops when asked.** Stop interrupts a read that is stuck on a slow or
  unreachable drive instead of abandoning it. After a stop, every figure that
  could still grow is marked "at least", and folders the scan never reached
  say "not read yet".
- **Folder tree.** Children are ordered by space on disk, each with its share
  of its parent.
- **Largest files** anywhere under the root, and **file kinds** (videos,
  images, documents, archives and so on).
- **Unreadable folders**, each with the reason Windows gave. A folder that was
  deleted while the scan ran is noted as gone, not listed as unreadable.
- **Whole-drive coverage.** On a whole-drive scan it compares what it counted
  with what the volume reports in use, and a panel says what the gap is made
  of as far as an ordinary account can tell.
- **Rescan as administrator.** After a whole-drive scan of a local drive that
  left folders unread, the app offers to run again elevated. It asks through
  the Windows prompt only when you press that button, never by itself.
- **Open in Explorer** on any row.
- **Report mode** for unattended checks:
  `FsAnalyser.App.exe <root> --report <file>` scans, writes what the window
  shows as JSON, and exits.

### How it counts

- Sizes are on-disk sizes, read from each file itself after the walk, so a
  file that is still being written is counted at its current size.
- A file with several hard links is counted once.
- Junctions and symbolic links are recorded and never followed.
- Cloud-only files (OneDrive and similar) are not downloaded.
- Paths longer than 260 characters are handled.

## Requirements

- Windows 11, x64.
- To build: PowerShell 7, and the .NET 10 SDK. `eng/dotnet.ps1` uses a
  machine-wide 10.0 SDK when there is one and otherwise installs one into the
  repository's own `.dotnet` folder.

## Build, test and run

From the repository root:

```powershell
# build and run the test suite
pwsh -NoProfile -ExecutionPolicy Bypass -File eng/dotnet.ps1 test FsAnalyser.slnx

# publish the app as one self-contained exe
pwsh -NoProfile -ExecutionPolicy Bypass -File eng/dotnet.ps1 publish src/FsAnalyser.App/FsAnalyser.App.csproj -c Release -o artifacts/publish

# run it, optionally with a path to scan at start
artifacts\publish\FsAnalyser.App.exe C:\Users
```

Report mode exit codes: `0` report written, `2` bad arguments, `3` the report
could not be written, `70` the app failed before writing one.

Tests that need a network share, an unreachable address or a cloud sync root
are opt-in and do not run by default. `tests/window/WindowCases.ps1` runs
end-to-end cases against the published exe; it opens real windows.

## Known limits

- Windows 11 x64 only.
- Settings last for the session; nothing is saved between runs.
- One precaution is on by default: Windows container layers are skipped,
  because reading them triggers a memory leak in a Windows driver. It can be
  turned off for a session in Settings.
- The layout has been checked at 125% display scaling only.
- Not signed. Windows SmartScreen will warn on first run once there is a build
  to hand out.

## Layout

```
src/FsAnalyser.Core   the scanner, the store of scanned entries, the analyses,
                      and every direct call into Windows
src/FsAnalyser.App    the window (WPF) and report mode
tests/                unit and real-folder tests, test rigs, window cases
eng/                  build and tooling scripts
docs/design/          one design note per piece of work
.taad/fitness.yaml    the architecture rules the code is held to
```

## Developed with TAAD

This project is built with TAAD, an AI SDLC pipeline engine that drives AI
coding agents through a software project and records every run, decision and
cost.

Each change here is a small slice that goes through TAAD's gated loop: the
defect is reproduced or the work is checked against what is already built, the
risky assumptions are tested in throwaway experiments, a design is drafted and
criticised, an agent builds it in its own git worktree, a different agent
reviews the diff, the result is validated against something real, and only
then is it merged into `uat`. Commit subjects that begin `SL-` and merges
named `Merge slice/SL-...` come from that loop, and the notes under
`docs/design/` are its design records.

TAAD also runs repository health checks on this code, one feature at a time,
and the architecture rules in `.taad/fitness.yaml` are checked at every gate.
A person decides what is built, approves what "finished" means for each piece
of work, and makes the runs that need a real machine.
