# SignPath Foundation setup

Status: prepared for application; **not yet approved or production-tested**. Current GitHub releases
remain unsigned. The Foundation decides whether the project has sufficient verifiable reputation.

## Application

Apply at <https://signpath.org/apply> using the public repository and releases. The maintainer must
personally agree to the [Foundation conditions](https://signpath.org/terms) and data processing terms.
Do not claim an existing sponsorship or invent download counts, endorsements or community history.

The project's [code signing policy](../CODE_SIGNING_POLICY.md) lists team roles and permitted files.
Its [privacy policy](../PRIVACY.md) describes local recording and the separate CI/signing services.

## Configure after approval

1. Enable MFA on the maintainer's GitHub and SignPath accounts. Complete the Foundation's onboarding.
2. Connect GitHub.com as a trusted build system for this project. If requested by SignPath, install
   its GitHub App with access limited to `viktorkirs/FrameForge`; review the requested permissions.
3. Create artifact configurations with the slugs `frameforge-inputs` and `frameforge-installer`
   from the XML files in this folder. Have SignPath review the generated Inno Setup uninstaller
   and the exception for its internal file version. Application and product versions must agree;
   the pinned compiler supplies the uninstaller's separate internal file version.
4. Create a `release-signing` policy using the approved Foundation certificate. Require manual
   approval by Victor Kirsanov and origin verification of the repository, `main` branch and
   `.github/workflows/signpath.yml`, with GitHub-hosted runners only. An API submitter must not
   be allowed to approve requests. Review changes to code and build scripts before signing.
5. Set GitHub Actions repository variables `SIGNPATH_ORGANIZATION_ID` and `SIGNPATH_PROJECT_SLUG`
   to the values assigned by SignPath. Store the CI submitter token in the Actions secret
   `SIGNPATH_API_TOKEN`. Never place tokens, keys or account credentials in source files or logs.

## Run and approve

Run **Actions → SignPath release preparation → Run workflow**.

- Leave **request_signing** unchecked to build and test only. This uploads seven unsigned input
  files for Foundation review and sends no request to SignPath. This mode works before onboarding.
- After onboarding, choose `main` and check **request_signing**. Production signing from forks
  or other branches is rejected before the build starts.

The workflow builds and runs headless regression checks, publishes the application and asks Inno
Setup to generate the external uninstaller. ISCC's first-pass exit code 2 and its explicit request
to sign that file are expected; other failures are rejected. Six application files and the
uninstaller are uploaded as one GitHub Actions artifact. SignPath signs that batch after manual
approval. The workflow checks trusted Foundation signatures and timestamps, inserts the signed
uninstaller into Inno Setup's cache and packages the signed application without rebuilding it.

The outer installer is uploaded for a second manually approved signing request. Using the signed
uninstaller also preserves Inno Setup's signed temporary-copy support. Final signature validation
and SHA256 calculation happen after signing. The `FrameForge-signed-windows-x64` artifact is the
only signed release output. The workflow does not create, replace or publish GitHub releases.
Approval waits are bounded to 30 minutes per batch; rejected, missing or untrusted signatures fail.

Before the first public signed release, verify installation, recorder launch, recording and
uninstallation with the real signed artifact, and link the code signing policy in its release notes.
Preparation and unsigned-rejection checks cannot validate a real signing account or certificate.
Signing does not guarantee immediate SmartScreen reputation outside the Microsoft Store.

## Local preparation

```powershell
pwsh -File installer/Build-Installer.ps1 -Mode PrepareSignPath
```

This uses a new `artifacts/signpath/` directory and refuses to reuse one from a previous build.
Use a different `-SigningDirectory` for another run. Keep all build outputs outside source control.
`Build-Installer.ps1 -Sign` remains a separate path for an owner's local certificate.

References: [GitHub integration](https://docs.signpath.io/trusted-build-systems/github),
[artifact configuration](https://docs.signpath.io/artifact-configuration/reference),
[Inno Setup external uninstall signing](https://jrsoftware.org/ishelp/topic_setup_signeduninstaller.htm).
