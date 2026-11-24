<p align="center">
  <img src="assets/icon.png" width="128" height="128" alt="Envy Icon">
</p>

<h1 align="center">Envy</h1>

<p align="center">
  <strong>Environment variables & secrets for your terminal and AI coding agents — native on macOS and Windows, 100% free & open-source.</strong>
</p>

<p align="center">
  <a href="https://github.com/joeystdio/envy/releases/latest/download/Envy.dmg">
    <img src="https://img.shields.io/badge/Download-Envy.dmg-10B981?style=for-the-badge&logo=apple" alt="Download Envy.dmg">
  </a>
  <a href="https://github.com/joeystdio/envy/releases/latest/download/Envy-Setup.exe">
    <img src="https://img.shields.io/badge/Download-Envy--Setup.exe-0078D4?style=for-the-badge&logo=windows" alt="Download Envy-Setup.exe">
  </a>
  <a href="https://github.com/joeystdio/envy/releases/latest">
    <img src="https://img.shields.io/github/v/release/joeystdio/envy?style=for-the-badge&color=blue" alt="Latest Release">
  </a>
  <img src="https://img.shields.io/badge/macOS-15.0%2B%20(Universal)-success?style=for-the-badge&logo=apple" alt="macOS 15+ Universal">
  <img src="https://img.shields.io/badge/Apple-Notarized-black?style=for-the-badge&logo=apple" alt="Apple Notarized">
  <img src="https://img.shields.io/badge/Windows-10%20%26%2011%20(x64)-0078D4?style=for-the-badge&logo=windows" alt="Windows 10 and 11">
  <a href="https://buymeacoffee.com/joeystdio">
    <img src="https://img.shields.io/badge/☕_Buy_Me_a_Coffee-joeystdio-FFDD00?style=for-the-badge&logo=buymeacoffee&logoColor=black" alt="Buy Me a Coffee">
  </a>
</p>

<p align="center">
  <img src="assets/screenshot.png" width="760" alt="Envy Screenshot">
</p>

---

## ✨ Features

- **🔐 Secrets in your Keychain, not your dotfiles**: API keys and tokens live in the macOS Keychain and are masked in the app. No more `export OPENAI_API_KEY=…` in plain text in `~/.zshrc`, where every program you run (coding agents included) can read it.
- **🌍 Global + per-project variables**: Keep **Global** variables that load in every terminal, plus a set per project that adds to or overrides them.
- **📂 Folder-aware, direnv-style**: Link a project to a folder and its variables load the moment a terminal `cd`s in, and unload when it leaves. Anything they overrode gets its old value back.
- **🎚️ One-click on/off**: Switch a whole project or a single variable on or off, from the window or the **menu bar**. Open terminals pick it up at their next prompt, no restart needed.
- **🤖 Built for coding agents**: Claude Code, Codex, Cursor CLI, Gemini CLI, Aider and friends get exactly what's switched on for the folder you start them in, and nothing else.
- **📥 Import everything**:
  - Scan a whole folder of code at once for `.env`, `.env.local` and `.envrc` files.
  - **.NET**: `appsettings.json`, `appsettings.Development.json` and `dotnet user-secrets`, flattened the way .NET reads them (`ConnectionStrings:Default` → `ConnectionStrings__Default`).
  - Paste or drop `.env` text, copy variables between projects, export back to `.env`.
- **🧭 Setup Assistant**: Detects your shell (zsh or bash, `ZDOTDIR`, dotfile symlinks) and the coding agents you have installed. It then connects your terminal, checks that a fresh shell loads Envy, and moves plain-text secrets out of your startup files into the Keychain.

---

## 📦 Installation

### Option 1: Homebrew (Recommended)
```bash
brew tap joeystdio/tap
brew install --cask envy
```

### Option 2: Direct Download (`.dmg`)
1. Download [**`Envy.dmg`**](https://github.com/joeystdio/envy/releases/latest/download/Envy.dmg) from the [latest release](https://github.com/joeystdio/envy/releases/latest).
2. Open `Envy.dmg` and drag **Envy.app** into your **Applications** folder.
3. Launch **Envy**. The Setup Assistant connects your terminal and imports your projects.

> Envy is signed with a Developer ID and **notarized by Apple**, so it opens without any Gatekeeper warnings.

### 🪟 Windows: Direct Download (`Envy-Setup.exe`)
1. Download [**`Envy-Setup.exe`**](https://github.com/joeystdio/envy/releases/latest/download/Envy-Setup.exe) from the [latest release](https://github.com/joeystdio/envy/releases/latest).
2. Run it. It installs for you only (in `%LOCALAPPDATA%\Programs\Envy`) and needs no admin rights.
3. Envy opens and its Setup Assistant connects your shells and imports your projects.

> The Windows installer isn't code-signed yet, so Microsoft Defender SmartScreen warns about an unknown publisher the first time. Click **More info**, then **Run anyway**. If your browser blocks the download, choose **Keep** in its download list.


---

## 🚀 Using Envy

Envy adds one marked line to `~/.zshrc` (and/or `~/.bashrc`, `~/.bash_profile`). From then on, every terminal loads whatever you switch on:

| Scope | Loads |
| --- | --- |
| 🌍 **Global** | In every terminal |
| 📦 **Project without a folder** | In every terminal, on top of Global |
| 📂 **Project with linked folders** | Only inside those folders (and subfolders) |

Later wins: Global, then always-on projects (sidebar order), then folder projects (deepest folder last).

In a terminal:

```bash
envy status   # which projects and variables are loaded here
envy reload   # reload right now
envy open     # open the app
```

- **🔒 Secret**: stored in the Keychain and masked in the app.
- **💲 Expand**: the value is evaluated like a double-quoted shell string, so `$HOME/bin:$PATH`, `${OTHER}`, `~` and `$(command)` work. Off by default, so values pass through literally.
- **Shortcuts**: ⌘N new variable, ⇧⌘N new project, ⌘I import `.env`, ⇧⌘I import projects from a folder, ⇧⌘E export `.env`, ⌘Z undo.

<details>
<summary><strong>How the shell hook works</strong></summary>

The line in your startup file sources `~/.config/envy/init.sh`. The app regenerates `~/.config/envy/vars.sh` (mode 600) whenever you change something. At every prompt, and on `cd` in zsh, the hook checks a generation stamp and the current folder. When either changed, it puts back everything it set earlier and applies the current set. New tabs, tmux panes and editor terminals inherit what their parent applied and clean it up correctly.

Disconnecting from **Shell Integration** removes the line again; a copy of the previous file is kept as `<file>.envy-backup`.
</details>


## 🪟 Envy on Windows

The Windows app is native too (WinUI 3, with Mica and a tray icon instead of the menu bar), and it does the same job:

| Shell | How it loads Envy | Per folder |
| --- | --- | --- |
| **PowerShell 7** and **Windows PowerShell 5.1** | A marked line in your profile (`Documents\PowerShell\profile.ps1`, `Documents\WindowsPowerShell\profile.ps1`) wraps the prompt | ✅ at every prompt, and right on `cd` in PowerShell 7 |
| **Git Bash** | The same marked line as on the Mac, in `~/.bashrc` | ✅ at every prompt |
| **Command Prompt** | Envy is chained onto cmd's `AutoRun` command (`HKCU\Software\Microsoft\Command Processor`), after anything already there | ⚠️ once, when the window opens, for the folder it opens in. Run `envy reload` after `cd` |

- **🔐 Secrets in Credential Manager**: secret values are stored as generic credentials (`Envy/…`), encrypted by Windows for your account. Settings live in `%APPDATA%\Envy`.
- **🧭 Setup Assistant**: finds PowerShell 7, Windows PowerShell, Git Bash and Command Prompt and the coding agents on their PATH, connects the shells and checks that a fresh one of each loads Envy. It also offers to move plain-text secrets out of your PowerShell profiles, `~/.bashrc` and your user environment variables into Credential Manager. Every file it changes keeps a copy as `<file>.envy-backup`.
- **🤖 The `envy` command for coding agents**: connected shells put `envy.exe` on `PATH`, so agents (and scripts) can run `envy status`, `envy set`, `envy import .env` and `envy exec -- <command>`, and ask you for a secret with `envy add NAME --secret`, which opens Envy for you to paste it: the value never passes through the agent. Envy can also teach Claude Code (a rule in `~\.claude\rules`) and Codex, Gemini CLI, Copilot CLI and others (a skill in `~\.agents\skills`) to work this way. The command talks to the app over a named pipe only your account can open.
- **💲 Expand on Windows** fills in `%NAME%`, `$NAME`, `${NAME}`, `$env:NAME` and a leading `~` (your user folder) when a shell loads the value, so `%USERPROFILE%\bin;%PATH%` works in all three shells. Commands such as `$(…)` are not run. Git Bash gets `PATH` converted to its `/c/…` form and back.
- `envy status`, `envy reload` and `envy open` work in all three shells.
- Windows PowerShell only runs profile scripts when your execution policy allows it. If it doesn't, Shell Integration offers to set it to `RemoteSigned` for your account.
- Closing the window keeps Envy in the notification area; right-click its icon to switch Global or a project on or off. Quit from there too.

<details>
<summary><strong>How the Windows hooks work</strong></summary>

Envy writes the hooks and the generated variables for each shell to `%LOCALAPPDATA%\Envy\shell` (`init.ps1`/`vars.ps1`, `init.sh`/`vars.sh`, `init.cmd`/`vars.cmd`). That folder holds your secret values, so only your account can read it, and it never roams to a server. A state stamp tells open shells when to reload. The bookkeeping (`__ENVY_VARS`, `__ENVY_O_<NAME>`) is shared by all three shells, so a Git Bash started from PowerShell, or a Command Prompt started from Git Bash, puts back what its parent set before applying the current set. Disconnecting removes the lines again and keeps `<file>.envy-backup`; uninstalling Envy disconnects every shell.
</details>

---

## 🛠️ Building from Source

### macOS

Requires macOS 15.0+ and Xcode 16+ (Swift 6 toolchain):

```bash
git clone https://github.com/joeystdio/envy.git
cd envy
swift test                      # core tests, including real zsh and bash runs of the hook
scripts/build-app.sh --install  # builds dist/Envy.app and copies it to /Applications
```

This compiles a Universal Binary (`arm64` Apple Silicon + `x86_64` Intel) and bundles `Envy.app`. It signs with your **Developer ID Application** certificate when you have one, so the Keychain keeps trusting Envy across rebuilds; otherwise it signs ad-hoc. Set `CODESIGN_IDENTITY` to choose another identity, or `-` for ad-hoc.

To try it without touching your real setup, point it at a sandbox folder. Settings, secrets (as a file instead of the Keychain), shell files and startup files then all live there:

```bash
open -n --env ENVY_HOME=/tmp/envy-sandbox dist/Envy.app
```


### Windows

The Windows app lives in [`windows/`](windows). Its core (`Envy.Core`, plain .NET 10 with xUnit tests) builds and tests on any OS; the WinUI 3 app needs Windows to run. With the .NET 10 SDK:

```powershell
cd windows
dotnet test --project tests/Envy.Core.Tests      # includes real PowerShell, Git Bash and cmd runs of the hooks
dotnet publish src/Envy.App -c Release -r win-x64 -p:Platform=x64 -o artifacts/publish
& "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" installer\Envy.iss   # artifacts\Envy-Setup.exe
```

To try it without touching your real setup, start it with `ENVY_HOME` set. Settings, secrets (as a file instead of Credential Manager), shell files, profiles, `.bashrc` and the AutoRun value then all live in that folder:

```powershell
$env:ENVY_HOME = "$env:TEMP\envy-sandbox"; .\artifacts\publish\Envy.exe
```

The [Windows workflow](.github/workflows/windows.yml) runs the tests on `windows-latest`, builds the app and the installer, and attaches `Envy-Setup.exe` to every published release. Create the release with the DMG as usual (`gh release create v1.0.2 dist/Envy.dmg …`) and the installer follows a few minutes later; both keep the same file name, so the download links always serve the newest release.

<details>
<summary><strong>Releasing a new version</strong></summary>

`scripts/release.sh` builds `dist/Envy.dmg`, signs it with your Developer ID, has Apple notarize it and staples the ticket. It uses the notarization credentials saved in your Keychain as `notarytool-profile` (set `NOTARY_PROFILE` for another name). To save them on a new Mac, with an app-specific password from [account.apple.com](https://account.apple.com):

```bash
xcrun notarytool store-credentials notarytool-profile --apple-id you@example.com --team-id R78A8G6R2N
```

Then:

```bash
VERSION=1.0.2 scripts/release.sh
gh release create v1.0.2 dist/Envy.dmg --title "Envy 1.0.2" --generate-notes
```

The DMG keeps the same name for every version, so the download links above always serve the newest release. Update `version` and `sha256` in the [Homebrew cask](https://github.com/joeystdio/homebrew-tap/blob/main/Casks/envy.rb) too. Publishing the release also starts the Windows workflow, which attaches `Envy-Setup.exe`.
</details>

---

## ☕ Support & Buy Me a Coffee

Envy is 100% free and open-source. If it keeps your secrets out of your dotfiles and your agents well-fed, consider buying me a coffee!

<p align="left">
  <a href="https://buymeacoffee.com/joeystdio">
    <img src="https://img.shields.io/badge/☕_Buy_Me_a_Coffee-buymeacoffee.com%2Fjoeystdio-FFDD00?style=for-the-badge&logo=buymeacoffee&logoColor=black" alt="Buy Me a Coffee">
  </a>
  <a href="https://paypal.me/joeystdio">
    <img src="https://img.shields.io/badge/💳_PayPal-paypal.me%2Fjoeystdio-00457C?style=for-the-badge&logo=paypal&logoColor=white" alt="Support via PayPal">
  </a>
</p>

---

## 📄 License

MIT License — completely free and open source forever.
