# SshNotepad

A Notepad-style app for Windows that keeps your notes and open windows on an SSH server you control. Open it on another computer and pick up where you left off. Everything stored on the server is encrypted with your SSH key.

## Features

- **Synced notes and windows.** Text, tabs, window positions, font, zoom and view options are mirrored, usually within a few seconds.
- **Your own server.** It only needs SSH with SFTP; nothing is installed on it.
- **Pageant authentication.** No key files or passwords are stored by the app.
- **Encryption.** Notes and window state are encrypted before upload, and the local copy is encrypted too.
- **Offline editing.** Keep typing without a connection; changes sync when it returns.
- **Conflict handling.** If a note is changed on two computers at once, both versions are kept, marked ⚠, with Compare / Keep this / Keep other / Keep both.
- **History.** The newest 50 saves of each note, plus one per day for 30 days.
- **Tabs or separate windows.** Drag tabs to reorder them, move them out or merge them. Starting the app again opens a new window.
- **Local files.** Open a text file as a synced note; Ctrl+S writes it back with its own encoding and line endings.
- **Spell check, autocorrect, and light and dark themes.**

## Requirements

- Windows 11 (64-bit). Windows 10-based systems may work but are less tested.
- [PuTTY's Pageant](https://www.chiark.greenend.org.uk/~sgtatham/putty/) 0.75 or newer, running, with an **ed25519 or RSA** key loaded. Other key types can't be used for encryption.
- An SSH server with SFTP that accepts that key. Developed against OpenSSH; saving relies on its rename behavior, so other servers are untested.
- To build: the [.NET 10 SDK](https://dotnet.microsoft.com/download).

## Getting started

1. Load your key in Pageant.
2. Start `SshNotepad.exe` and enter the host, port, user and folder (default `~/.sshnotepad`), and choose the key.
3. Check the server's host key fingerprint when asked, then trust it.
4. Do the same on your other computers, with the same key, host and folder.

> [!WARNING]
> Your notes can only be unlocked with your private SSH key. There is no recovery password. **If you lose the key, the notes can't be read again**, so back it up.

## Build

Run these from the repository folder. Build output goes to `%LOCALAPPDATA%\SshNotepadBuild`.

```powershell
dotnet build src\SshNotepad\SshNotepad.csproj
```

To make a single exe that doesn't need .NET installed:

```powershell
dotnet publish src\SshNotepad\SshNotepad.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o app
```

## Test

The local tests need Pageant running with a usable key, and don't contact a server:

```powershell
dotnet run --project src\SshNotepad.Tests
```

To include live tests, which use `~/.sshnotepad-test` on the server, add the login, the host key fingerprint and, optionally, the Pageant key's fingerprint:

```powershell
dotnet run --project src\SshNotepad.Tests -- user@host SHA256:hostkey SHA256:pageantkey
```

`src\ui-test.ps1` drives two copies of the app against a test folder on a server and checks that they mirror each other.

## How it works

- Each save is a new file, `docs/<note id>/<revision>.enc`. Only one computer can create a given revision, so a save never overwrites another computer's.
- Files are encrypted with AES-256-GCM using a random data key. That key is stored in `vault.json`, wrapped with a key derived from a Pageant signature. **Sync > Change encryption key** re-wraps it without re-encrypting the notes.
- The server can't read note text or window state. It can see note IDs, file sizes, save times, and the key fingerprint in `vault.json`.
- Settings and the encrypted local copy are in `%LOCALAPPDATA%\SshNotepad`. Links to local files (their paths, not their contents) and connection settings there are not encrypted.

| Folder | Contents |
|---|---|
| `src/SshNotepad` | The WinForms app |
| `src/SshNotepad.Core` | Sync, encryption, SFTP and Pageant client |
| `src/SshNotepad.Tests` | Console tests |

## License

[GNU General Public License v3.0](LICENSE). Uses [SSH.NET](https://github.com/sshnet/SSH.NET).
