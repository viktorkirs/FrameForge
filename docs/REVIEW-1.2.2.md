# Capture and distribution review — 1.2.2

## Confirmed defects fixed

1. **Window resizing.** Capture textures were fixed at startup, `ContentSize` was clamped, and the frame pool was never resized.
   The capture now retires the transitional frame, recreates its pool at the new size, and publishes a complete new texture.
   Recording consumes an owned snapshot and rebuilds size-dependent overlay and scaler resources. The output MP4 canvas remains
   fixed, with aspect-preserving letterboxing. BGRA capture retains odd source dimensions; only the video canvas is aligned to even
   dimensions. Growing, shrinking and restoring a window are checked using both encoders.
2. **Software readback.** The engine allocated a staging texture ahead of time but did not use it: its hot path called the
   allocating FrameGrabber helpers and optionally allocated another scaled array. A dedicated FrameReadback now reuses its
   staging texture and output array and combines crop, scale and letterboxing in one mapped copy. The software encoder still
   copies into an MF-owned sample, as it must retain asynchronous ownership. Preview and screenshot frames remain independently
   owned. The allocation check measured zero managed bytes over 60 repeated scaled 1080p readbacks after warm-up.
3. **Audio pause history.** Every sample scanned every prior pause. Packets now locate the initial pause using binary search and
   split at pause boundaries, with prefix totals for removed time. Per-sample timestamp rounding is preserved. Differential
   tests cover late and boundary-crossing packets; an accelerated two-hour timeline exercises 7,200 pauses and ring-buffer wrap.
4. **Video verification.** The NV12 probe assumed tightly packed rows. Decoder padding corrupted previews for sizes such as
   600 × 450. The probe now uses the 2D buffer's contiguous copy and the decoder's storage dimensions.

## Distribution completed

- Windows GitHub Actions workflow: build, desktop-independent timing checks and self-contained installer artifact.
- MIT project license with Victor Kirsanov attribution and bundled third-party notices.
- Replaced the untracked-provenance SVG icon geometry with original, code-defined FrameForge icons under the project license.
- Optional Authenticode signing of the application, installer and uninstaller. Signing is opt-in and fails if the certificate,
  private key, SDK signing tool or timestamp service is unavailable. A normal build is explicitly unsigned.
- English README with a retained Russian version and reproducible local test commands.

## Remaining product work

- **PNG/JPEG sequences and interval capture:** not implemented. PNG screenshots do not provide these modes. Implement an explicit
  export choice, interval, JPEG quality, image naming and bounded disk-write queue; avoid changing the existing video FPS meaning.
- **Crash recovery:** handled errors, ordinary stop and low disk attempt MP4 finalization. Abrupt process termination or power loss
  can leave the current MP4 incomplete. Evaluate independently finalized segments or fragmented MP4, retain a session manifest,
  and verify process-kill, disk-full and restart recovery before claiming crash-safe recording.
- **Real signing:** the build hook is ready, but a trusted code-signing certificate must be provided by the publisher.

## Validation scope

The local full suite covers capture, both encoders, audio sources, pause/resume, resizing, marker lifetime, settings and installer
behavior. CI does not pretend to exercise GPU capture or physical audio devices on a hosted runner. A separate real-time endurance
mode records a dedicated test window with pauses and resizing, logs process memory, scans encoded timestamps, and decodes the tail.
An accelerated audio timeline test is distinct from a real-time recording; results must be reported separately.
