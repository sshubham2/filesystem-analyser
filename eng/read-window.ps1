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

param(
    [Parameter(Mandatory = $true)] [string] $Root,
    [string] $Exe = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\publish\FsAnalyser.App.exe'),
    [int] $TimeoutSec = 300
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
$process = Start-Process -FilePath $Exe -ArgumentList "`"$rootArgument`" --report `"$report`"" -PassThru
$null = $process.Handle # keeps the handle, without which ExitCode reads empty after the wait

if ($process.WaitForExit($TimeoutSec * 1000)) {
    Write-Output "EXIT $($process.ExitCode)"
}
else {
    $process.Kill()
    $process.WaitForExit()
    Write-Output 'TIMEOUT'
}

if (Test-Path -LiteralPath $report -PathType Leaf) {
    Write-Output (Get-Content -LiteralPath $report -Raw)
    Remove-Item -LiteralPath $report -Force
}
else {
    Write-Output 'NO REPORT'
}
