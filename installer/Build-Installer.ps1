#requires -Version 7.0
[CmdletBinding()]
param([string]$IsccPath)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
[xml]$props = Get-Content -LiteralPath (Join-Path $repoRoot 'Directory.Build.props') -Raw
$appVersion = [string]$props.Project.PropertyGroup.Version
if ($appVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'Expected a three-part application version.' }

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
foreach ($required in @('FrameForge.exe', 'FrameForge.dll', 'coreclr.dll', 'NAudio-LICENSE.txt')) {
    if (-not (Test-Path -LiteralPath (Join-Path $payload $required))) { throw "Payload missing $required." }
}
if ((Get-Item -LiteralPath (Join-Path $payload 'FrameForge.exe')).VersionInfo.FileVersion -ne "$appVersion.0") {
    throw 'Published executable version does not match Directory.Build.props.'
}
& $IsccPath "/DAppVersion=$appVersion" "/DPayloadDir=$payload" "/DRepoRoot=$repoRoot" "/DInstallerOutputDir=$output" (Join-Path $PSScriptRoot 'FrameForge.iss')
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup compilation failed.' }
$setup = Join-Path $output "FrameForge-Setup-$appVersion-x64.exe"
$hash = Get-FileHash -LiteralPath $setup -Algorithm SHA256
[IO.File]::WriteAllText("$setup.sha256", "$($hash.Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($setup))`n")
[pscustomobject]@{ Installer = $setup; SHA256 = $hash.Hash; Payload = $payload }
