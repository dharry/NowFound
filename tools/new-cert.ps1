# Create a self-signed HTTPS certificate (PFX + CER) for NowFound Web (Kestrel).
#   .\tools\new-cert.ps1                                         nowfound.invalid + localhost, 10 years
#   .\tools\new-cert.ps1 -DnsName nowfound.invalid,localhost      Use your own host names
#   .\tools\new-cert.ps1 -OutDir D:\NowFound\certs -Password "secret"
#   .\tools\new-cert.ps1 -Csr -DnsName nowfound.invalid           Create a CSR for your internal CA (AD CS)
#   .\tools\new-cert.ps1 -Import server.cer -Password "secret"   Turn the issued certificate into certs\nowfound.pfx
# After replacing the PFX, set the same password in web\appsettings.Production.json and restart the service.
# (comments/messages are ASCII on purpose: Windows PowerShell 5.1 code page safe)
[CmdletBinding(PositionalBinding = $false)]
param(
    [string[]]$DnsName = @('nowfound.invalid', 'localhost'),
    [int]$Years = 10,
    [string]$Password = 'nowfound',
    [string]$OutDir = '',
    [switch]$Csr,
    [string]$Import = '',
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$RemainingArgs
)

function Show-Usage {
    Write-Host @'
Usage: new-cert.ps1 [-DnsName <name>[,<name>...]] [-Years <n>] [-Password <text>] [-OutDir <dir>]
       new-cert.ps1 -Csr -DnsName <name>[,<name>...] [-OutDir <dir>]
       new-cert.ps1 -Import <issued.cer> -Password <text> [-OutDir <dir>]

Without -Csr / -Import: creates a self-signed HTTPS certificate (nowfound.pfx + nowfound.cer)
for NowFound Web (Kestrel). For testing only.

Internal CA (AD CS) workflow (run both steps on the NowFound server, in an elevated PowerShell):
  1. -Csr     Creates nowfound.csr. The private key stays on this computer (pending request).
              Submit the CSR to your CA and save the issued certificate (e.g. server.cer).
  2. -Import  Accepts the issued certificate and exports nowfound.pfx
              (private key and certificate chain, protected by -Password).

Options:
  -DnsName <names>    Host names in the certificate (default: nowfound.invalid, localhost; required with -Csr)
  -Years <n>          Validity in years of the self-signed certificate (default: 10)
  -Password <text>    PFX password (default: nowfound; required with -Import)
  -Csr                Create a certificate signing request
  -Import <file>      Issued certificate (.cer) from the CA
  -OutDir <dir>       Output folder (default: deploy\certs in the repository, certs next to this script in a package)
  -h, --help, -help   Show this usage

Examples:
  .\new-cert.ps1 -DnsName nowfound.invalid,localhost
  .\new-cert.ps1 -Csr -DnsName nowfound.invalid,nowfound
  .\new-cert.ps1 -Import C:\temp\server.cer -Password "secret"

After replacing the PFX, set the same password in web\appsettings.Production.json and restart the service.
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
if (-not $OutDir) {
    # Repository (tools\ next to deploy\) -> deploy\certs ; bundled package -> certs next to this script.
    $here = Split-Path -Parent $MyInvocation.MyCommand.Path
    $repoDeploy = Join-Path (Split-Path -Parent $here) 'deploy'
    $OutDir = if (Test-Path -LiteralPath $repoDeploy) { Join-Path $repoDeploy 'certs' } else { Join-Path $here 'certs' }
}
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

function Assert-Administrator {
    $principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Administrator privileges are required. Run this script from an elevated PowerShell.'
    }
}

if ($Csr -and $Import) { throw '-Csr and -Import cannot be used together.' }

if ($Csr) {
    # Step 1 for an internal CA: create the request. The key pair is created in the computer store (exportable).
    Assert-Administrator
    if (-not $PSBoundParameters.ContainsKey('DnsName')) { throw '-DnsName is required with -Csr (the names users type in the browser).' }
    $infPath = Join-Path ([IO.Path]::GetTempPath()) "nowfound-request-$([guid]::NewGuid().ToString('N')).inf"
    $csrPath = Join-Path $OutDir 'nowfound.csr'
    $san = ($DnsName | ForEach-Object { "_continue_ = `"dns=$_&`"" }) -join "`r`n"
    $inf = @"
[Version]
Signature = "`$Windows NT`$"

[NewRequest]
Subject = "CN=$($DnsName[0])"
KeyLength = 2048
KeySpec = 1
KeyUsage = 0xA0
Exportable = TRUE
MachineKeySet = TRUE
ProviderName = "Microsoft RSA SChannel Cryptographic Provider"
RequestType = PKCS10
HashAlgorithm = SHA256

[EnhancedKeyUsageExtension]
OID = 1.3.6.1.5.5.7.3.1

[Extensions]
2.5.29.17 = "{text}"
$san
"@
    try {
        Set-Content -LiteralPath $infPath -Value $inf -Encoding ASCII
        if (Test-Path -LiteralPath $csrPath) { Remove-Item -LiteralPath $csrPath -Force }
        certreq.exe -new $infPath $csrPath | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'certreq -new failed.' }
    }
    finally {
        Remove-Item -LiteralPath $infPath -Force -ErrorAction SilentlyContinue
    }

    Write-Host "Created: $csrPath"
    Write-Host "Names: $($DnsName -join ', ')"
    Write-Host 'Next: submit the CSR to your CA, save the issued certificate (.cer), then run on this same computer:'
    Write-Host '  .\new-cert.ps1 -Import <issued.cer> -Password <text>'
    return
}

if ($Import) {
    # Step 2 for an internal CA: bind the issued certificate to the pending request and export it as a PFX.
    Assert-Administrator
    if (-not $PSBoundParameters.ContainsKey('Password')) { throw '-Password is required with -Import (do not use the default password).' }
    if (-not (Test-Path -LiteralPath $Import)) { throw "File not found: $Import" }
    $issuedPath = (Resolve-Path -LiteralPath $Import).Path
    $issued = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($issuedPath)
    certreq.exe -accept $issuedPath | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'certreq -accept failed. Run it on the computer where -Csr was run, with the certificate issued for that CSR.' }

    $stored = Get-Item -LiteralPath "Cert:\LocalMachine\My\$($issued.Thumbprint)" -ErrorAction SilentlyContinue
    if (-not $stored -or -not $stored.HasPrivateKey) { throw 'The certificate was not bound to a private key. It must be issued for the CSR created by this script on this computer.' }
    $pfxPath = Join-Path $OutDir 'nowfound.pfx'
    try {
        $secure = ConvertTo-SecureString -String $Password -Force -AsPlainText
        Export-PfxCertificate -Cert $stored -FilePath $pfxPath -Password $secure -ChainOption BuildChain | Out-Null
    }
    finally {
        # Keep the private key only in the PFX.
        Remove-Item -LiteralPath $stored.PSPath -Force
    }

    Write-Host "Created: $pfxPath (chain included)"
    Write-Host "Subject: $($issued.Subject)  Expires: $($issued.NotAfter.ToString('yyyy-MM-dd'))"
    Write-Host 'Next: set the same password in web\appsettings.Production.json and restart the service.'
    return
}

$cert = New-SelfSignedCertificate -Subject "CN=$($DnsName[0])" -DnsName $DnsName `
    -KeyAlgorithm RSA -KeyLength 2048 -HashAlgorithm SHA256 -KeyExportPolicy Exportable `
    -NotAfter (Get-Date).AddYears($Years) -CertStoreLocation 'Cert:\CurrentUser\My' `
    -FriendlyName 'NowFound Web'
try {
    $secure = ConvertTo-SecureString -String $Password -Force -AsPlainText
    Export-PfxCertificate -Cert $cert -FilePath (Join-Path $OutDir 'nowfound.pfx') -Password $secure | Out-Null
    Export-Certificate -Cert $cert -FilePath (Join-Path $OutDir 'nowfound.cer') | Out-Null
}
finally {
    Remove-Item -LiteralPath "Cert:\CurrentUser\My\$($cert.Thumbprint)" -Force
}

Write-Host "Created: $OutDir\nowfound.pfx (password: $Password), nowfound.cer"
Write-Host "Names: $($DnsName -join ', ')  Expires: $($cert.NotAfter.ToString('yyyy-MM-dd'))"
