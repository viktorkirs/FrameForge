#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ArtifactDirectory,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{40}$')][string]$CommitSha,
    [switch]$ValidateOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = 'viktorkirs/FrameForge'
$installers = @(Get-ChildItem -LiteralPath $ArtifactDirectory -Filter 'FrameForge-Setup-*-x64.exe' -File)
if ($installers.Count -ne 1) { throw 'Expected exactly one versioned installer in the CI artifact.' }
$setup = $installers[0]
if ($setup.Name -notmatch '^FrameForge-Setup-(\d+\.\d+\.\d+)-x64\.exe$') { throw 'Unexpected installer name.' }
$version = $Matches[1]
$checksum = (Get-Content -LiteralPath "$($setup.FullName).sha256" -Raw).Trim()
$hash = (Get-FileHash -LiteralPath $setup.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
if ($checksum -ne "$hash  $($setup.Name)") { throw 'Installer checksum mismatch.' }
if ($setup.VersionInfo.ProductName.Trim() -ne 'FrameForge Recorder' -or
    $setup.VersionInfo.ProductVersion.Trim() -ne $version -or $setup.VersionInfo.FileVersion.Trim() -ne "$version.0") {
    throw 'Installer metadata does not match its versioned filename.'
}
$signature = Get-AuthenticodeSignature -LiteralPath $setup.FullName
if ($signature.Status -notin @('NotSigned', 'Valid')) { throw 'The installer has an invalid Authenticode signature.' }
if ($ValidateOnly) {
    [pscustomobject]@{ Version = $version; SHA256 = $hash; Signature = [string]$signature.Status }
    return
}

if (-not $env:GH_TOKEN) { throw 'Publishing requires GH_TOKEN with repository contents write permission.' }
Get-Command gh -ErrorAction Stop | Out-Null
function Invoke-Gh([string[]]$Arguments) {
    $result = & gh @Arguments
    if ($LASTEXITCODE -ne 0) { throw "GitHub CLI failed: $($Arguments[0])" }
    return $result
}
# Older concurrent builds remain downloadable as Actions artifacts, but must not
# replace a newer main build at the permanent download URL.
$mainSha = Invoke-Gh @('api', "repos/$repository/git/ref/heads/main", '--jq', '.object.sha')
if ($mainSha.Trim() -ne $CommitSha) {
    Write-Host 'A newer main commit exists. Keeping this installer in its Actions artifact only.'
    return
}

$releaseDirectory = Join-Path (Split-Path $setup.DirectoryName -Parent) 'latest-release'
New-Item -ItemType Directory -Path $releaseDirectory -Force | Out-Null
$assetName = 'FrameForge-Setup-x64.exe'
$alias = Join-Path $releaseDirectory $assetName
Copy-Item -LiteralPath $setup.FullName -Destination $alias
[IO.File]::WriteAllText("$alias.sha256", "$hash  $assetName`n")
$manifestPath = Join-Path $releaseDirectory 'FrameForge-update.json'
$manifest = [ordered]@{
    version = $version
    downloadUrl = "https://github.com/$repository/releases/download/latest/$assetName"
    sha256 = $hash
    size = $setup.Length
    commit = $CommitSha
} | ConvertTo-Json
[IO.File]::WriteAllText($manifestPath, $manifest)
$signatureText = if ($signature.Status -eq 'Valid') { 'Valid Authenticode signature.' } else {
    'This installer is unsigned. SignPath Foundation signing is awaiting approval; Windows may show a SmartScreen warning.'
}
$notesPath = Join-Path $releaseDirectory 'release-notes.md'
$notes = @"
Latest successful Windows x64 installer built from the main branch.

- Application version: **$version**
- Source commit: [$CommitSha](https://github.com/$repository/commit/$CommitSha)
- Creator: **Victor Kirsanov**
- SHA256: $hash

$signatureText

[Code signing policy](https://github.com/$repository/blob/main/CODE_SIGNING_POLICY.md) · [Privacy policy](https://github.com/$repository/blob/main/PRIVACY.md)

This rolling build updates after a successful push to main. Versioned releases remain separate.
"@
[IO.File]::WriteAllText($notesPath, $notes)
$null = & gh release view latest --repo $repository --json tagName 2>$null
if ($LASTEXITCODE -ne 0) {
    Invoke-Gh @('release', 'create', 'latest', '--repo', $repository, '--target', $CommitSha,
        '--title', "Latest installer - $version", '--notes-file', $notesPath, '--prerelease') | Out-Host
}
Invoke-Gh @('release', 'upload', 'latest', $alias, "$alias.sha256", '--repo', $repository, '--clobber') | Out-Host
$release = (Invoke-Gh @('api', "repos/$repository/releases/tags/latest")) | ConvertFrom-Json
foreach ($file in @($alias, "$alias.sha256")) {
    $name = [IO.Path]::GetFileName($file)
    $asset = @($release.assets | Where-Object name -eq $name)
    $expectedDigest = 'sha256:' + (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($asset.Count -ne 1 -or $asset[0].state -ne 'uploaded' -or
        $asset[0].size -ne (Get-Item -LiteralPath $file).Length -or $asset[0].digest -ne $expectedDigest) {
        throw "Release asset verification failed: $name"
    }
}
# Publish the version notice only after the installer and checksum have been verified.
Invoke-Gh @('release', 'upload', 'latest', $manifestPath, '--repo', $repository, '--clobber') | Out-Host
$manifestRelease = (Invoke-Gh @('api', "repos/$repository/releases/tags/latest")) | ConvertFrom-Json
$manifestAsset = @($manifestRelease.assets | Where-Object name -eq 'FrameForge-update.json')
$manifestDigest = 'sha256:' + (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($manifestAsset.Count -ne 1 -or $manifestAsset[0].state -ne 'uploaded' -or
    $manifestAsset[0].size -ne (Get-Item -LiteralPath $manifestPath).Length -or $manifestAsset[0].digest -ne $manifestDigest) {
    throw 'Update manifest verification failed.'
}
# Only this explicitly mutable rolling tag is moved; versioned release tags are untouched.
Invoke-Gh @('api', '--method', 'PATCH', "repos/$repository/git/refs/tags/latest",
    '--raw-field', "sha=$CommitSha", '--field', 'force=true', '--silent') | Out-Host
Invoke-Gh @('release', 'edit', 'latest', '--repo', $repository,
    '--title', "Latest installer - $version", '--notes-file', $notesPath, '--prerelease') | Out-Host
$url = "https://github.com/$repository/releases/download/latest/$assetName"
Write-Host "Published installer $version from ${CommitSha}: $url"
if ($env:GITHUB_STEP_SUMMARY) { "[Download the latest installer]($url) — version $version, commit $CommitSha" >> $env:GITHUB_STEP_SUMMARY }
