#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$FilePath,
    [Parameter(Mandatory)][string]$Version,
    [switch]$Uninstaller
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$file = Get-Item -LiteralPath $FilePath
$signature = Get-AuthenticodeSignature -LiteralPath $file.FullName
if ($signature.Status -ne 'Valid' -or -not $signature.SignerCertificate -or
    $signature.SignerCertificate.GetNameInfo([Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false) -ne 'SignPath Foundation') {
    throw "A valid SignPath Foundation signature is required: $FilePath"
}
if (-not $signature.TimeStamperCertificate) { throw "A timestamp is required: $FilePath" }
$metadata = $file.VersionInfo
if ($metadata.ProductName.Trim() -ne 'FrameForge Recorder' -or $metadata.ProductVersion.Trim() -ne $Version) {
    throw "Signed file product/version mismatch: $FilePath"
}
# Inno Setup uninstallers retain the compiler's internal file version.
if (-not $Uninstaller -and $metadata.FileVersion.Trim() -ne "$Version.0") {
    throw "Signed file version mismatch: $FilePath"
}
