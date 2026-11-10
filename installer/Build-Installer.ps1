#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$IsccPath,
    [switch]$Sign,
    [string]$CertificateThumbprint = $env:FRAMEFORGE_SIGN_CERT_SHA1,
    [string]$SignToolPath = $env:FRAMEFORGE_SIGNTOOL_PATH,
    [string]$TimestampServer = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
[xml]$props = Get-Content -LiteralPath (Join-Path $repoRoot 'Directory.Build.props') -Raw
$appVersion = [string]$props.Project.PropertyGroup.Version
if ($appVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'Expected a three-part application version.' }
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
$payload = Join-Path $repoRoot ("artifacts\installer-payload\{0}-{1}" -f $appVersion, [Guid]::NewGuid().ToString('N'))
$output = Join-Path $repoRoot 'dist'
New-Item -ItemType Directory -Path $payload, $output -Force | Out-Null
& dotnet publish (Join-Path $repoRoot 'src\FrameForge.UI\FrameForge.UI.csproj') -c Release -r win-x64 --self-contained true -p:DebugType=None -p:DebugSymbols=false -o $payload --nologo
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
if ((Get-Item -LiteralPath (Join-Path $payload 'FrameForge.exe')).VersionInfo.FileVersion -ne "$appVersion.0") {
    throw 'Published executable version does not match Directory.Build.props.'
}
$compilerArgs = @("/DAppVersion=$appVersion", "/DPayloadDir=$payload", "/DRepoRoot=$repoRoot", "/DInstallerOutputDir=$output", '/Q')
if ($Sign) {
    $env:FRAMEFORGE_SIGN_CERT_SHA1 = $CertificateThumbprint
    $env:FRAMEFORGE_SIGNTOOL_PATH = $SignToolPath
    $env:FRAMEFORGE_TIMESTAMP_URL = $TimestampServer
    & (Join-Path $PSScriptRoot 'Sign-File.ps1') -FilePath (Join-Path $payload 'FrameForge.exe')
    $command = '$q' + (Get-Process -Id $PID).Path + '$q -NoProfile -File $q' + (Join-Path $PSScriptRoot 'Sign-File.ps1') + '$q -FilePath $f'
    $compilerArgs += @('/DSignBuild', "/Sframeforge=$command")
}
& $IsccPath @compilerArgs (Join-Path $PSScriptRoot 'FrameForge.iss')
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup compilation failed.' }
$setup = Join-Path $output "FrameForge-Setup-$appVersion-x64.exe"
if ($Sign -and (Get-AuthenticodeSignature -LiteralPath $setup).Status -ne 'Valid') { throw 'Setup signature is invalid.' }
$hash = Get-FileHash -LiteralPath $setup -Algorithm SHA256
[IO.File]::WriteAllText("$setup.sha256", "$($hash.Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($setup))`n")
[pscustomobject]@{ Installer = $setup; SHA256 = $hash.Hash; Payload = $payload }
