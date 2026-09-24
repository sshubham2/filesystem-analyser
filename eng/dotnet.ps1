# Runs the dotnet CLI with a .NET 10 SDK, forwarding every argument unchanged.
#
#   pwsh -NoProfile -ExecutionPolicy Bypass -File eng/dotnet.ps1 test FsAnalyser.slnx
#
# A machine-wide 10.0.x SDK is used when `dotnet --list-sdks` shows one. Otherwise Microsoft's
# dotnet-install.ps1 installs SDK $SdkVersion, the one this repo was built and measured on, as the
# ordinary user into the gitignored repo-local .dotnet/ folder, and DOTNET_ROOT and PATH point at it
# for this process only. The downloaded installer runs only if its Authenticode signature is valid and
# Microsoft's. It never falls back to an older SDK: global.json pins 10.0.100 (rollForward
# latestFeature), so a 9.0 SDK fails loudly.

$ErrorActionPreference = 'Stop'

$SdkVersion = '10.0.401'
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
    Write-Host "eng/dotnet.ps1: no 10.0 SDK found; installing SDK $SdkVersion into $localRoot"
    try {
        Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer -UseBasicParsing
    }
    catch {
        Write-Error "eng/dotnet.ps1: could not download dotnet-install.ps1. $($_.Exception.Message)"
        exit 1
    }

    $signature = Get-AuthenticodeSignature -FilePath $installer
    $signer = if ($signature.SignerCertificate) { $signature.SignerCertificate.Subject } else { '(none)' }
    if ($signature.Status -ne 'Valid' -or $signer -notmatch '(^|,\s*)O=Microsoft Corporation(,|$)') {
        Remove-Item -LiteralPath $installer -Force
        Write-Error "eng/dotnet.ps1: refusing to run dotnet-install.ps1: signature $($signature.Status), signer $signer."
        exit 1
    }

    try {
        & $installer -Version $SdkVersion -InstallDir $localRoot -NoPath
    }
    catch {
        Write-Error "eng/dotnet.ps1: could not install SDK $SdkVersion into $localRoot. $($_.Exception.Message)"
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
