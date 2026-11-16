# FrameForge Recorder

[English](README.md) · [Русский](README.ru.md)

A Windows screen recorder built with .NET 10, WPF, Windows Graphics Capture and Media Foundation.
Created by **Victor Kirsanov**. Licensed under [MIT](LICENSE).

## Download and install

[Download the latest installer](https://github.com/viktorkirs/FrameForge/releases/download/latest/FrameForge-Setup-x64.exe).
This permanent link updates after the build and checks pass for each push to `main`.
The rolling build's release notes identify the application version and source commit.
Versioned releases are also available in [Releases](https://github.com/viktorkirs/FrameForge/releases).
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
- Edit recordings with a preview, waveforms, independent clip placement and linked video/audio trimming.
- Move a selected region and edit its size before recording starts.
- Find windows using live thumbnails and search.
- Smooth the recorded cursor with acceleration, braking, arc and spiral paths.
- Draw temporary yellow highlights: enable the marker in **Settings → Cursor**, hold **Alt + left mouse button**,
  and release Alt to clear the drawing.
- Russian, English and German interface.

Choose the interface language on first launch. To change it later, open **Settings → General → Language**.

The program checks GitHub for a newer installer on startup and offers a download when one is available.
Use **About → Check for updates** to check manually, or disable startup checks in **Settings → General**.
Downloads open in your browser; installation remains manual.

Use the equally prominent **Recording** and **Editor** buttons to switch workspaces. Returning to
recording pauses and hides the editor; returning to editing restores the same unfinished project.
Click the timeline to seek, use **Split** (`Ctrl+B`), drag blocks to move them independently and drag
their edges to trim. `Delete` removes the selected block, `Ctrl+Z` / `Ctrl+Y` undo and redo, and Space
plays or pauses. The source in/out fields accept precise times. **Export MP4** saves a separate file
and preserves the original. A video's audio stays linked to it; other clips keep their positions.
Trimming either edge moves only that edge. Empty timeline intervals remain black and silent in the export.

Use the wheel to scroll tracks and **Ctrl+wheel** to scroll time horizontally. **Track height** makes
lanes compact, while the separate zoom slider changes the time scale. Drag the splitters to allocate
space to the preview/timeline or resize the tools panel. The arrow buttons scroll horizontally and
**Fit** fits the whole project into view.

Drag across the ruler (or Shift+drag) to select one time range across every lane. **Cut range** (`Ctrl+X`)
copies and removes that zone without shifting later clips; Delete removes it without copying.
`Ctrl+C` / `Ctrl+V` copy and paste clips or ranges at the playhead. **Copy track / Paste track**
(`Ctrl+Shift+C` / `Ctrl+Shift+V`) create an independent copy with the same timing. Pasting into an
occupied interval creates another lane rather than displacing existing clips.

Use **Add video** to append another source, or create a **Video track** first to overlay it at the playhead.
**Add image** creates an independent image layer; PNG alpha remains transparent in preview and export.
**Add text** creates an editable text layer. The inspector provides installed fonts, size, color and alpha,
bold, italic, underline, alignment and multiline text. Drag the preview handles to move or resize either
layer, and drag the timeline edges or set **Layer duration** to change how long it stays on screen.
You can start with images or text without opening a video first.

Drag files from Explorer into the editor to import images, video or audio as independent tracks.
Drop on the timeline to choose their start time, or on the preview to use the playhead. Multiple files
receive separate layers at the same start time. Drop one `.ffproj` file to open a project.

Extra tracks keep their own timing: drag their blocks to move them, drag their edges to trim, and use
the track's speaker button to mute it. **Add audio** imports music or an existing voice recording.
**Record voiceover** starts playback and records only the selected microphone from the playhead;
stopping playback adds a new audio track. WAV files are kept in the output folder's `Voiceovers` directory.

Edit the picture directly in the preview. Drag its body to move it or its handles to resize it.
**Crop frame** shows the full source with eight crop handles; drag a handle or draw a new rectangle,
then choose **Done**. Hold Shift to move the crop rectangle. Rotation buttons turn the picture by 90°.
The inspector also has volume, fade-in/fade-out sliders and optional precise settings.
Choose the output canvas under **Video format**: original, landscape 16:9, portrait 9:16 or square 1:1.
The preview and exported MP4 use the same format; scale and reposition individual clips to compose it.

**Save project** (`Ctrl+S`) writes an editable `.ffproj`; `Ctrl+Shift+S` saves a copy and `Ctrl+O` opens one.
Projects retain clips, layers, transforms, fades, volumes, canvas format, playhead and timeline zoom.
Version-three projects retain editable text and image layers. Version-one and version-two projects migrate on load.
Text and its formatting are stored inside the project. Projects reference the original video, audio and
image files rather than embedding them. Keep those files with the
project when moving it; relative paths let a project folder move together. If media is missing,
FrameForge asks for its new location before replacing the current editing session.

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

Editor checks use synthetic video/audio and do not record the desktop or microphone:

```powershell
dotnet run --project tests/FrameForge.RegressionTests -c Release -- artifacts/verification/editor --editor
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
