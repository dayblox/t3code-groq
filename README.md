# T3Code-Groq

Single-file C# on-launch patcher and launcher for **T3 Code (Windows)** that redirects OpenAI cloud voice transcription to **Groq Whisper** (`whisper-large-v3` by default), with optional custom icon and app name overrides that survive T3 Code auto-updates.

Zero external dependencies — compiles on any Windows 10/11 machine using the built-in `.NET Framework 4.x` `csc.exe`.

## Features

- **Groq Whisper Cloud Voice:** Patches `resources/server.asar` so T3 Code's OpenAI cloud voice provider sends audio transcriptions to `https://api.groq.com/openai/v1/audio/transcriptions` (`whisper-large-v3`), mapping `languages[]` → `language` and custom vocabulary `keywords[]` → `prompt`.
- **Automatic Update Survival:** Uses a `<20ms` fast-path state cache (`%LOCALAPPDATA%\T3Code-Groq\state.txt`) keyed on `server.asar`, `app.asar`, and Start Menu shortcuts. Whenever T3 Code auto-updates, the next launch automatically re-patches and re-syncs everything.
- **Merged Taskbar & Start Menu Shortcuts:** `--create-shortcut` creates a Desktop shortcut and syncs the Start Menu shortcut with T3 Code's `AppUserModelID`, so pinning to Start or Taskbar merges cleanly with the running T3 Code window.
- **Optional Icon & Name Overrides:** `--icon` and `--name` override the Desktop shortcut, Start Menu / Pin to Start entry, Taskbar button, and Alt+Tab / window icon and title. Omitting them keeps T3 Code's defaults.
- **Safe Backups & Restore:** Automatically backs up `server.asar`, `app.asar`, and the original Start Menu shortcut before patching. `--dry-run` tests patching on a temp copy without touching your install; `--restore` reverts everything.

## Quick Start

1. Download `T3Code-Groq.exe` from the [latest release](https://github.com/dayblox/t3code-groq/releases/latest) (or build from source below) and place it in a permanent folder (e.g. `C:\Tools\T3Code-Groq.exe`).
2. In T3 Code, set your **OpenAI** cloud voice API key to your **Groq API key** (`gsk_...`).
3. Create a merged shortcut (and sync Start Menu):
   ```powershell
   .\T3Code-Groq.exe --create-shortcut
   ```
   Or with a custom icon (opens a Windows file picker) and/or custom name:
   ```powershell
   .\T3Code-Groq.exe --create-shortcut --icon --name "My Editor"
   ```
4. Pin the created shortcut to your Taskbar or Start Menu and launch T3 Code from it.

## CLI Options

| Flag | Description |
|---|---|
| *(none)* | Patch if needed, sync Start Menu shortcut, and launch T3 Code |
| `--create-shortcut` | Create a merged Desktop shortcut and sync the Start Menu shortcut |
| `--icon [path]` | Override window, Alt+Tab, Taskbar, and Start icon (`.ico`, `.exe`, or `.dll`). Omit `path` to open a file picker. |
| `--name "<title>"` | Override shortcut name, Start Menu name, and Alt+Tab / window title |
| `--model <id>` | Groq Whisper model (default: `whisper-large-v3`, e.g. `whisper-large-v3-turbo`) |
| `--dry-run` | Verify patching and ASAR integrity on a temp copy without modifying the install |
| `--patch-only` | Patch if needed without launching T3 Code |
| `--restore` | Restore original `server.asar`, `app.asar`, and Start Menu shortcut from backups |
| `--install-dir <dir>` | Custom T3 Code install directory (default: `%LOCALAPPDATA%\Programs\t3code` or `T3CODE_INSTALL_DIR`) |

## Build from Source

No SDK installation required — uses Windows' built-in C# compiler:

```powershell
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" `
  /nologo /optimize+ /target:winexe /out:T3Code-Groq.exe `
  /r:System.Web.Extensions.dll /r:System.Windows.Forms.dll `
  T3CodeGroq.cs
```

## State & Logs

All state, backups, and logs live in `%LOCALAPPDATA%\T3Code-Groq\`:
- `state.txt` — fast-path launch cache
- `patcher.log` — patching and sync log
- `icon.txt`, `name.txt`, `aumid.txt` — saved overrides and detected `AppUserModelID`
- `startmenu.lnk.bak` — backup of the original Start Menu shortcut
