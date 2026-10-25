# Register NowFound Web as a Windows service (LocalService, automatic start, HTTPS).
# (comments/messages are ASCII on purpose: Windows PowerShell 5.1 code page safe)
#
#   .\register-web-service.ps1                 Register and start (https://<host>:5002)
#   .\register-web-service.ps1 -Seed           Create the initial admin on first start
#   .\register-web-service.ps1 -Unregister     Stop and remove the service
#
# Must be run from an elevated (Administrator) PowerShell.
# HTTPS endpoint and certificate: web\appsettings.Production.json (certs\nowfound.pfx; a self-signed one is generated
# on the first registration if it does not exist).
# Replace the certificate with your own for production use (see INSTALL.md).
[CmdletBinding(PositionalBinding = $false)]
param(
    [string]$ServiceName = 'NowFound',
    [switch]$Seed,
    [switch]$Unregister,
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$RemainingArgs
)

function Show-Usage {
    Write-Host @'
Usage: .\register-web-service.ps1 [-Seed] [-Unregister] [-ServiceName <name>]

Registers NowFound Web as a Windows service (LocalService, automatic start, HTTPS 5002),
grants that account access to the data, web and certs folders only,
opens the HTTPS port in Windows Firewall and starts the service.
Must be run from an elevated (Administrator) PowerShell.

Options:
  -Seed                 Create the initial administrator (admin@nowfound.invalid) on first start
  -Unregister           Stop and remove the service and its firewall rule
  -ServiceName <name>   Service name (default: NowFound)
  -h, --help, -help     Show this usage

HTTPS endpoint and certificate: web\appsettings.Production.json
(certs\nowfound.pfx: a self-signed certificate with a random password is generated on first registration
when none exists; replace it for production use).
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

$principalCheck = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principalCheck.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Administrator privileges are required. Run this script from an elevated PowerShell.'
}

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    if ($existing.Status -ne 'Stopped') { Stop-Service -Name $ServiceName -Force }
    sc.exe delete $ServiceName | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Failed to delete service '$ServiceName'." }
    Write-Host "Removed service '$ServiceName'."
    if ($Unregister) {
        Get-NetFirewallRule -DisplayName "$ServiceName HTTPS" -ErrorAction SilentlyContinue | Remove-NetFirewallRule
        return
    }
    Start-Sleep -Seconds 2
} elseif ($Unregister) {
    Write-Host "Service '$ServiceName' does not exist."
    return
}

$exe = Join-Path $PSScriptRoot 'web\NowFound.Web.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "NowFound.Web.exe not found: $exe" }

New-Service -Name $ServiceName -BinaryPathName "`"$exe`"" -DisplayName 'NowFound Web' `
    -Description 'NowFound file name search (Web console)' -StartupType Automatic | Out-Null

# The web console does not need SYSTEM: it never reads the searched folders (the Indexer does).
# Run it as the low-privilege built-in LocalService account (no password).
sc.exe config $ServiceName obj= 'NT AUTHORITY\LocalService' | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Failed to set the service account of '$ServiceName' to LocalService." }

# Grant LocalService only what it needs (S-1-5-19 is locale independent).
#   data  : Modify (SQLite databases and their -wal/-shm files; also inherited by files the Indexer creates)
#   web   : Read & execute (program files)
#   certs : Read & execute (TLS certificate)
function Grant-LocalService([string]$Path, [string]$Rights) {
    icacls.exe $Path /grant "*S-1-5-19:(OI)(CI)$Rights" /T /C /Q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Failed to grant LocalService access to: $Path" }
}
# HTTPS certificate: generated here, once per installation (no private key is shipped in the package).
# An existing certs\nowfound.pfx (your own, or one from an earlier install) is never overwritten.
$certDir = Join-Path $PSScriptRoot 'certs'
$pfxPath = Join-Path $certDir 'nowfound.pfx'
if (-not (Test-Path -LiteralPath $pfxPath)) {
    $newCert = Join-Path $PSScriptRoot 'new-cert.ps1'
    if (-not (Test-Path -LiteralPath $newCert)) { throw "new-cert.ps1 not found: $newCert" }

    $bytes = [byte[]]::new(24)
    # RandomNumberGenerator.Fill does not exist in Windows PowerShell 5.1 (.NET Framework): use Create().GetBytes.
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    $pfxPassword = [Convert]::ToBase64String($bytes).Replace('+', 'A').Replace('/', 'B').Replace('=', '')

    $names = [System.Collections.Generic.List[string]]::new()
    foreach ($n in 'nowfound.invalid', 'localhost', $env:COMPUTERNAME) { if ($n) { $names.Add($n) } }
    try {
        $fqdn = [Net.Dns]::GetHostEntry('').HostName
        if ($fqdn -and -not $names.Contains($fqdn)) { $names.Add($fqdn) }
    } catch { }

    & $newCert -OutDir $certDir -Password $pfxPassword -DnsName $names.ToArray()

    # Write the generated password into the Kestrel settings (the file is not overwritten by upgrades).
    $settingsPath = Join-Path $PSScriptRoot 'web\appsettings.Production.json'
    $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
    $settings.Kestrel.Endpoints.Https.Certificate.Password = $pfxPassword
    [IO.File]::WriteAllText($settingsPath, ($settings | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
    Write-Host "Generated HTTPS certificate: $pfxPath"
}

$dataDir = Join-Path $PSScriptRoot 'data'
New-Item -ItemType Directory -Force -Path $dataDir | Out-Null
Grant-LocalService $dataDir 'M'
Grant-LocalService (Join-Path $PSScriptRoot 'web') 'RX'
if (Test-Path -LiteralPath $certDir) { Grant-LocalService $certDir 'RX' }

# Restart automatically on failure (5 seconds later).
sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/5000/restart/5000 | Out-Null

# Service environment variables (multi-string value under the service key).
$environment = @('ASPNETCORE_ENVIRONMENT=Production')
if ($Seed) { $environment += 'Database__SeedData=true' }
Set-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName" -Name Environment -Value $environment -Type MultiString

# Open the HTTPS port in Windows Firewall (idempotent).
$ruleName = "$ServiceName HTTPS"
Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Protocol TCP -LocalPort 5002 -Action Allow | Out-Null

Start-Service -Name $ServiceName
Write-Host "Registered and started service '$ServiceName' (LocalService). URL: https://$($env:COMPUTERNAME):5002"
Write-Host "DB folder: $(Join-Path $PSScriptRoot 'data')"
