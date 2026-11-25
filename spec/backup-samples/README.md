# Sample backups

Shared test vectors for [the backup format](../backup-format.md). All values are dummies.

Password for every sample: `correct horse battery`

| File | Expected |
| --- | --- |
| `windows.envybackup` | Opens; contents equal `windows.expected.json` (made on Windows). |
| `macos.envybackup` | Opens; contents equal `macos.expected.json` (Mac-style contents). |
| `newer-version.envybackup` | "This backup was made by a newer version of Envy. Update Envy to restore it." |
| `damaged.envybackup` | Last byte of the tag changed: "Wrong password, or the file is damaged." |
| `windows.envybackup` with any other password | "Wrong password, or the file is damaged." |

## Expanding values

`macos.envybackup` restored on Windows lists these (in this order):

| Where | Key | Suggestion | Default |
| --- | --- | --- | --- |
| Global | `PATH` | `%USERPROFILE%\bin;$PATH` | Use suggestion |
| Global | `TOOLS` | none (runs a command) | Keep as is |
| Global | `APP_CONFIG` | `%USERPROFILE%\.config\app` | Use suggestion |

`CACHE` (`${XDG_CACHE_HOME}/envy`) isn't listed: it works on both.

`windows.envybackup` restored on a Mac lists these:

| Where | Key | Suggestion | Default |
| --- | --- | --- | --- |
| Global | `PATH` | `$HOME/bin:${PATH}` | Use suggestion |
| Global | `JAVA_BIN` | `${JAVA_HOME}/bin` | Use suggestion |
| Global | `WHO` | none (would run a command on a Mac) | Turn Expand off |
| api | `API_URL` | `http://localhost:${PORT}/api` | Use suggestion |

`GREETING` (`hello ${USERNAME}`) isn't listed.

## Folders

`macos.envybackup` holds `~/Developer/api`, `/Volumes/Code/api-old` and `~/Developer/web`; `windows.envybackup` holds
`~/code/api` and `D:\work\site`. Restored, `~/…` becomes the local home folder; the absolute ones from the other system
are kept and reported as missing.
