# Envy backup format, version 1

One file holds everything Envy manages: Global and every project, with their folder links, variables and secret values.
It is always encrypted with a password. Envy for macOS and Envy for Windows both write and read it, so a backup made on
one restores on the other. This document is the contract between the two apps; the samples in
[`backup-samples/`](backup-samples) are the shared test vectors.

## File

- Extension `.envybackup`. Suggested name: `Envy Backup YYYY-MM-DD.envybackup`.
- UTF-8 JSON (any whitespace). Unknown fields are ignored.

```json
{
  "format": "envy-backup",
  "version": 1,
  "kdf": { "name": "PBKDF2-HMAC-SHA256", "iterations": 600000, "salt": "<base64, 16 bytes>" },
  "cipher": { "name": "AES-256-GCM", "nonce": "<base64, 12 bytes>" },
  "data": "<base64 of ciphertext followed by the 16-byte tag>"
}
```

Nothing about the contents (names, counts, paths, dates) is outside the encrypted `data`.

### Reading

1. Not JSON, or `format` isn't `envy-backup`: "This isn't an Envy backup."
2. `version` greater than 1: "This backup was made by a newer version of Envy. Update Envy to restore it."
3. `kdf.name` or `cipher.name` unknown, `iterations` outside 100,000…10,000,000, salt not 16 bytes, nonce not 12
   bytes, or `data` shorter than 16 bytes: "This backup is damaged."
4. Decryption fails (tag mismatch): "Wrong password, or the file is damaged."

## Encryption

- Password: normalised to Unicode NFC, then UTF-8 encoded.
- Key: PBKDF2-HMAC-SHA256(password, salt, iterations) → 32 bytes. Writers use 600,000 iterations and a random salt.
- Cipher: AES-256-GCM with a random 12-byte nonce, a 16-byte tag, and the additional authenticated data
  `envy-backup v1` (14 bytes of ASCII). `data` is the ciphertext with the tag appended
  (CryptoKit: `AES.GCM.SealedBox(combined: nonce + data)`; .NET: `AesGcm.Decrypt(nonce, data[..^16], data[^16..], …)`).
- Password rules when writing: at least 12 characters, typed twice. The password is never stored.

## Contents (the decrypted `data`)

UTF-8 JSON. Field names match the apps' own settings files.

```json
{
  "app": "Envy 1.2.0",
  "platform": "windows",
  "created": "2026-11-25T05:00:00Z",
  "global": { "name": "Global", "isEnabled": true, "folders": [], "variables": [ … ] },
  "projects": [
    {
      "name": "api",
      "isEnabled": true,
      "folders": ["~/code/api"],
      "variables": [
        { "key": "PORT", "value": "4000", "isEnabled": true, "isSecret": false, "expands": false, "note": "" },
        { "key": "OPENAI_API_KEY", "value": "sk-…", "isEnabled": true, "isSecret": true, "expands": false, "note": "" }
      ]
    }
  ]
}
```

- `platform` is `macos` or `windows`: the system the backup was made on. `created` is ISO 8601 in UTC.
- Projects are in sidebar order. Every variable is included, switched off or not, with secret values in `value`.
  No IDs: restoring gives everything new IDs (Global keeps its fixed one).
- Missing fields take the same defaults as in the settings files: `isEnabled` true, the rest false or empty.

### Folders

- A folder inside the home folder is written relative to it with forward slashes: `~` for the home folder itself,
  `~/code/api` below it. The comparison ignores case.
- Any other folder is written as the absolute path it is (`D:\work\api`, `/Volumes/Code/api`).
- Restoring turns `~` and `~/…` into the local home folder with the local separator. Other paths are kept as they are.

## Restoring

The app asks for the file and the password, then shows what will happen before anything changes. The change is one
undo step ("Restore Backup").

### Modes

- **Add to what's here** (the default). Nothing already on the machine is deleted.
  - Global: the backup's variables merge into Global.
  - A backup project whose name matches a project here (ignoring case) merges into it: its variables merge, and its
    folders are added to that project's folders. The project's own on/off state stays as it is here.
  - Any other backup project is added at the end, with its on/off state from the backup.
  - Merging a variable: one with the same key (compared as the restoring system compares names: ignoring case on
    Windows, exactly on macOS) is replaced by the backup's, including its on/off state, secret flag, expand flag and
    note. Others are added.
  - A folder already linked to a different project here is not linked again (one folder never loads two projects); the
    summary lists it. A new project that loses all its folders this way is restored switched off, so its variables don't
    load in every terminal, and the summary says so.
- **Replace everything.** Global and every project here are replaced by the backup's.

### Summary

Before restoring, the app shows: projects added, merged and (Replace) removed; variables added and replaced; the number
of secrets; folders that don't exist on this machine (restored anyway; the project shows the folder as missing, so it
can be linked again); folder links that were skipped; and the expanding values below.

### Expanding values from the other system

Only when `platform` differs from the restoring system, and only for variables with `expands` true. Values that already
mean the same on both (`$NAME`, `${NAME}`, a leading `~`) aren't shown. Each listed value has a suggestion (or none) and
a choice: **Use suggestion** (the default when there is one, editable), **Keep as is**, or **Turn Expand off**.

From macOS, restoring on Windows (Windows expands `%NAME%`, `$NAME`, `${NAME}`, `$env:NAME` and a leading `~`, and never
runs commands):

| Found | Reason shown | Suggestion |
| --- | --- | --- |
| `$(` or a backtick | "Runs a command. Windows doesn't run commands in values." | none; default **Keep as is** |
| `$HOME` or `${HOME}` | "$HOME isn't set in PowerShell or Command Prompt." | each becomes `%USERPROFILE%` |
| a key that is or ends in `PATH`, with `:` | "Windows separates folders with ;." | `:` becomes `;` |

From Windows, restoring on macOS (macOS expands the value like a double-quoted shell string, including `$(command)`):

| Found | Reason shown | Suggestion |
| --- | --- | --- |
| `$(` or a backtick | "This would run a command on a Mac; on Windows it was plain text." | none; default **Turn Expand off** |
| `%NAME%` or `$env:NAME` | "A Mac doesn't understand %NAME% or $env:NAME." | `%USERPROFILE%` becomes `$HOME`; any other `%NAME%` and `$env:NAME` become `${NAME}` |
| a key that is or ends in `PATH`, with `;` | "A Mac separates folders with :." | `;` becomes `:` |

When a suggestion is made (rows 2 and 3 in either direction), the separators in the rest of the value follow the new
system too: `/` becomes `\` on Windows and `\` becomes `/` on a Mac, unless the value contains `://`. `NAME` is a name
of letters, digits and underscores not starting with a digit; other `%…%` text is left alone. Rows apply together, in
order, and the reasons are joined.

## Samples

[`backup-samples/`](backup-samples) holds backups with known passwords and their expected decrypted contents. Both apps'
tests open them, check the contents, and check the restore summary and suggestions they produce.
