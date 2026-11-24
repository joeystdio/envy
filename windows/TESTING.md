# Testing Envy for Windows on a real PC

A checklist for a full manual pass of the Windows app, on a PC that has PowerShell 7, Windows PowerShell 5.1, Git for Windows (Git Bash) and Command Prompt. Every item maps to a feature of the Mac app. Take a screenshot of each numbered step and keep a pass/fail note per item.

Use dummy values only (`sk-test-…`, `ghp_test…`), never real secrets.

## 0. Before you start

Envy changes your PowerShell profiles, `~/.bashrc` (and `~/.bash_profile`), and cmd's AutoRun value. It keeps `<file>.envy-backup` copies of files it changes, and uninstalling disconnects everything again. Save the current state too, so it can be compared afterwards:

```powershell
$before = "$env:USERPROFILE\envy-test-before"; New-Item -ItemType Directory -Force $before | Out-Null
reg export "HKCU\Software\Microsoft\Command Processor" "$before\command-processor.reg" /y
reg export "HKCU\Environment" "$before\environment.reg" /y
Copy-Item $PROFILE.CurrentUserAllHosts, "$env:USERPROFILE\.bashrc", "$env:USERPROFILE\.bash_profile" $before -ErrorAction SilentlyContinue
powershell -NoProfile -Command '[Environment]::GetFolderPath("MyDocuments") + "\WindowsPowerShell\profile.ps1"' | % { Copy-Item $_ "$before\winps-profile.ps1" -ErrorAction SilentlyContinue }
```

Plant a few plain-text secrets for the Setup Assistant to find:

```powershell
Add-Content $PROFILE.CurrentUserAllHosts "`n`$env:ENVY_TEST_PS_TOKEN = 'sk-test-from-profile'"
Add-Content "$env:USERPROFILE\.bashrc" "`nexport ENVY_TEST_BASH_SECRET='bash-test-secret'"
[Environment]::SetEnvironmentVariable('ENVY_TEST_USER_API_KEY', 'user-env-test-key', 'User')
```

And a folder of projects to import:

```powershell
$code = "$env:USERPROFILE\envy-test-code"
New-Item -ItemType Directory -Force "$code\shop\sub", "$code\lab", "$code\Shop.Api", "$code\web" | Out-Null
Set-Content "$code\shop\.env" "API_URL=http://localhost`nSTRIPE_SECRET_KEY=sk_test_1"
Set-Content "$code\shop\.env.local" "API_URL=http://127.0.0.1"
Set-Content "$code\lab\.envrc" 'export DATA="$USERPROFILE/data"'
Set-Content "$code\Shop.Api\appsettings.json" '{ "Api": { "BaseUrl": "https://localhost:5001" }, "ConnectionStrings": { "Db": "Server=localhost" } }'
Set-Content "$code\Shop.Api\appsettings.Development.json" '{ "Api": { "BaseUrl": "https://localhost:7001" } }'
Set-Content "$code\Shop.Api\Shop.Api.csproj" '<Project Sdk="Microsoft.NET.Sdk.Web"><PropertyGroup><UserSecretsId>envy-test-secrets</UserSecretsId></PropertyGroup></Project>'
New-Item -ItemType Directory -Force "$env:APPDATA\Microsoft\UserSecrets\envy-test-secrets" | Out-Null
Set-Content "$env:APPDATA\Microsoft\UserSecrets\envy-test-secrets\secrets.json" '{ "Stripe:ApiKey": "sk_test_user_secret" }'
Set-Content "$code\web\.env" "PORT=3000"
```

## 1. Install

1. Build the installer (or download `Envy-Setup.exe` from the CI run): see "Build" at the end.
2. Run `Envy-Setup.exe` from Explorer. Expected: SmartScreen says "Windows protected your PC"; **More info → Run anyway** continues. No UAC prompt (per-user install into `%LOCALAPPDATA%\Programs\Envy`). "Open Envy" at the end starts the app.
3. Expected: Envy opens with Mica, and the Setup Assistant covers the window. An Envy icon is in the notification area.

## 2. Setup Assistant

1. Welcome lists PowerShell 7, Windows PowerShell 5.1, Git Bash and Command Prompt with their paths, and the coding agents found (Claude Code, Codex, …).
2. Connect: all four shells ticked. **Connect**. Expected for each: "Verified: a new … loads Envy". If Windows PowerShell says its execution policy blocks profile scripts, **ask the user before** clicking "Allow My Profile Scripts" (it sets `RemoteSigned` for the current user).
3. Check the files: each changed profile and `~/.bashrc` has the `# >>> Envy >>>` block and a `.envy-backup` next to it. `reg query "HKCU\Software\Microsoft\Command Processor" /v AutoRun` shows Envy's part *after* any value that was there before.
4. Secrets: lists `ENVY_TEST_PS_TOKEN`, `ENVY_TEST_BASH_SECRET` and `ENVY_TEST_USER_API_KEY` (secret-looking ones ticked). **Import** → "Imported 3 into Global". **Remove Them for Me** → the lines are gone from the profile and `.bashrc` (backups kept), and `ENVY_TEST_USER_API_KEY` is no longer in `[Environment]::GetEnvironmentVariable('ENVY_TEST_USER_API_KEY','User')`.
5. Projects: **Choose Folder…** → `envy-test-code`. Expected: lab, shop, Shop.Api, web. Import them.
6. Done → **Start Using Envy**.

## 3. Variables and secrets

1. Global shows the imported secrets masked (••••). The eye shows and hides a value. The lock makes a variable secret or plain; `cmdkey /list | findstr Envy` lists one `Envy/<id>` credential per secret.
2. Shop.Api: `Api__BaseUrl` is `https://localhost:7001` (Development wins) and `Stripe__ApiKey` came from user secrets (`:` became `__`), marked secret.
3. Add variables with **Add Variable** (Ctrl+N): a name like `GITHUB_TOKEN` pre-ticks Secret. "Add & New" keeps the dialog open.
4. Add to Global: `TOOLS` = `%USERPROFILE%\tools` with Expand on, and `PATH` = `%USERPROFILE%\envy-bin;%PATH%` with Expand on.
5. Turn a single variable off with its checkbox; turn a project off with its switch (sidebar, header, and the tray icon's right-click menu).
6. Ctrl+Z undoes the last step; Ctrl+Y redoes it.

## 4. Shells

In each of a **new** PowerShell 7, Windows PowerShell and Git Bash window:

1. `envy status` lists Global and the loaded variables. `$env:TOOLS` / `echo $TOOLS` is `C:\Users\<you>\tools`. The first `PATH` entry is the envy-bin folder (in Git Bash: `/c/Users/<you>/envy-bin`), and `git --version` still works.
2. `cd $env:USERPROFILE\envy-test-code\shop\sub` → `API_URL` is `http://127.0.0.1` and `envy status` shows "Global, shop". `cd ~` → `API_URL` is gone.
3. Leave the window open, toggle **shop** off in Envy, press Enter in the shell → it's unloaded at the next prompt. Toggle it on again.
4. Start Git Bash from PowerShell (`& "C:\Program Files\Git\bin\bash.exe" -l`), and cmd from Git Bash: variables are right and `PATH` works in each.
5. `envy reload` reloads; `envy open` brings the Envy window to the front.

In **Command Prompt**:

1. A new window started in `envy-test-code\shop` has `API_URL` (`echo %API_URL%`), and `envy status` works.
2. `cd %USERPROFILE%` then `envy reload` → `API_URL` is gone. Shell Integration says that cmd doesn't switch per folder by itself.
3. A value with `&`, `^`, `%` and quotes arrives intact (`set NAME`).

## 5. Import, copy and export

1. Import → Paste or Open .env File…: paste `A=1`, `B="two words"`; also try opening an `appsettings.json`.
2. Drag a `.env` file from Explorer onto a project page → the import dialog opens with its contents.
3. Right-click a variable → Copy to / Move to another project; "Copy as .env" puts `KEY=value` lines on the clipboard; Ctrl+C / Ctrl+V in the table do the same.
4. Export .env (Ctrl+Shift+E) writes the enabled variables, secrets included.
5. New Project with a folder that has a `.env` offers to import its variables.

## 6. Generated files

1. Shell Integration → Show in File Explorer opens `%LOCALAPPDATA%\Envy\shell`. `icacls "%LOCALAPPDATA%\Envy\shell"` lists only your account.
2. The preview shows the PowerShell, Git Bash and Command Prompt scripts with secrets masked.

## 7. The envy command and coding agents

In a new PowerShell window, `cd $env:USERPROFILE\envy-test-code\web` (repeat the starred items in Git Bash and Command Prompt):

1. `(Get-Command envy -CommandType Application).Source` is `%LOCALAPPDATA%\Envy\shell\bin\envy.exe`, and that folder is on `PATH` once (`$env:PATH -split ';'`). Quit Envy from the tray first: `envy list` starts it in the notification area and answers within a few seconds. ★
2. `envy set PORT=4000` → "Updated PORT in web." and the note about `envy exec`. Envy's window shows it at once, and **Ctrl+Z** in Envy undoes it as one step. `envy set API_TOKEN=x` is refused ("looks like a secret"); `envy set STRIPE_SECRET_KEY=x --project shop` is refused ("is a secret in shop"). ★
3. `envy add OPENAI_API_KEY --secret` → Envy comes to the front with "New Variable in web": the name filled in, Secret on, the cursor in Value, a note that a command in your terminal asked for it, and **Add** greyed out until a value is pasted. Paste `sk-test-agent`, **Add**. `envy list web` shows `OPENAI_API_KEY  secret` and never the value.
4. `envy import .env --project lab` → names only, "Stored as secrets in Windows Credential Manager: …" for secret-looking ones, and the file is unchanged.
5. `envy status` → "Envy loads here: Global, web (later ones win)", and since this window started before the changes, "missing …" with the `envy exec` hint. `envy exec -- pwsh -NoProfile -c '$env:PORT'` prints `4000`. ★
6. `envy project add demo --folder .` in a folder no project has; `envy link --project demo` in another folder; `envy unlink --project demo`; `envy off PORT`; `envy on --project demo`; `envy remove PORT`. Each appears in Envy right away.
7. `envy help` and `envy --version` work with Envy quit and without starting it. ★
8. Shell Integration → **Coding agents** lists Claude Code (`~\.claude\rules\envy.md`) and the other agents (`~\.agents\skills\envy\SKILL.md`). **Install** writes the file; **Remove** deletes it (and the empty `envy` skill folder) and leaves other files there alone. A file with that name that Envy didn't write is reported and left alone.
9. With nothing installed and an agent present, the banner "Teach your coding agents about Envy…" appears once: **Not Now** hides it for good (`%APPDATA%\Envy\.agents-offered`); **Add Instructions** installs them. A fresh Setup Assistant shows a "Teach your coding agents" step with a checkbox per instruction file.
10. With the instructions installed, start Claude Code (or Codex, Gemini CLI, Copilot CLI) in `envy-test-code\web` and ask it to "get this project running; it needs PORT and an OPENAI_API_KEY". Expected: it uses `envy status`/`envy set`, runs `envy add OPENAI_API_KEY --secret` and tells you Envy is waiting instead of asking for the key in the chat or writing a `.env`, and runs the app with `envy exec --`.
11. A sandboxed process can't reach Envy: copy `envy.exe` and `envy.json` from `%LOCALAPPDATA%\Envy\shell\bin` to a test folder, run `icacls <folder>\envy.exe /setintegritylevel low`, then from there `envy list` works with "read from Envy's settings file…" on stderr, and `envy set A=1 --global` says "This process isn't allowed to reach Envy, probably because a coding agent's sandbox blocks it…".

## 8. Sandbox

```powershell
$env:ENVY_HOME = "$env:TEMP\envy-sandbox"; & "$env:LOCALAPPDATA\Programs\Envy\Envy.exe"
```

The title shows the sandbox path. Connecting shells there writes `Documents\PowerShell\profile.ps1`, `.bashrc` and `AutoRun.txt` inside the sandbox; your real profiles and registry don't change. Secrets go to `AppData\Roaming\Envy\secrets.json` there, not Credential Manager.

## 9. Uninstall

Settings → Apps → Envy → Uninstall. Expected: the Envy blocks are gone from the profiles and `.bashrc`, Envy's part is gone from AutoRun (anything else stays), `%LOCALAPPDATA%\Envy\shell` (with the envy command) is removed, and so are `~\.claude\rules\envy.md` and `~\.agents\skills\envy`. Compare with the files saved in step 0, then remove the test folders and the `Envy/…` credentials if you don't keep Envy.

## Build

```powershell
winget install --id Microsoft.DotNet.SDK.10 -e     # if dotnet --list-sdks has no 10.0
winget install --id JRSoftware.InnoSetup -e        # only for the installer
git clone -b feature/windows https://github.com/joeystdio/envy.git; cd envy\windows
dotnet test --project tests/Envy.Core.Tests
dotnet publish src/Envy.App -c Release -r win-x64 -p:Platform=x64 -o artifacts/publish   # also publishes cli\envy.exe
& "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" installer\Envy.iss    # artifacts\Envy-Setup.exe
```

(`winget install JRSoftware.InnoSetup` may install per user, in `%LOCALAPPDATA%\Programs\Inno Setup 6`.)
