#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$IsccPath,
    [switch]$Sign,
    [string]$CertificateThumbprint = $env:FRAMEFORGE_SIGN_CERT_SHA1,
    [string]$SignToolPath = $env:FRAMEFORGE_SIGNTOOL_PATH,
    [string]$TimestampServer = 'http://timestamp.digicert.com',
    [ValidateSet('Build', 'PrepareSignPath', 'CompleteSignPath')]
    [string]$Mode = 'Build',
    [string]$SigningDirectory,
    [string]$SignedFilesDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
[xml]$props = Get-Content -LiteralPath (Join-Path $repoRoot 'Directory.Build.props') -Raw
$appVersion = [string]$props.Project.PropertyGroup.Version
if ($appVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'Expected a three-part application version.' }
if ($Sign -and $Mode -ne 'Build') { throw 'Local certificate signing and SignPath are separate build modes.' }
if (-not $SigningDirectory) { $SigningDirectory = Join-Path $repoRoot 'artifacts\signpath' }
$SigningDirectory = [IO.Path]::GetFullPath($SigningDirectory)
$statePath = Join-Path $SigningDirectory 'build.json'
$ownedFiles = @('FrameForge.exe', 'FrameForge.dll', 'FrameForge.Core.dll',
    'FrameForge.Capture.dll', 'FrameForge.Encoding.dll', 'FrameForge.Diagnostics.dll')
if ($Mode -eq 'PrepareSignPath' -and (Test-Path -LiteralPath $SigningDirectory)) {
    throw 'Use a new signing directory for each build; old signing inputs must not be reused.'
}
if ($Mode -eq 'CompleteSignPath') {
    if (-not $SignedFilesDirectory) { throw 'CompleteSignPath requires -SignedFilesDirectory.' }
    $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
    if ($state.Version -ne $appVersion) { throw 'Version changed after preparing the signing inputs.' }
    $payload = [IO.Path]::GetFullPath($state.Payload)
    $payloadRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts\installer-payload')) + [IO.Path]::DirectorySeparatorChar
    if (-not $payload.StartsWith($payloadRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Signing payload is outside the build directory.' }
    $IsccPath = $state.Compiler
    # Verify the entire returned batch before changing the prepared payload.
    foreach ($file in $ownedFiles) {
        & (Join-Path $PSScriptRoot 'Test-SignPathSignature.ps1') -FilePath (Join-Path $SignedFilesDirectory "app\$file") -Version $appVersion
    }
    $signedUninstaller = Join-Path $SignedFilesDirectory 'uninstaller\FrameForge-Uninstaller.exe'
    & (Join-Path $PSScriptRoot 'Test-SignPathSignature.ps1') -FilePath $signedUninstaller -Version $appVersion -Uninstaller
    foreach ($file in $ownedFiles) {
        Copy-Item -LiteralPath (Join-Path $SignedFilesDirectory "app\$file") -Destination (Join-Path $payload $file)
    }
    Copy-Item -LiteralPath $signedUninstaller -Destination (Join-Path $SigningDirectory "uninstaller\$($state.Uninstaller)")
}
if ($Sign) {
    if ($CertificateThumbprint -notmatch '^[A-Fa-f0-9]{40}$') { throw 'Signing requested but no valid certificate thumbprint was supplied.' }
    $certificate = Get-Item -LiteralPath "Cert:\CurrentUser\My\$CertificateThumbprint" -ErrorAction Stop
    $usages = @($certificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.37' } |
        ForEach-Object { $_.EnhancedKeyUsages } | ForEach-Object { $_.Value })
    if (-not $certificate.HasPrivateKey -or $certificate.NotAfter -le (Get-Date) -or
        '1.3.6.1.5.5.7.3.3' -notin $usages) {
        throw 'Signing requires a valid code-signing certificate with an accessible private key.'
    }
    if (-not $SignToolPath -or -not (Test-Path -LiteralPath $SignToolPath)) { throw 'Supply the Windows SDK signtool.exe path with -SignToolPath.' }
    $timestampUri = $null
    if (-not [Uri]::TryCreate($TimestampServer, [UriKind]::Absolute, [ref]$timestampUri) -or
        $timestampUri.Scheme -notin @('http', 'https')) { throw 'Supply an HTTP(S) RFC3161 timestamp URL.' }
}

if (-not $IsccPath) {
    # NuGet distribution is unpacked locally; installing a build tool into Windows is unnecessary.
    $toolVersion = '6.7.3'
    $packageHash = 'F780898E402FF80612CC8D9FCB8C6E02932BD1CB4C900FFDAA31F9341CFB49F4'
    $toolRoot = Join-Path $repoRoot "artifacts\toolchain\inno-nuget-$toolVersion"
    $IsccPath = Join-Path $toolRoot 'tools\ISCC.exe'
    if (-not (Test-Path -LiteralPath $IsccPath)) {
        $package = Join-Path $repoRoot "artifacts\toolchain\tools.innosetup.$toolVersion.nupkg"
        New-Item -ItemType Directory -Path (Split-Path $package) -Force | Out-Null
        Invoke-WebRequest -Uri "https://api.nuget.org/v3-flatcontainer/tools.innosetup/$toolVersion/tools.innosetup.$toolVersion.nupkg" -OutFile $package
        if ((Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash -ne $packageHash) {
            throw 'Inno Setup package SHA256 mismatch.'
        }
        & dotnet nuget verify $package --all
        if ($LASTEXITCODE -ne 0) { throw 'Inno Setup NuGet signature verification failed.' }
        [IO.Compression.ZipFile]::ExtractToDirectory($package, $toolRoot, $true)
    }
}
$IsccPath = (Resolve-Path -LiteralPath $IsccPath).Path
if ($Mode -ne 'CompleteSignPath') {
    $payload = Join-Path $repoRoot ("artifacts\installer-payload\{0}-{1}" -f $appVersion, [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $payload -Force | Out-Null
    & dotnet publish (Join-Path $repoRoot 'src\FrameForge.UI\FrameForge.UI.csproj') -c Release -r win-x64 --self-contained true -p:DebugType=None -p:DebugSymbols=false -p:IncludeSourceRevisionInInformationalVersion=false -o $payload --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Self-contained application publish failed.' }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Installed-README.txt') -Destination (Join-Path $payload 'README.txt')
    # Include notices from the exact runtime packs selected by publish, not from an
    # unrelated SDK installed on the build host.
    $assets = Get-Content -LiteralPath (Join-Path $repoRoot 'src\FrameForge.UI\obj\project.assets.json') -Raw | ConvertFrom-Json
    $runtimeConfig = Get-Content -LiteralPath (Join-Path $payload 'FrameForge.runtimeconfig.json') -Raw | ConvertFrom-Json
    foreach ($runtime in @('Microsoft.NETCore.App.Runtime.win-x64', 'Microsoft.WindowsDesktop.App.Runtime.win-x64')) {
        $frameworkName = $runtime.Replace('.Runtime.win-x64', '')
        $framework = $runtimeConfig.runtimeOptions.includedFrameworks | Where-Object name -eq $frameworkName | Select-Object -First 1
        if (-not $framework) { throw "Published runtime version not found: $runtime" }
        $folder = $null
        foreach ($root in $assets.packageFolders.PSObject.Properties.Name) {
            $candidate = Join-Path $root ($runtime.ToLowerInvariant() + '/' + $framework.version)
            if (Test-Path -LiteralPath $candidate) { $folder = $candidate; break }
        }
        if (-not $folder) { throw "Runtime license folder not found: $runtime" }
        foreach ($notice in Get-ChildItem -LiteralPath $folder -File | Where-Object { $_.Name -match '^(LICENSE|THIRD-PARTY-NOTICES)(\.TXT)?$' }) {
            $noticeName = [IO.Path]::GetFileNameWithoutExtension($notice.Name) + '.txt'
            Copy-Item -LiteralPath $notice.FullName -Destination (Join-Path $payload "licenses\$runtime-$noticeName")
        }
    }
    foreach ($required in @('FrameForge.exe', 'FrameForge.dll', 'coreclr.dll', 'NAudio-LICENSE.txt')) {
        if (-not (Test-Path -LiteralPath (Join-Path $payload $required))) { throw "Payload missing $required." }
    }
}
$output = Join-Path $repoRoot 'dist'
New-Item -ItemType Directory -Path $output -Force | Out-Null
if ((Get-Item -LiteralPath (Join-Path $payload 'FrameForge.exe')).VersionInfo.FileVersion -ne "$appVersion.0") {
    throw 'Published executable version does not match Directory.Build.props.'
}
$compilerArgs = @("/DAppVersion=$appVersion", "/DPayloadDir=$payload", "/DRepoRoot=$repoRoot", "/DInstallerOutputDir=$output", '/Q')
if ($Mode -ne 'Build') {
    $uninstallerDir = Join-Path $SigningDirectory 'uninstaller'
    New-Item -ItemType Directory -Path $uninstallerDir -Force | Out-Null
    $compilerArgs += @('/DExternalSignBuild', "/DExternalUninstallerDir=$uninstallerDir")
}
if ($Sign) {
    $env:FRAMEFORGE_SIGN_CERT_SHA1 = $CertificateThumbprint
    $env:FRAMEFORGE_SIGNTOOL_PATH = $SignToolPath
    $env:FRAMEFORGE_TIMESTAMP_URL = $TimestampServer
    & (Join-Path $PSScriptRoot 'Sign-File.ps1') -FilePath (Join-Path $payload 'FrameForge.exe')
    $command = '$q' + (Get-Process -Id $PID).Path + '$q -NoProfile -File $q' + (Join-Path $PSScriptRoot 'Sign-File.ps1') + '$q -FilePath $f'
    $compilerArgs += @('/DSignBuild', "/Sframeforge=$command")
}
$compilerLog = & $IsccPath @compilerArgs (Join-Path $PSScriptRoot 'FrameForge.iss') 2>&1
$compilerExit = $LASTEXITCODE
$compilerLog | Out-Host
if ($Mode -eq 'PrepareSignPath') {
    # ISCC deliberately stops before packaging when the external uninstaller is
    # first generated. Only this specific failure is an expected preparation result.
    $uninstallers = @(Get-ChildItem -LiteralPath $uninstallerDir -Filter 'uninst-*.e32' -File)
    if ($compilerExit -ne 2 -or $uninstallers.Count -ne 1 -or
        ($compilerLog | Out-String) -notmatch 'Signed uninstaller mode is enabled') {
        throw 'Inno Setup did not generate the expected unsigned uninstaller.'
    }
    $unsigned = Join-Path $SigningDirectory 'unsigned'
    New-Item -ItemType Directory -Path (Join-Path $unsigned 'app'), (Join-Path $unsigned 'uninstaller') -Force | Out-Null
    foreach ($file in $ownedFiles) {
        $source = Get-Item -LiteralPath (Join-Path $payload $file)
        if ($source.VersionInfo.ProductName.Trim() -ne 'FrameForge Recorder' -or
            $source.VersionInfo.ProductVersion.Trim() -ne $appVersion -or
            $source.VersionInfo.FileVersion.Trim() -ne "$appVersion.0") {
            throw "Signing input metadata mismatch: $file"
        }
        Copy-Item -LiteralPath $source.FullName -Destination (Join-Path $unsigned 'app')
    }
    Copy-Item -LiteralPath $uninstallers[0].FullName -Destination (Join-Path $unsigned 'uninstaller\FrameForge-Uninstaller.exe')
    [ordered]@{ Version = $appVersion; Payload = $payload; Compiler = $IsccPath; Uninstaller = $uninstallers[0].Name } |
        ConvertTo-Json | Set-Content -LiteralPath $statePath -Encoding utf8
    [pscustomobject]@{ UnsignedInputs = $unsigned; State = $statePath }
    exit 0
}
if ($compilerExit -ne 0) { throw 'Inno Setup compilation failed.' }
$setup = Join-Path $output "FrameForge-Setup-$appVersion-x64.exe"
if ($Sign -and (Get-AuthenticodeSignature -LiteralPath $setup).Status -ne 'Valid') { throw 'Setup signature is invalid.' }
$hash = Get-FileHash -LiteralPath $setup -Algorithm SHA256
[IO.File]::WriteAllText("$setup.sha256", "$($hash.Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($setup))`n")
[pscustomobject]@{ Installer = $setup; SHA256 = $hash.Hash; Payload = $payload }
