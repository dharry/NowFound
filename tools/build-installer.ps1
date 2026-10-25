# Build the Windows installer: publish (deploy\publish.ps1) -> Inno Setup 7 (installer\installer.iss).
#   .\tools\build-installer.ps1                       Run tests, publish, build dist\NowFound-Setup-1.0.0.exe
#   .\tools\build-installer.ps1 -Version 1.2.3        Set the installer version
#   .\tools\build-installer.ps1 -SkipTests            Skip dotnet test during publish
#   .\tools\build-installer.ps1 -SkipPublish          Reuse the existing dist\NowFound
# Inno Setup 7 location: -InnoSetupDir, or the INNO_SETUP_DIR environment variable
# (default: C:\Program Files\Inno Setup 7).
# (comments/messages are ASCII on purpose: Windows PowerShell 5.1 code page safe)
[CmdletBinding(PositionalBinding = $false)]
param(
    [string]$Version = '1.0.0',
    [switch]$SkipTests,
    [switch]$SkipPublish,
    [string]$InnoSetupDir = $(if ($env:INNO_SETUP_DIR) { $env:INNO_SETUP_DIR } else { 'C:\Program Files\Inno Setup 7' }),
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$RemainingArgs
)

function Show-Usage {
    Write-Host @'
Usage: build-installer.ps1 [-Version <x.y.z>] [-SkipTests] [-SkipPublish] [-InnoSetupDir <dir>]

Publishes (deploy\publish.ps1) and builds the Windows installer dist\NowFound-Setup-<version>.exe
with Inno Setup 7.

Options:
  -Version <x.y.z>        Installer version (default: 1.0.0)
  -SkipTests              Skip dotnet test during publish
  -SkipPublish            Reuse the existing dist\NowFound
  -InnoSetupDir <dir>     Inno Setup 7 folder (default: INNO_SETUP_DIR or C:\Program Files\Inno Setup 7)
  -h, --help, -help       Show this usage
'@
}

if ($RemainingArgs) {
    if ($RemainingArgs | Where-Object { $_ -in '-h', '--help', '-help', '-?', '/?' }) {
        Show-Usage
        exit 0
    }

    Write-Host "Unknown argument: $($RemainingArgs -join ' ')"
    Write-Host ''
    Show-Usage
    exit 1
}

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)

$iscc = Join-Path $InnoSetupDir 'ISCC.exe'
if (-not (Test-Path -LiteralPath $iscc)) {
    throw "ISCC.exe not found: $iscc  (install Inno Setup 7, or set -InnoSetupDir / INNO_SETUP_DIR)"
}

if (-not $SkipPublish) {
    $publishArgs = @()
    if ($SkipTests) { $publishArgs += '-SkipTests' }
    & (Join-Path $root 'deploy\publish.ps1') @publishArgs
}

if (-not (Test-Path -LiteralPath (Join-Path $root 'dist\NowFound\web\NowFound.Web.exe'))) {
    throw 'dist\NowFound was not found. Run without -SkipPublish.'
}

& $iscc "/DAppVersion=$Version" (Join-Path $root 'installer\installer.iss')
if ($LASTEXITCODE -ne 0) { throw "ISCC failed ($LASTEXITCODE)" }

Write-Host "Done: $(Join-Path $root "dist\NowFound-Setup-$Version.exe")"
