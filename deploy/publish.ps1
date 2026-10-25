# Build, publish and package NowFound into dist/ (NowFound-publish.zip, Windows).
#   .\deploy\publish.ps1                Run tests, publish, prune runtimes, zip
#   .\deploy\publish.ps1 -SkipTests     Build only; skip dotnet test
#   .\deploy\publish.ps1 -IncludeLinux  Also package the Linux files (NowFound-publish.tar.gz; Linux is not supported yet)
# (comments/messages are ASCII on purpose: Windows PowerShell 5.1 code page safe)
[CmdletBinding(PositionalBinding = $false)]
param(
    [switch]$SkipTests,
    [switch]$IncludeLinux,
    # 'win' / 'unix' are RID-neutral managed assets (e.g. ServiceController, required by the Windows service).
    [string[]]$KeepRuntimes = $(if ($IncludeLinux) { @('linux-x64', 'win-x64', 'win', 'unix') } else { @('win-x64', 'win', 'unix') }),
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$RemainingArgs
)

function Show-Usage {
    Write-Host @'
Usage: publish.ps1 [-SkipTests] [-IncludeLinux] [-KeepRuntimes <rid>[,<rid>...]]

Runs the tests, publishes Web and Indexer, prunes native runtimes and packages
dist\NowFound-publish.zip (Windows).

Options:
  -SkipTests               Build only; skip dotnet test
  -IncludeLinux            Also package the Linux files and dist\NowFound-publish.tar.gz (Linux is not supported yet)
  -KeepRuntimes <rids>     Runtimes to keep (default: win-x64, win, unix; with -IncludeLinux also linux-x64)
  -h, --help, -help        Show this usage
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
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root 'dist'
$out = Join-Path $dist 'NowFound'

function Invoke-Dotnet {
    & dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($args -join ' ') failed ($LASTEXITCODE)" }
}

if ($SkipTests) {
    # Build only (no tests): compile errors still stop the script before publish.
    Invoke-Dotnet build (Join-Path $root 'NowFound.slnx') -c Release
}
else {
    Invoke-Dotnet test (Join-Path $root 'NowFound.slnx') -c Release
}

if (Test-Path -LiteralPath $out) { Remove-Item -LiteralPath $out -Recurse -Force }
foreach ($archive in 'NowFound-publish.zip', 'NowFound-publish.tar.gz') {
    Remove-Item -LiteralPath (Join-Path $dist $archive) -Force -ErrorAction SilentlyContinue
}

Invoke-Dotnet publish (Join-Path $root 'src\NowFound.Web\NowFound.Web.csproj') -c Release -o (Join-Path $out 'web')
Invoke-Dotnet publish (Join-Path $root 'src\NowFound.Indexer\NowFound.Indexer.csproj') -c Release -o (Join-Path $out 'indexer')

# Keep only the native runtimes for the supported platforms (runtimes/<rid>/...).
foreach ($app in 'web', 'indexer') {
    $runtimes = Join-Path $out "$app\runtimes"
    if (-not (Test-Path -LiteralPath $runtimes)) { continue }
    Get-ChildItem -LiteralPath $runtimes -Directory |
        Where-Object { $KeepRuntimes -notcontains $_.Name } |
        ForEach-Object {
            Write-Host "Removing $app\runtimes\$($_.Name)"
            Remove-Item -LiteralPath $_.FullName -Recurse -Force
        }
}

# Not needed in production.
Remove-Item -LiteralPath (Join-Path $out 'web\appsettings.Development.json') -Force -ErrorAction SilentlyContinue
Get-ChildItem -LiteralPath $out -Recurse -Filter *.pdb | Remove-Item -Force

# HTTPS: Production Kestrel endpoint. The certificate is generated at install time (no private key is shipped; see INSTALL.md).
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'appsettings.Production.json') -Destination (Join-Path $out 'web')
Copy-Item -LiteralPath (Join-Path $root 'tools\new-cert.ps1') -Destination $out

$files = 'INSTALL.md', 'run-web.bat', 'run-indexer.bat', 'register-indexer-task.ps1', 'register-web-service.ps1'
if ($IncludeLinux) {
    $files += 'run-indexer.sh', 'install-linux.sh', 'nowfound.service', 'nowfound-indexer.service', 'nowfound-indexer.timer'
}
foreach ($f in $files) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $f) -Destination $out
}

Compress-Archive -Path $out -DestinationPath (Join-Path $dist 'NowFound-publish.zip') -Force
Write-Host "Done: $(Join-Path $dist 'NowFound-publish.zip')"

if (-not $IncludeLinux) { return }

# Linux package (extract, then: sudo bash install-linux.sh). Scripts are run with bash, so no exec bit is needed.
# Use the Windows bsdtar explicitly: a GNU tar earlier on PATH (e.g. Git for Windows) misreads "D:\..." as a remote host.
$tar = Join-Path $env:SystemRoot 'System32\tar.exe'
if (-not (Test-Path -LiteralPath $tar)) { $tar = 'tar' }
& $tar -czf (Join-Path $dist 'NowFound-publish.tar.gz') -C $dist NowFound
if ($LASTEXITCODE -ne 0) { throw "tar failed ($LASTEXITCODE)" }
Write-Host "Done: $(Join-Path $dist 'NowFound-publish.tar.gz')"
