# FrameForge project workflow

- Whenever committing changes for this project, also build the installer. Pushes to `main`
  automatically build, check and publish it through `.github/workflows/build.yml`.
- Before reporting a push complete, verify the installer publication succeeded. Include the
  permanent download link in the reply:
  https://github.com/viktorkirs/FrameForge/releases/download/latest/FrameForge-Setup-x64.exe
- Keep generated installers, recordings and signing credentials out of source control.
