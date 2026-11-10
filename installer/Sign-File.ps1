#requires -Version 7.0
param([Parameter(Mandatory)][string]$FilePath)
$ErrorActionPreference = 'Stop'
$thumbprint = $env:FRAMEFORGE_SIGN_CERT_SHA1
$tool = $env:FRAMEFORGE_SIGNTOOL_PATH
$timestamp = $env:FRAMEFORGE_TIMESTAMP_URL
if ($thumbprint -notmatch '^[A-Fa-f0-9]{40}$' -or -not $tool -or -not $timestamp) {
    throw 'Signing requires a certificate thumbprint, signtool.exe and timestamp URL.'
}
& $tool sign /sha1 $thumbprint /fd SHA256 /tr $timestamp /td SHA256 $FilePath
if ($LASTEXITCODE -ne 0) { throw 'Authenticode signing failed.' }
$signature = Get-AuthenticodeSignature -LiteralPath $FilePath
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Thumbprint -ne $thumbprint) {
    throw 'Authenticode signature verification failed.'
}
