# Signs exes with the local self-signed code-signing cert, creating the cert on first
# use. Self-signed means machines that haven't trusted the cert still warn — this exists
# for local/dev builds and AV heuristics, not SmartScreen reputation. For distribution,
# get a real certificate (SignPath Foundation signs qualifying open-source for free).
#
#   powershell -File tools\sign.ps1 path\to\App.exe [more.exe ...]

param([Parameter(Mandatory, ValueFromRemainingArguments)][string[]]$Path)

$subject = 'CN=blancodagoat Code Signing'

$cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert | Where-Object Subject -eq $subject | Select-Object -First 1
if (-not $cert) {
    Write-Host "Creating self-signed code-signing certificate: $subject"
    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $subject `
        -KeyExportPolicy Exportable -CertStoreLocation Cert:\CurrentUser\My `
        -NotAfter (Get-Date).AddYears(5)
}

$signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
    Sort-Object FullName | Select-Object -Last 1
if (-not $signtool) { throw 'signtool.exe not found - install the Windows SDK Signing Tools.' }

& $signtool.FullName sign /sha1 $cert.Thumbprint /fd sha256 /td sha256 /tr http://timestamp.digicert.com @Path
exit $LASTEXITCODE
