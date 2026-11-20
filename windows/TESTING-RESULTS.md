# Envy for Windows: test results

A full pass of [TESTING.md](TESTING.md) on a real PC, with dummy values for every test secret. Failures found on the way were fixed on `feature/windows`, rebuilt, reinstalled and checked again; the commit is named next to each.

- **PC:** Windows 11 Pro 10.0.26300, 1920×1080 at 100 %.
- **Shells:** PowerShell 7.6.9 (Microsoft Store), Windows PowerShell 5.1, Git for Windows 2.44.0 (Git Bash), Command Prompt.
- **Build tools:** .NET SDK 10.0.401 and Inno Setup 6. Both were already installed, so no winget installs were needed.
- **How things were checked:** the GUI was driven with mouse and keyboard and screenshotted. Files, the registry and Credential Manager were checked from PowerShell (`Get-Content`, `reg query`, `cmdkey /list`, `icacls`, hashes). Shell behaviour was checked in new shell windows started through Explorer (so they had a fresh environment), each running the checklist's commands from a script and logging the output.
- **Where the PC differed from the checklist's assumptions:**
  - The user environment already held other secret-looking variables.
  - A folder of real projects was imported during the Setup Assistant, alongside `envy-test-code`.
  - A Microsoft sign-in window and a pinned Claude window sometimes covered the screen.

Results: **pass**, **pass after fix** (failed, fixed, verified), **not tested** (with the reason), **n/a**.

## Fixes

| Commit | Problem | Found in |
| --- | --- | --- |
| `efa5479` | Git Bash started from a PowerShell or Command Prompt where Envy set PATH lost `/usr/bin` and `/mingw64/bin`, so `ls` and `cygpath` were gone and PATH stayed in Windows form. CI missed it because its runners have Git's `usr\bin` on the Windows PATH. | Build (`dotnet test`), 4.4 |
| `71978c0` | Envy started from PowerShell 7 passed its PSModulePath on to Windows PowerShell, which then couldn't run `Get-ExecutionPolicy` or `Set-ExecutionPolicy` (the shell check and "Allow My Profile Scripts"). | Build (`dotnet test` output), 7 |
| `de58e70` | Command Prompts started in the same second shared one `run-%RANDOM%%RANDOM%.cmd` copy of `vars.cmd` and deleted it under each other: 6 of 8 started together loaded nothing. | 4 (Command Prompt) |
| `bd92929` | A project page's header (name, folders, On/Off) disappeared after a change made on that page, sometimes. It failed with 0x800F1000 while re-adding the name box to a new panel. When the change came from Add & New, the dialog was left stuck and Envy had to be restarted. | 3.3, 3.5, 3.6 |
| `cb5a315` | A "Ctrl+N" tooltip followed the pointer around the window, often covering sidebar entries. | 2.2, 3.x |
| `78259c8` | Ctrl+V in the table, a dropped file, and the variable editor kept only the first line of their text: the text box got its text before `AcceptsReturn`. | 5.3 |
| `b1c86e4` | Dialogs clipped their right 12 pixels (for example the "Import 4 Projects" button and the value field's eye). Long sentences in horizontal rows were cut off mid-sentence: the Setup Assistant's Connect notes and the Shell Integration status card. | 2.2, 2.5, 3.3, 6 |

`dotnet test --project tests/Envy.Core.Tests`: 87 tests, 2 failing on this PC at the start. At the end: 88 tests (one added for concurrent Command Prompts), all passing.

## 0. Before you start

| Item | Result | How it was checked |
| --- | --- | --- |
| Save the current state | pass | Ran the block. `HKCU\Software\Microsoft\Command Processor` didn't exist and neither did `$PROFILE.CurrentUserAllHosts`, so there was nothing to export or copy for those. The Windows PowerShell host profile was saved as well. |
| Plant plain-text secrets | pass | Ran the block, then read the profile and `.bashrc` and checked `ENVY_TEST_USER_API_KEY` at User scope. |
| Folder of projects | pass | Ran the block and listed `envy-test-code` and the user-secrets file. |

## 1. Install

| Item | Result | How it was checked |
| --- | --- | --- |
| 1.1 Build the installer | pass after fix | Two tests failed on this PC (`efa5479`). After the fixes, `dotnet test`, `dotnet publish` and `ISCC.exe` all succeeded, producing `artifacts\Envy-Setup.exe`. |
| 1.2 Run `Envy-Setup.exe` | pass (SmartScreen n/a) | Opened through the shell (Explorer was covered by a sign-in window). There was no SmartScreen prompt because a local build has no mark of the web (no `Zone.Identifier` stream), and no UAC prompt. Envy landed in `%LOCALAPPDATA%\Programs\Envy`, with the uninstall entry under HKCU. "Open Envy" started the app. |
| 1.3 Mica, Setup Assistant, tray icon | pass | The Setup Assistant covered the window on first start. `MainWindow` sets `MicaBackdrop`. The notification-area icon is registered: an `HKCU\Control Panel\NotifyIconSettings` entry for Envy.exe with tooltip "Envy". Windows put it in the overflow. |

## 2. Setup Assistant

| Item | Result | How it was checked |
| --- | --- | --- |
| 2.1 Welcome | pass | Screenshot: all four shells with their paths, plus "Claude Code, Copilot CLI and Gemini CLI". |
| 2.2 Connect | pass after fix | All four were ticked. After Connect, each showed "Verified: a new … loads Envy". No execution-policy prompt was needed (CurrentUser was already RemoteSigned). The Command Prompt note was cut off mid-sentence (`b1c86e4`); after the fix it wraps (checked in the sandbox). |
| 2.3 Check the files | pass | Read both profiles and `.bashrc`: each has the `# >>> Envy >>>` block. `.envy-backup` exists where the file existed before; the Windows PowerShell `profile.ps1` was new. `reg query … /v AutoRun` shows only Envy's part, since there was no earlier value; the "after what was there" order is covered by the test `AutoRunValueRunsEnvyAfterWhatWasThere`. |
| 2.4 Secrets | pass, with a caution | The list also held real user environment variables. 8 of them were ticked along with the test ones (secret-looking names come pre-ticked), so Import brought in 11, not 3. "Remove Them for Me" then deleted those 8 from the user environment; they were restored from the step 0 export. Checked on disk: the test lines are gone from the profile and `.bashrc`, the backups still hold them, and `ENVY_TEST_USER_API_KEY` is gone from the User scope. |
| 2.5 Projects | pass after fix | Import Projects → Choose Folder… → `envy-test-code` found lab (.envrc), shop (.env + .env.local), Shop.Api (appsettings + Development + user secrets) and web (.env), and created 4 projects. The "Import 4 Projects" button was clipped (`b1c86e4`). |
| 2.6 Done | pass | "You're set" → Start Using Envy wrote `.onboarded`. Seen again in the sandbox. |

## 3. Variables and secrets

| Item | Result | How it was checked |
| --- | --- | --- |
| 3.1 Masking, eye, lock, credentials | pass | Global showed the imported secrets as ••••. The eye showed and hid a value. The lock on a test variable made it plain, then secret again: config and Credential Manager followed (430 → 429 → 430). `cmdkey /list` had exactly one `Envy/<id>` per secret in the config, and none left over. Once, a second eye click revealed the next row instead; that didn't reproduce in three more tries while the PC was in use by someone else. |
| 3.2 Shop.Api | pass | `Api__BaseUrl` = `https://localhost:7001`; `Stripe__ApiKey` came from user secrets and is secret. |
| 3.3 Add Variable | pass after fix | Ctrl+N opened the dialog, and `GITHUB_TOKEN` pre-ticked Secret. "Add & New" saved the variable and kept the dialog open. The first time, the form didn't clear and the dialog got stuck (`bd92929`). After the fix, Add & New cleared the form and kept the header intact. |
| 3.4 TOOLS and PATH in Global | pass | Both were added with Expand on. In the config, `expands` is true; `vars.ps1` builds them from `USERPROFILE` and `PATH`. |
| 3.5 Turn things off | pass after fix; tray not tested | Unticking a variable removed it from `vars.ps1`, `vars.sh` and `vars.cmd`. Switching shop off in the sidebar and on from its header updated the config and the generated files, but the header sometimes vanished (`bd92929`). After the fix: 9 changes in a row, header intact. The tray icon's right-click menu was **not tested**: it needs a right-click in the notification area, which the test setup couldn't do. |
| 3.6 Undo and redo | pass | Unticked API_URL, then Ctrl+Z, Ctrl+Y and Ctrl+Z; the saved config followed each step. |

## 4. Shells

PowerShell 7, Windows PowerShell 5.1 and Git Bash each ran in a new window started from Explorer. In PowerShell, "the next prompt" was simulated by calling `prompt`, which Envy wraps and the host runs before each prompt; in PowerShell 7, `cd` also triggers the hook by itself. The Git Bash window was an interactive `bash -l -i` reading the commands, so it went through real prompt cycles (`PROMPT_COMMAND`).

| Item | PowerShell 7 | Windows PowerShell | Git Bash | How it was checked |
| --- | --- | --- | --- | --- |
| 4.1 status, TOOLS, PATH, git | pass | pass | pass | `envy status` listed Global and its variables. `TOOLS` = `%USERPROFILE%\tools`. The first PATH entry was the envy-bin folder (`/c/Users/<you>/envy-bin` in Git Bash), and `git --version` worked. |
| 4.2 cd into `shop\sub` and back | pass | pass | pass | In `shop\sub`, `API_URL` = `http://127.0.0.1` and status said "Global, shop". After `cd ~`, `API_URL` was empty. |
| 4.3 Toggle shop with the window open | pass | pass | pass | All three windows waited while shop was switched off in Envy; at the next prompt `API_URL` was empty. Then on again; at the next prompt it was back. |
| 4.4 Git Bash from PowerShell, cmd from Git Bash | pass after fix | pass after fix | pass | `bash -l` from each PowerShell: `TOOLS` right, envy-bin first, `/usr/bin/ls` and `/usr/bin/cygpath` found, git works (`efa5479`). Command Prompt from Git Bash: `TOOLS` right, envy-bin first, `ls` and `git` found, `envy status` works. |
| 4.5 envy reload, envy open | pass | pass | pass | `envy reload` printed the status. `envy open` restored a minimized Envy and brought it to the foreground, checked from each shell with Envy minimized first. |

Command Prompt, in a new window started in `envy-test-code\shop` with `cmd /k` (AutoRun included):

| Item | Result | How it was checked |
| --- | --- | --- |
| 1. `API_URL` and `envy status` | pass | `API_URL=http://127.0.0.1`; status said "Global, shop"; the `envy` doskey macro was defined. |
| 2. `cd %USERPROFILE%`, `envy reload` | pass | `API_URL` was empty after the reload. Shell Integration says Command Prompt "doesn't switch when you cd. Run envy reload after cd." |
| 3. `&`, `^`, `%` and quotes | pass | A Global test value `R&D ^caret 100% "quoted" it's` came out intact in `set ENVY_TEST_SPECIAL`. |
| Command Prompts started together | pass after fix | Not in the checklist, but found here: 8 `cmd /c` started at once, only 2 loaded Envy (`de58e70`). After the fix, 8 of 8 and 16 of 16 loaded it, with no leftover run folders. One start with AutoRun takes about 120 ms here, against 24 ms without. |

## 5. Import, copy and export

| Item | Result | How it was checked |
| --- | --- | --- |
| 5.1 Paste or Open .env File | pass | Pasting `A=1` and `B="two words"` showed A = 1 and B = two words, and imported them. Open File… → `Shop.Api\appsettings.json` showed `Api__BaseUrl` and `ConnectionStrings__Db` (secret). |
| 5.2 Drag a .env file onto a project page | not tested | Dragging from Explorer or a file dialog wasn't possible in this setup. The drop handler opens the same import dialog with the file's text, which lost all but the first line before `78259c8`. |
| 5.3 Copy to, Move to, Copy as .env, Ctrl+C, Ctrl+V | pass after fix | Copy to put A in lab and kept it in web; Move to took B out of web into lab. Copy as .env put `PORT=3000` on the clipboard, and Ctrl+C on a selected row put `A=1`. Ctrl+V of two lines opened the import dialog with only the first line (`78259c8`); after the fix, both lines arrived and were imported. |
| 5.4 Export .env (Ctrl+Shift+E) | pass | Saved `web-export.env`: header plus every enabled variable, the secret's value included. Leaving disabled variables out is covered by `ExportWritesEnabledVariablesWithAHeader`. |
| 5.5 New Project with a folder that has a .env | pass | Choose… → a new folder with a `.env` filled in the name and offered "Import its 2 variables (.env)". The project was created with both, the token as a secret. |

## 6. Generated files

| Item | Result | How it was checked |
| --- | --- | --- |
| 6.1 Show in File Explorer, permissions | pass | Opened Explorer at `%LOCALAPPDATA%\Envy\shell` with `init.ps1` selected. `icacls` lists only the signed-in account (full control). |
| 6.2 Preview with secrets masked | pass after fix | The PowerShell and Git Bash previews show `••••••` for secrets, Command Prompt `******`. The status card above was cut off (`b1c86e4`); it wraps now. |

## 7. Sandbox

| Item | Result | How it was checked |
| --- | --- | --- |
| Title, files, secrets | pass | Started with `ENVY_HOME=%TEMP%\envy-sandbox` from PowerShell 7. The title shows the sandbox path. Connecting wrote `Documents\PowerShell\profile.ps1`, `Documents\WindowsPowerShell\profile.ps1`, `.bashrc`, `.bash_profile` and `AutoRun.txt` inside the sandbox. A test secret went to the sandbox's `AppData\Roaming\Envy\secrets.json`, and the Credential Manager count stayed the same. Hashes of the real profiles, `.bashrc`, `.bash_profile` and config, plus the AutoRun value, were identical before and after. Its Windows PowerShell check, run with PowerShell 7's module path, no longer failed (`71978c0`). |

## 8. Uninstall

| Item | Result | How it was checked |
| --- | --- | --- |
| Uninstall and compare | pass | Ran Envy's registered uninstaller (`unins000.exe`, the same one Settings → Apps → Uninstall runs) silently, because the Settings window couldn't be operated here. Afterwards: the install folder, uninstall entry and `%LOCALAPPDATA%\Envy\shell` were gone, no Envy lines were left in either profile or `.bashrc`, and the AutoRun value was gone (there was nothing else to keep). The config and credentials stayed, as designed. Compared with step 0: the user environment export is byte-identical, and `.bash_profile` and the Windows PowerShell host profile are unchanged; see the notes for `.bashrc` and the profile files. |

## Notes (not failures)

- **Secret detection goes by name only.** A `*_URL` variable whose value held a database password was imported as plain, and showed unmasked in the table and the previews.
- **"Remove Them for Me" also deletes real tokens.** It deletes every ticked user environment variable, including real ones other programs rely on, and secret-looking names come pre-ticked. Working as designed, but easy to do by accident.
- **The row menu (Copy to, Move to, Copy as .env, Delete) is hard to reach.** It opens when you right-click a row's checkbox or buttons. Right-clicking the name or value shows the text box's own menu instead.
- **Tab can skip the value box.** Pressing Tab right after typing a secret-looking name can skip the value box, since it's being swapped for a password box at that moment. Seen with fast synthetic input only.
- **Starting Envy from a terminal prints its shell checks there.** With `& Envy.exe` (as in step 7), the checks print bash job-control warnings and Windows PowerShell progress records into that terminal.
- **Line endings in startup files.** `StartupFiles` writes the whole file with CRLF once it contains any CRLF. Step 0's `Add-Content` appended a CRLF line to an LF `.bashrc`, so after the test it was all CRLF, plus the blank line from the planted line's leading `` `n ``. Git Bash reads it fine.
- **Files left behind.** Uninstalling leaves the `.envy-backup` files, which still hold the removed values. It also leaves a `profile.ps1` that Envy created, now empty.
- **Command Prompt start-up cost grows with the config.** With about 2,000 variables across 36 projects, `vars.cmd` is about 0.8 MB, and every Command Prompt reads it at start (about 120 ms here).
