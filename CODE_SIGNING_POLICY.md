# Code signing policy

Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by [SignPath Foundation](https://signpath.org).

## What is signed

The Windows release of Envy: `Envy-Setup.exe`, and inside it Envy's own executables (`Envy.exe`, `Envy.dll`, `Envy.Core.dll` and the `envy.exe` command). The .NET and Windows App SDK files that ship with it are Microsoft's and carry Microsoft's own signatures.

Signing starts with the first release after SignPath Foundation accepted the project. Earlier Windows releases are unsigned.

The macOS app is signed with an Apple Developer ID and notarized by Apple, separately from this policy.

## How releases are built and signed

- Every Windows release is built from the source in this repository, on GitHub-hosted runners, by [`.github/workflows/windows.yml`](.github/workflows/windows.yml), from the release's tag.
- The workflow submits Envy's executables, and then the installer, to SignPath. Each signing request is approved by hand by an approver below before anything is signed.
- Only files built from this repository are signed.

## Team roles

- **Committers and reviewers:** [joeystdio](https://github.com/joeystdio). Changes from anyone else come in as pull requests and are reviewed by a committer before they are merged, build scripts and CI configuration included.
- **Approvers:** [joeystdio](https://github.com/joeystdio).

## Privacy policy

This program will not transfer any information to other networked systems unless specifically requested by the user or the person installing or operating it.

Envy keeps everything on your computer: settings in your user profile, and secret values in the Windows Credential Manager (or the macOS Keychain). It has no telemetry and doesn't check for updates by itself.

## System changes and uninstalling

Envy tells you before it changes anything outside its own folders: its Setup Assistant shows each startup file (PowerShell profiles, `~/.bashrc`) and the Command Prompt AutoRun value it will change, keeps a backup of each file, and asks before adding instructions for coding agents. Uninstalling Envy from Settings → Apps takes all of those changes out again.
