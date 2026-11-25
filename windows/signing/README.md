# Code signing for Windows releases

Envy-Setup.exe and Envy's own executables are signed through [SignPath Foundation](https://signpath.org), which gives
open-source projects a free certificate. The certificate is issued to SignPath Foundation, so Windows shows
"SignPath Foundation" as the publisher. Signing runs in the release workflow (`.github/workflows/windows.yml`), and
each signing request waits for manual approval in SignPath. Until the steps below are done, the workflow builds the
same files unsigned.

## What the workflow does

For a release (a published GitHub release, or the workflow run by hand with a tag) and only when the repository has
the `SIGNPATH_API_TOKEN` secret and the `SIGNPATH_ORGANIZATION_ID` variable:

1. Publishes the app, then uploads `Envy.exe`, `Envy.dll`, `Envy.Core.dll` and `cli/envy.exe` and submits them to
   SignPath with the artifact configuration `envy-app`. It waits (up to 6 hours) for the request to be approved and
   signed, checks the signatures, and puts the signed files back.
2. Builds the installer from the signed app, submits `Envy-Setup.exe` with the artifact configuration `envy-installer`,
   waits again, and checks the signature.
3. Attaches the signed `Envy-Setup.exe` to the release.

So every release needs two approvals in SignPath, a few minutes apart.

## Setting it up

### 1. Before applying

- Turn on two-factor authentication for the GitHub account that owns the repository (SignPath requires it for
  everyone on the team, on GitHub and on SignPath).
- Make sure `main` has [CODE_SIGNING_POLICY.md](../../CODE_SIGNING_POLICY.md) and the README and website link to it:
  SignPath requires a "Code signing policy" on the home page and download pages, with the attribution line, the team
  roles and the privacy statement.

### 2. Apply

Apply at <https://signpath.org/apply> for a free SignPath Foundation subscription for `joeystdio/envy`. Useful
answers: MIT license; repository `https://github.com/joeystdio/envy`; downloads at
`https://github.com/joeystdio/envy/releases`; code signing policy at
`https://github.com/joeystdio/envy/blob/main/CODE_SIGNING_POLICY.md`; built on GitHub-hosted runners by
`.github/workflows/windows.yml`; what's signed: the Inno Setup installer and the app's own executables. SignPath
checks the project before accepting it and isn't obliged to accept.

### 3. In SignPath, once accepted

- **Trusted build system:** link GitHub to the Envy project, and install the SignPath GitHub App on
  `joeystdio/envy` when SignPath asks (it uses it to verify that the files came from this repository's workflow).
- **Project slug:** `envy` (or set the repository variable `SIGNPATH_PROJECT_SLUG` to whatever it is).
- **Signing policy:** `release-signing` (or set `SIGNPATH_SIGNING_POLICY_SLUG`), with yourself as approver. If it
  restricts branches, allow `main` and the release tags (`v*`): a published release builds from its tag.
- **Artifact configurations:** add two, named with these slugs, and paste the files from this folder:
  - `envy-app` ← [`envy-app.xml`](envy-app.xml)
  - `envy-installer` ← [`envy-installer.xml`](envy-installer.xml)

  Both require product name `Envy` and the release's version, as SignPath Foundation's terms ask. Inno Setup pads the
  installer's version strings with spaces; if SignPath rejects `Envy-Setup.exe` over its product name or version,
  that's why, and the restriction in `envy-installer` is what to adjust (in SignPath; the workflow only names the
  slug).
- **API token:** create one for a user with the Submitter role on the project.

### 4. In GitHub: Settings → Secrets and variables → Actions

- Secret `SIGNPATH_API_TOKEN`: the API token.
- Variable `SIGNPATH_ORGANIZATION_ID`: the organization ID shown in SignPath.
- Variables `SIGNPATH_PROJECT_SLUG` and `SIGNPATH_SIGNING_POLICY_SLUG` only if they aren't `envy` and
  `release-signing`.

### 5. The first signed release

Publish a release as usual (or run the Windows workflow with the tag). Approve both signing requests in SignPath when
they appear. Afterwards, check `Envy-Setup.exe` → Properties → Digital Signatures, and update the SmartScreen note in
the README's Windows installation section.
