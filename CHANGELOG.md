# Changelog

## 1.2.2

- Recreate the capture frame pool and dependent resources when a recorded window changes size.
  Fit the full window into the fixed video canvas, preserving its aspect ratio.
- Reuse the software readback staging texture and pixel buffer instead of allocating them per frame.
- Use block-based audio pause mapping with binary search and prefix totals, preserving sample rounding.
- Handle NV12 decoder row padding in recording verification, and support bounded tail probes for long files.
- Add resize, allocation, pause-boundary, accelerated timeline and real-time endurance checks.
- Add a Windows CI build and installer artifact, MIT licensing, dependency notices and optional Authenticode signing.
- Add English documentation with a retained Russian version and original MIT-licensed icon geometry.

## 1.2.1

- Clear old marker surfaces on Alt release and handle rapid release/press transitions independently.
- Increase the settings window size while retaining the tab appearance and small-screen scrolling.
- Add a self-contained per-user Windows installer, shortcuts and uninstall support.

## 1.2.0

- Add the temporary yellow Alt marker and independent circular audio level meters.
- Improve settings navigation.
