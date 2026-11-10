#requires -Version 7.0
[CmdletBinding()]
param([string]$SetupPath)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
[xml]$props = Get-Content -LiteralPath (Join-Path $repoRoot 'Directory.Build.props') -Raw
$version = [string]$props.Project.PropertyGroup.Version
if (-not $SetupPath) { $SetupPath = Join-Path $repoRoot "dist\FrameForge-Setup-$version-x64.exe" }
$SetupPath = (Resolve-Path -LiteralPath $SetupPath).Path
$registry = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{F9BFB42E-55A8-4333-BBAC-1C6CDE84FD8E}_is1'
if (Test-Path -LiteralPath $registry) { throw 'FrameForge is already installed. Refusing to modify an existing installation.' }
$work = Join-Path $repoRoot ("artifacts\installer-verification\" + [Guid]::NewGuid().ToString('N'))
$installed = Join-Path $work 'installed'
$group = 'FrameForge Installer Verification ' + [Guid]::NewGuid().ToString('N')
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) "$group\FrameForge Recorder.lnk"
$settings = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'FrameForge\settings.json'
$settingsBefore = if (Test-Path -LiteralPath $settings) { (Get-FileHash -LiteralPath $settings).Hash } else { '' }
New-Item -ItemType Directory -Path $work -Force | Out-Null
$results = [Collections.Generic.List[string]]::new()

function Start-CheckedProcess([string]$File, [string[]]$Arguments) {
    $info = [Diagnostics.ProcessStartInfo]::new($File)
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    return [Diagnostics.Process]::Start($info)
}
function Invoke-CheckedProcess([string]$File, [string[]]$Arguments) {
    $process = Start-CheckedProcess $File $Arguments
    try {
        if (-not $process.WaitForExit(45000)) { throw "Timed out running $([IO.Path]::GetFileName($File)); the process was left running." }
        return $process.ExitCode
    } finally { $process.Dispose() }
}
function Assert([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    $results.Add('PASS ' + $Message)
    Write-Host ('PASS ' + $Message)
}
$common = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/NOCLOSEAPPLICATIONS', '/SP-')
$installArgs = $common + @('/LANG=russian', "/DIR=$installed", "/GROUP=$group", '/TASKS=!desktopicon', "/LOG=$work\install.log")
$uninstaller = Join-Path $installed 'unins000.exe'
$uninstallArgs = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=$work\uninstall.log")
$didInstall = $false
try {
    $exit = Invoke-CheckedProcess $SetupPath $installArgs
    Assert ($exit -eq 0) 'Installer succeeds without elevation'
    $didInstall = $true
    $metadata = Get-ItemProperty -LiteralPath $registry
    Assert ($metadata.DisplayVersion -eq $version -and $metadata.Publisher -eq 'Victor Kirsanov') 'Windows uninstall entry has the correct version and manufacturer'
    $exe = Join-Path $installed 'FrameForge.exe'
    Assert ((Test-Path -LiteralPath $exe) -and (Test-Path -LiteralPath $uninstaller) -and (Test-Path -LiteralPath (Join-Path $installed 'coreclr.dll'))) 'Recorder, uninstaller and bundled .NET are present'
    Assert (-not (Test-Path -LiteralPath (Join-Path $installed 'portable.marker'))) 'Installed application uses normal user settings'
    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut($shortcut)
    Assert ((Test-Path -LiteralPath $shortcut) -and $link.TargetPath -eq $exe -and $link.WorkingDirectory -eq $installed) 'Start menu shortcut points to the installed recorder'
    [IO.File]::WriteAllText((Join-Path $installed 'user-note.txt'), 'User-created file must survive uninstall.')

    $probe = Join-Path $work 'selftest'
    $app = Start-CheckedProcess $exe @('--self-test', '--seconds', '3', '--output', $probe)
    try {
        $watch = [Diagnostics.Stopwatch]::StartNew()
        $seenGuard = $false
        while ($watch.Elapsed.TotalSeconds -lt 5 -and -not $app.HasExited) {
            $opened = $null
            if ([Threading.Mutex]::TryOpenExisting('Local\FrameForge.Recorder.Running', [ref]$opened)) {
                $opened.Dispose()
                $seenGuard = $true
                break
            }
            Start-Sleep -Milliseconds 20
        }
        Assert $seenGuard 'Installed recorder exposes its running-app guard'
        $blocked = Invoke-CheckedProcess $SetupPath ($common + @('/LANG=russian', "/DIR=$installed", "/LOG=$work\blocked-update.log"))
        Assert ($blocked -ne 0 -and -not $app.HasExited) 'Updating a running recorder is blocked without stopping it'
        if (-not $app.WaitForExit(30000)) { throw 'Installed recorder self-test timed out.' }
        Assert ($app.ExitCode -eq 0 -and (Get-Content -LiteralPath (Join-Path $probe 'result.txt') -Raw).Contains('SELF-TEST OK')) 'Installed recorder starts and records a valid MP4 with audio'
    } finally { $app.Dispose() }

    $upgrade = Invoke-CheckedProcess $SetupPath ($common + @('/LANG=german', "/DIR=$installed", "/GROUP=$group", '/TASKS=!desktopicon', "/LOG=$work\upgrade.log"))
    Assert ($upgrade -eq 0 -and (Get-ItemProperty -LiteralPath $registry).DisplayVersion -eq $version) 'Reinstall upgrades in place under the same uninstall identity'
    $guard = [Threading.Mutex]::new($false, 'Local\FrameForge.Recorder.Running')
    try {
        $blocked = Invoke-CheckedProcess $uninstaller @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=$work\blocked-uninstall.log")
        Assert ($blocked -ne 0 -and (Test-Path -LiteralPath $exe)) 'Uninstall is blocked while the recorder guard exists'
    } finally { $guard.Dispose() }
    $exit = Invoke-CheckedProcess $uninstaller $uninstallArgs
    Assert ($exit -eq 0 -and -not (Test-Path -LiteralPath $exe)) 'Uninstall removes installed application files'
    $didInstall = $false
    Assert (-not (Test-Path -LiteralPath $registry) -and -not (Test-Path -LiteralPath $shortcut)) 'Uninstall removes its registry entry and Start menu shortcut'
    Assert ((Test-Path -LiteralPath (Join-Path $installed 'user-note.txt')) -and (Test-Path -LiteralPath (Join-Path $probe 'selftest.mp4'))) 'Uninstall preserves user-created files and recorded videos'
    $settingsAfter = if (Test-Path -LiteralPath $settings) { (Get-FileHash -LiteralPath $settings).Hash } else { '' }
    Assert ($settingsBefore -eq $settingsAfter) 'Existing user settings remain unchanged'
    $results.Add("PASS: $($results.Count) installer lifecycle checks")
} finally {
    if ($didInstall -and (Test-Path -LiteralPath $uninstaller)) {
        $cleanup = Invoke-CheckedProcess $uninstaller $uninstallArgs
        if ($cleanup -ne 0) { Write-Warning "Test installation remains in $installed" }
    }
    [IO.File]::WriteAllLines((Join-Path $work 'result.txt'), $results)
    Write-Host "Verification artifacts: $work"
}
