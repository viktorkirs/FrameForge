# Validation — FrameForge 1.2.2

Date: 2026-11-10. Windows x64, NVIDIA GeForce GTX 1050 Ti.

## Automated checks

- Release solution build: no warnings or errors.
- Full local regression suite: **53 passed**, including hardware/software capture, actual audio devices, resizing,
  marker lifetime, settings, allocation checks and encoder-surface leases across 40 input-size changes.
- Installer lifecycle: **14 passed** (install, update, recording, running-app guards, uninstall and preservation of user files).
- Windows GitHub Actions: solution build, six desktop-independent checks, installer build and artifact upload passed.
- Repeated scaled 1080p readback after warm-up: **0 managed bytes allocated over 60 frames** in the readback operation.
  This excludes independently owned previews/screenshots and the MF-owned encoding sample copy.
- Accelerated two-hour audio timeline: 7,200 pauses, two sources, exact expected samples and ring-buffer wrap passed.

## Real-time recordings

All recordings used a dedicated changing test window and system loopback audio; the microphone was off.
These are actual elapsed-time runs, separate from the accelerated timeline check.

| Scenario | Wall target | Video duration after pauses | A/V duration difference | Submitted / container frames | Skipped intervals / filled frames |
| --- | ---: | ---: | ---: | ---: | ---: |
| Hardware endurance | 60 min | 3586.7667 s | 6.67 ms | 107595 / 107603 | 8 / 8 |
| Software endurance | 12 min | 717.6667 s | 8.00 ms | 21530 / 21530 | 0 / 0 |
| Hardware resize/pause stress | 3 min | 137.5333 s | 2.67 ms | 4110 / 4126 | 16 / 16 |

The one-hour run exercised 11 pauses and five resizes. The software run exercised two pauses and one resize.
The three-minute stress recording logged 17 capture-size changes. It intentionally paused every five seconds and resized every ten.
No recorder error was reported in these runs. Encoded timestamps were scanned, and head/middle/tail video frames and tail audio
were decoded. The quiet 997 Hz end marker was present in the decoded audio. The one-hour MP4 was 140,130,390 bytes.

Maximum **sampled** private process memory was about 307 MiB in the one-hour run and 251 MiB in software mode.
Memory grew during initialization and resize-related allocations; this measurement alone is not proof that no leak can exist.
Managed memory and private-byte snapshots were retained locally for inspection.

## Test corrections and scope

The original endurance checker incorrectly required container-frame count to equal submitted-frame count. The Media Foundation
path filled skipped timestamp intervals with repeated frames: in the hour file, 107595 submitted + 8 skipped = 107603 encoded.
That assertion produced a false failure even though the A/V difference was only 6.67 ms. The completed files were independently
re-probed and their counts reconciled. The permanent checker now reports submitted, skipped, filled and container counts separately,
and preserves raw recorder results even when an assertion fails.
The corrected checker itself passed a fresh 30-second hardware run with five pauses and two resizes.

The hour run began before the final GPU output-pool reuse refinement. Final capture code was additionally checked by the full
regression suite, the twelve-minute software run, and the three-minute hardware stress run. This distinction matters: it is not
a claim that every final binary was separately recorded for an hour.

The installer is unsigned; the optional Authenticode path still requires the publisher's real certificate. Missing signing input
was verified to fail explicitly. Abrupt process termination/power-loss recovery and PNG/JPEG interval sequences remain unimplemented;
see [the review](REVIEW-1.2.2.md). Recordings and raw device logs were kept out of Git.
