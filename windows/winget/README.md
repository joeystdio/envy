# winget

Envy's package in the [Windows Package Manager](https://learn.microsoft.com/windows/package-manager/) is `joeystdio.Envy`:

```powershell
winget install joeystdio.Envy
```

## How a release gets there

The `winget` job in the [Windows workflow](../../.github/workflows/windows.yml) runs after a release's `Envy-Setup.exe` is
attached. It downloads [Komac](https://github.com/russellbanks/Komac) (pinned to one version and checked against its
SHA-256), which adds the new version to the existing manifests: installer URL and hash, release notes, release date. It
then opens a pull request on [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs) from the token owner's
fork. winget's bots validate it, a moderator merges it, and `winget upgrade` offers the new version a few hours later.

The job skips prereleases, and skips with a warning while the package isn't in winget yet. If a pull request for that
version already exists, Komac leaves it alone, so re-running the job is safe.

## One-time setup

1. Fork [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs) under the account that owns the token
   (`joeystdio/winget-pkgs` exists).
2. Create a classic personal access token on that account with only the `public_repo` scope. Fine-grained tokens can't
   open pull requests on another owner's repository.
3. Add it to this repository as the Actions secret `WINGET_TOKEN`.

Until the secret exists, the job only prints a notice. When the token expires, the job fails with a message about
`WINGET_TOKEN`; replace the secret and re-run the job.

If Komac ever reports that the token can't update workflow files, press **Sync fork** on the fork's page and re-run the
job.

## By hand

For a release the job didn't submit, from any Windows PC with Komac (`winget install RussellBanks.Komac`):

```powershell
komac update joeystdio.Envy --version 1.2.1 --urls https://github.com/joeystdio/envy/releases/download/v1.2.1/Envy-Setup.exe --submit
```

To move to a newer Komac, change `KOMAC_VERSION` and `KOMAC_SHA256` in the workflow. The release page lists each file's
SHA-256.
