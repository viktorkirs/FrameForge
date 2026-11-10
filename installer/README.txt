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
