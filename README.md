# FrameForge Recorder

[English](README.md) · [Русский](README.ru.md)

A Windows screen recorder built with .NET 10, WPF, Windows Graphics Capture and Media Foundation.
Created by **Victor Kirsanov**. Licensed under [MIT](LICENSE).

## Download and install

Download `FrameForge-Setup-<version>-x64.exe` from [Releases](https://github.com/viktorkirs/FrameForge/releases).
The installer includes the .NET runtime, installs for the current user, adds a Start menu shortcut,
and offers an optional desktop shortcut. Uninstalling preserves your settings and recordings.

[Code signing policy](CODE_SIGNING_POLICY.md) · [Privacy policy](PRIVACY.md).
SignPath Foundation signing is being prepared; currently published installers are unsigned.

The installer requires x64-compatible Windows 10 build 19041 or later, or Windows 11.
Screen capture and hardware encoding depend on the available graphics drivers and hardware.

## Features

- Record a monitor, an application window, or a selected screen region to H.264 MP4.
- Capture system audio and microphone audio independently, with device selection and circular level meters.
- Pause and resume, take PNG screenshots, and configure global keyboard shortcuts.
- Move a selected region and edit its size before recording starts.
- Find windows using live thumbnails and search.
- Smooth the recorded cursor with acceleration, braking, arc and spiral paths.
- Draw temporary yellow highlights: enable the marker in **Settings → Cursor**, hold **Alt + left mouse button**,
  and release Alt to clear the drawing.
- Russian, English and German interface.

Choose the interface language on first launch. To change it later, open **Settings → General → Language**.

When a captured window changes size, the full image is fitted into the recording's fixed canvas.
Black bars preserve its aspect ratio; the MP4 resolution remains constant.

## Build

Requirements: Windows and the .NET 10 SDK.

```powershell
dotnet build FrameForge.slnx -c Release
dotnet run --project src/FrameForge.UI -c Release
```

Build the self-contained installer with PowerShell 7+:

```powershell
pwsh -File installer/Build-Installer.ps1
```

The installer and its SHA256 checksum are written to `dist/`.
[Installer documentation](installer/README.txt) includes optional Authenticode signing.
Signing requires your own valid code-signing certificate; ordinary builds are unsigned.
The separate [SignPath workflow](.github/workflows/signpath.yml) supports the free open-source
signing program after approval. See [setup instructions](signing/README.md).

## Verification

CI builds the Windows solution, runs tests that do not need a desktop, and uploads an installer artifact:

```powershell
dotnet run --project tests/FrameForge.RegressionTests -c Release -- artifacts/verification --headless
```

On an interactive Windows desktop with a hardware H.264 encoder, output device and microphone:

```powershell
dotnet run --project tests/FrameForge.RegressionTests -c Release -- artifacts/verification --capture --audio
pwsh -File installer/Test-Installer.ps1
```

Real-time endurance recording (default: one hour, test window and system audio, microphone off):

```powershell
dotnet run --project tests/FrameForge.RegressionTests -c Release -- artifacts/endurance --long-recording --seconds 3600
```

The audio checks briefly play a quiet test tone. Endurance checks record only the dedicated test window,
exercise pauses and resizing, log memory, and decode the final video/audio samples. They still capture
the selected system audio output. Settings used by regression tests are isolated from your application settings.
Local recordings, test artifacts, build output and signing keys are excluded from Git.

Recorded results for 1.2.2: [full validation report](docs/VALIDATION-1.2.2.md).

## Current limits and next steps

PNG/JPEG image sequences and interval capture are not implemented yet. A PNG screenshot is a separate feature.
MP4 is finalized on a normal stop, including handled errors and low-disk shutdown; process termination or
power loss before finalization can still leave an incomplete file. Segmented or fragmented recording and
crash-recovery tests are planned work. See the [technical review](docs/REVIEW-1.2.2.md).

## License and acknowledgements

FrameForge code is MIT licensed, copyright © 2026 Victor Kirsanov.
Third-party components retain their own licenses; see [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).
