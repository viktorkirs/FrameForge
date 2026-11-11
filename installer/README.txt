Building the Windows installer

Requirements: Windows, .NET 10 SDK, PowerShell 7+, network access for first build.
From the repository root:
  pwsh -File installer\Build-Installer.ps1

The script publishes a fresh self-contained win-x64 application, obtains the pinned
Tools.InnoSetup 6.7.3 NuGet package into artifacts\toolchain when needed, verifies
its SHA256 and NuGet signature, and compiles installer\FrameForge.iss.
Alternatively supply -IsccPath with a compatible Inno Setup 6.7+ compiler path.
No compiler installation or administrator access is needed.

Output: dist\FrameForge-Setup-<version>-x64.exe and its .sha256 file.
Version comes from Directory.Build.props. Keep the AppId stable across releases.
Each push to main builds an installer in GitHub Actions. After successful checks,
the publish job updates the permanent download link:
  https://github.com/viktorkirs/FrameForge/releases/download/latest/FrameForge-Setup-x64.exe
Each commit keeps its own Actions artifact for 14 days. An older concurrent build
cannot replace the download for a newer main commit. Versioned releases are unchanged.
The installer supports Russian, English and German and installs per user.
It never force-closes the recorder. Settings and recordings are outside the payload
and are not deleted by the uninstaller.

To verify installation, reinstallation, recording, guards and uninstall locally:
  pwsh -File installer\Test-Installer.ps1
This uses a temporary folder in artifacts, creates and removes a temporary Start
menu group, and refuses to run if this product is already installed. It checks the
actual installer executable and leaves existing recorder settings unchanged.

Installer engine: https://jrsoftware.org/
Compiler package: https://www.nuget.org/packages/Tools.InnoSetup/6.7.3

Optional Authenticode signing (not enabled for ordinary builds):
  pwsh -File installer\Build-Installer.ps1 -Sign -CertificateThumbprint <40-hex-thumbprint> -SignToolPath "C:\...\signtool.exe"

Use a valid code-signing certificate with its private key in CurrentUser\My and the
Windows SDK signing tool. The script uses SHA256 and an RFC3161 timestamp.
It signs the app before packaging and configures Inno Setup to sign the installer
and uninstaller. Every signature is verified. Missing signing inputs fail the build;
it never silently falls back to an unsigned result when -Sign was requested.
No certificate, private key or password belongs in the repository. The signing
path must be exercised with the publisher's real certificate before signed release.

SignPath Foundation (free open-source program, approval required):
  pwsh -File installer\Build-Installer.ps1 -Mode PrepareSignPath

This prepares the application and an external Inno Setup uninstaller for the
separate GitHub Actions workflow. The prepared files are unsigned. See
signing/README.md and CODE_SIGNING_POLICY.md for onboarding, manual approvals,
artifact configurations and verification. Ordinary installer builds are unchanged.
