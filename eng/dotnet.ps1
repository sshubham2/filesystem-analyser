# Runs the dotnet CLI with a .NET 10 SDK, forwarding every argument unchanged.
#
#   pwsh -NoProfile -ExecutionPolicy Bypass -File eng/dotnet.ps1 test FsAnalyser.slnx
#
# A machine-wide 10.0.x SDK is used when `dotnet --list-sdks` shows one. Otherwise Microsoft's
# dotnet-install.ps1 installs the 10.0 channel, as the ordinary user, into the gitignored repo-local
# .dotnet/ folder, and DOTNET_ROOT and PATH point at it for this process only. It never falls back
# to an older SDK: global.json pins 10.0.100 (rollForward latestFeature), so a 9.0 SDK fails loudly.

$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$localRoot = Join-Path $repo '.dotnet'
$localExe = Join-Path $localRoot 'dotnet.exe'

function Test-Has10Sdk([string] $dotnetExe) {
    if (-not (Test-Path $dotnetExe)) { return $false }
    $sdks = & $dotnetExe --list-sdks 2>$null
    return [bool]($sdks | Where-Object { $_ -match '^10\.0\.\d+' })
}

$machine = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
$dotnetExe = $null

if ($machine -and (Test-Has10Sdk $machine.Source)) {
    $dotnetExe = $machine.Source
}
elseif (Test-Has10Sdk $localExe) {
    $dotnetExe = $localExe
}
else {
    New-Item -ItemType Directory -Force -Path $localRoot | Out-Null
    $installer = Join-Path $localRoot 'dotnet-install.ps1'
    Write-Host "eng/dotnet.ps1: no 10.0 SDK found; installing the 10.0 channel into $localRoot"
    try {
        Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer -UseBasicParsing
        & $installer -Channel 10.0 -InstallDir $localRoot -NoPath
    }
    catch {
        Write-Error "eng/dotnet.ps1: could not install a 10.0 SDK into $localRoot. $($_.Exception.Message)"
        exit 1
    }
    if (-not (Test-Has10Sdk $localExe)) {
        Write-Error "eng/dotnet.ps1: dotnet-install.ps1 finished but $localExe lists no 10.0 SDK."
        exit 1
    }
    $dotnetExe = $localExe
}

if ($dotnetExe -eq $localExe) {
    $env:DOTNET_ROOT = $localRoot
    $env:PATH = "$localRoot;$env:PATH"
    $env:DOTNET_MULTILEVEL_LOOKUP = '0'
}
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

& $dotnetExe @args
exit $LASTEXITCODE
