# Code signing policy

## Status

FrameForge is seeking approval for the SignPath Foundation open-source signing program.
Approval, an account and production signing configuration are still required. Published installers
are currently unsigned; this policy does not claim that SignPath has approved or signed them.

The intended attribution after approval is:

Free code signing provided by [SignPath.io](https://signpath.io/), certificate by
[SignPath Foundation](https://signpath.org/).

The certificate is issued in SignPath Foundation's name. The project creator and copyright holder
remain **Victor Kirsanov**; signing does not change the [MIT license](LICENSE).

## Team and approval

- Author/committer: [Victor Kirsanov (@viktorkirs)](https://github.com/viktorkirs).
- Reviewer: Victor Kirsanov. External contributions require maintainer review before merge.
- Release signing approver: Victor Kirsanov. Each production signing request requires manual
  approval in SignPath after review of its commit, build results and artifacts.

Maintainers must use multi-factor authentication for GitHub and SignPath. Production signing must
remain disabled until the maintainer has confirmed this and the Foundation has accepted the project.
The signing policy must require manual approval; possessing a CI submitter token must not grant
permission to approve signatures. This project currently has one maintainer, so these roles are
held by the same person.

## What may be signed

Only artifacts built from the public [viktorkirs/FrameForge](https://github.com/viktorkirs/FrameForge)
repository by the reviewed `main` branch's GitHub-hosted workflow may receive production signatures:

- The FrameForge executable and its five project assemblies.
- The Inno Setup uninstaller generated from the project's installer script, using the pinned compiler.
- The final FrameForge installer containing those signed files.

Third-party runtime and dependency binaries retain their original signatures and licenses. They may
be bundled with the installer; they are not re-signed as FrameForge code. See
[THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).

The [workflow](.github/workflows/signpath.yml) uploads build artifacts to GitHub Actions before
requesting signatures so SignPath can verify their origin. Artifact configurations restrict file
names, product names, versions and company metadata. Returned signatures, publisher identity and
timestamps are checked before packaging or exporting a signed release. Failure stops the signed
build; it does not publish an unsigned substitute. Signed files must not be edited afterwards.

Signed downloads must link to this policy. Ordinary development builds and preparation artifacts
must be clearly identified as unsigned. The Windows build workflow publishes its latest successful
unsigned installer as a clearly labelled rolling build. The SignPath workflow's signed output
still requires separate release review and publication.

## Privacy

FrameForge does not transmit recordings, microphone input, screenshots, settings or diagnostics to
network services. Data is saved to locations chosen by the user. Selecting a network-synchronized
folder can cause the user's separate sync software to upload it. The build/signing process shares
software artifacts and build provenance with GitHub and SignPath, not users' recordings.

See the [privacy policy](PRIVACY.md) and the [Foundation's conditions](https://signpath.org/terms).
