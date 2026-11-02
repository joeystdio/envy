# Envy

Secrets and environment variables for your terminal and the coding agents you run in it.

Envy is a native macOS app. You keep **Global** variables plus per-**project** sets, switch any project or
single variable on or off, and every terminal (and Claude Code, Codex, Cursor or Aider started from it) gets
exactly what's switched on. Secrets live in your Keychain, not in dotfiles.

## Download

**[Download Envy.dmg](https://github.com/joeystdio/envy/releases/latest/download/Envy.dmg)** (macOS 15 or later),
open it and drag Envy into Applications. On first launch the Setup Assistant connects your terminal and imports
your projects. Older versions are on the [Releases](https://github.com/joeystdio/envy/releases) page.

## How it works

- **Global** loads in every terminal.
- A **project without a folder** loads in every terminal too, on top of Global.
- A **project with linked folders** loads only while the terminal is inside one of those folders (or a
  subfolder), direnv-style, and unloads when you leave. If a variable overrode something, the old value comes back.
- Precedence, later wins: Global, then always-on projects (sidebar order), then folder projects (deepest folder last).
- Changes reach open terminals at their next prompt. New tabs, tmux panes and editor terminals also pick up
  what their parent applied and clean it up correctly.

Envy adds one marked line to `~/.zshrc` (and/or `~/.bashrc`, `~/.bash_profile`) from **Shell Integration**.
That line sources `~/.config/envy/init.sh`; the app regenerates `~/.config/envy/vars.sh` (mode 600) whenever
you change something. Disconnecting removes the line again; a backup of the previous file is kept as
`<file>.envy-backup`.

In a terminal:

```
envy status   # which projects and variables are loaded here
envy reload   # reload right now
envy open     # open the app
```

## Setup Assistant

On first launch (and any time from Envy › Setup Assistant…) Envy:

1. **Detects your setup**: login shell, where zsh reads its startup files (`ZDOTDIR` set in `~/.zshenv`, dotfile
   symlinks), and which coding agents are on your PATH (Claude Code, Codex, Cursor CLI, Gemini CLI, Aider, …).
2. **Connects the right startup file**, then opens a fresh login shell in the background to verify it loads Envy.
3. **Finds plain-text secrets** exported from `~/.zshrc`, `~/.zprofile`, `~/.zshenv`, `~/.bashrc`, … and imports
   the ones you pick into Global (secrets go to the Keychain), telling you which lines to delete afterwards.
4. **Imports your projects**: pick a folder (one-click for `~/Developer`, `~/code`, …) and every folder with a
   `.env`, `.env.local`, `.envrc`, or .NET `appsettings.json` / user secrets becomes a folder-linked project.

## .NET

.NET reads configuration overrides from environment variables with `__` between sections, so Envy flattens
`appsettings.json` and `appsettings.Development.json` that way (`ConnectionStrings:Default` becomes
`ConnectionStrings__Default`; comments and trailing commas are fine; `Logging` and `AllowedHosts` are skipped).
`dotnet user-secrets` (found through the `UserSecretsId` in the `.csproj`) are imported too, always as secrets, and
win over the files. Copies under `bin/` and `obj/` are ignored. You can also paste or open an `appsettings.json` in
the Import sheet.

## Using it

- **Add** variables one by one (⌘N), **paste or drop** a `.env` or `appsettings.json` file, or **copy from another project**.
- **Import projects** from a folder of code any time (⇧⌘I, or the button at the bottom of the sidebar).
- Select rows and use the context menu to copy or move them to another project, copy them as `.env`, toggle
  secret, or turn them off. ⌘C / ⌘V on the table copy and paste `.env` text. ⌘Z undoes.
- **Secret** (lock icon): the value is stored in the Keychain and masked in the app.
- **Expand** (`$` icon): the value is evaluated like a double-quoted shell string, so `$HOME/bin:$PATH`,
  `${OTHER}`, `~` and `$(command)` work. Off by default, so values are passed through literally.
- **Export .env** writes a project's enabled variables to a file (mode 600).
- The **menu bar** icon has quick on/off switches for every project.

## Build

Requires Xcode 16+ (Swift 6 toolchain); the app runs on macOS 15 or later.

```bash
swift test                      # core tests, including real zsh and bash runs of the hook
scripts/build-app.sh            # builds dist/Envy.app
scripts/build-app.sh --install  # also copies it to /Applications
```

The build signs with your Developer ID Application certificate when you have one. The Keychain then keeps
trusting Envy across rebuilds: after you click Always Allow once, updates don't ask again. Without a certificate
the signature is ad-hoc, and macOS asks for Keychain access after every rebuild. To pick another identity, or
ad-hoc with `-`:

```bash
CODESIGN_IDENTITY="Apple Development: Your Name (TEAMID)" scripts/build-app.sh
```

## Sharing with colleagues

`scripts/release.sh` builds `dist/Envy.dmg`: signed with your Developer ID, notarized by Apple and stapled, so it
opens without Gatekeeper warnings on any Mac. Number each release with `VERSION`, then publish it as a GitHub
release; the Download link above always serves the newest one.

It uses the notarization credentials saved in your Keychain as `notarytool-profile` (set `NOTARY_PROFILE` for
another name). To save them on a new Mac, with an app-specific password made at
[account.apple.com](https://account.apple.com) under Sign-In and Security:

```bash
xcrun notarytool store-credentials notarytool-profile --apple-id you@example.com --team-id R78A8G6R2N
```

Then for each release:

```bash
VERSION=1.0.1 scripts/release.sh
gh release create v1.0.1 dist/Envy.dmg --title "Envy 1.0.1" --generate-notes
```

To try it without touching your real setup, point it at a sandbox folder. Settings, secrets (as a file instead
of the Keychain), shell files and startup files then all live there:

```bash
open -n --env ENVY_HOME=/tmp/envy-sandbox dist/Envy.app
```

## Layout

- `Sources/EnvyCore`: model, `.env` parsing, shell hook and script generation, startup-file editing.
- `Sources/Envy`: the SwiftUI app (window, menu bar extra, Keychain, persistence).
- `Tests/EnvyCoreTests`: unit tests plus integration tests that run the generated hook in `/bin/zsh` and `/bin/bash`.
