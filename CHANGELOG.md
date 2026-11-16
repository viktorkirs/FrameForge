# Changelog

## 1.6.0

- Add image layers, including PNG transparency, with independent timing, preview handles,
  crop, rotation, scale, fades and MP4 compositing. Support PNG, JPEG, BMP, TIFF and static GIF frames.
- Add editable text layers with installed fonts, size, color/alpha, bold, italic, underline,
  multiline text and alignment. Use the same transparent raster and placement for preview and export.
- Drag images, video and audio from Explorer into the editor. Timeline drops use the pointer's
  start time and create independent tracks; dropping a single `.ffproj` opens the project.
- Store text inside version-three projects; preserve relative image paths and continue to load
  version-one and version-two projects. Still layers support splitting, range cuts, copy/paste and undo.

## 1.5.0

- Keep absolute clip positions on the main track as well as extra tracks. Left-edge trimming moves
  the left edge; right-edge trimming moves the right edge. Moving, trimming and deleting leave other
  clips untouched. Preview and export render empty intervals as black video and silence.
- Add independent vertical and horizontal wheel scrolling (Ctrl+wheel for time), timeline fit and
  scrolling buttons, adjustable track height and live splitters for the preview, timeline and inspector.
- Select a time range across every lane and cut/delete it in one undoable operation. Copy/paste clips,
  ranges and entire tracks, preserving source settings and creating independent edit identities.
- Add prominent Recording/Editor navigation and preserve unfinished editing sessions when switching.
- Replace native message boxes with themed dialogs and translated Save / Don't save / Cancel actions.
- Save version-two `.ffproj` placements and compact track height; migrate version-one projects on load.

## 1.4.0

- Extend editing to multiple source videos and independent video/audio tracks with timed placement,
  waveforms, splitting, trimming, volume, mute and shared undo/redo.
- Crop directly in the preview with eight handles, a thirds grid and shaded margins. Drag the frame
  to reposition it, resize with its handles, rotate in 90° steps, or use optional precise settings.
- Record the selected microphone while playing the montage and add the saved WAV as a new voiceover track.
- Add per-clip alpha fade-in/fade-out and landscape, portrait and square MP4 output presets.
- Save and resume editable `.ffproj` projects with relative media paths, source relinking, canvas format,
  playhead and zoom. Register an optional per-user project file association in the installer.
- Hide the capture-region border and toolbar while editing, then restore them when the editor closes.
- Use the supplied SVG Repo icon artwork as native WPF vectors, with no additional runtime dependency.

## 1.3.0

- Add a recording editor with a seekable preview, linked video/audio lanes, source thumbnails,
  waveform, splitting, ripple deletion, edge trimming, drag reordering and undo/redo.
- Export the montage to a separate MP4 with precise cuts, progress and cancellation; preserve the
  original recording and existing destination files if rendering fails or is canceled.
- Open the latest recording from the saved-recording action or an existing MP4 from the title bar.
- Highlight timeline blocks and enlarge trim handles on hover, with movement/resize cursors.
- Decode paused previews independently of playback, including live edge previews, rapid seeks,
  frame stepping after trimming and the exclusive end of a clip.
- Check GitHub for new installers on startup and from About, show a download notice, and allow
  automatic checks to be disabled in General settings. Publish a verified update manifest with each installer.

## 1.2.5

- Play a short camera shutter sound after successfully saving and copying a screenshot,
  including screenshots taken with the global hotkey. Playback does not block the interface.

## 1.2.4

- Capture the selected region or application window when taking a screenshot outside recording.
  Use the same target for the recording-frame fallback, saved PNG and clipboard image.
- Report missing or unavailable screenshot targets instead of capturing the full monitor.

## 1.2.3

- Use the same close button icon, size and hover appearance in the main window and settings.
- Ask for Russian, English or German on first launch and remember the selection.
- Keep language selection in Settings and remove the language buttons from the main window.

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
