FrameForge regression checks

Run from the repository root:
dotnet run --project tests\FrameForge.RegressionTests -c Release -- artifacts\verification\regression --capture --audio

Requires Windows, .NET 10 SDK, an interactive desktop and a connected monitor.
The --capture checks additionally require a working hardware H.264 encoder.
Without --capture, the shutdown check still makes a short recording with automatic encoder selection.

Settings and logs are isolated in the test executable's portable data folder.
Videos and the final result are written to the supplied output folder.
The checks do not change the installed application's settings.

Marker and meter checks cover independent peak tracking, silence, pause/reset,
round meter animation and cleanup, source toggle states, marker option persistence,
stroke separation, bounded drawing data, keyboard hook cleanup, and screen rendering.
Repeated and rapid Alt activation checks verify that old marker surfaces are destroyed,
queued release/press events each take effect, and each activation starts without old ink.
The --capture checks also decode yellow marker strokes and their removal from
hardware-encoded region and software-encoded window recordings.

Region and window-picker checks cover movement before and after confirmation,
saved region coordinates, live H.264 crop movement with both encoders,
window search, DWM thumbnail clipping during scrolling and native handle cleanup.

Additional checks cover the confirmed-region Edit button (apply, cancel and recording lock),
cursor acceleration and braking, arc/spiral paths, speed profiles, frame-rate consistency,
precise dragging, cursor profile persistence and the settings layout on short screens.

The --audio checks require an active output device and microphone. They briefly play
a quiet 997 Hz test tone and record each selected source locally, verify the AAC
track by decoding it, check mixing and pause synchronization, and test source errors.
