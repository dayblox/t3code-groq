# T3Code-Groq

Single-file C# on-launch patcher and launcher for **T3 Code (Windows)** that redirects OpenAI cloud voice transcription to **Groq Whisper** (`whisper-large-v3` by default), with optional custom icon and app name overrides that survive T3 Code auto-updates.

Zero external dependencies — compiles on any Windows 10/11 machine using the built-in `.NET Framework 4.x` `csc.exe`.

## Features

- **Groq Whisper Cloud Voice:** Patches `resources/server.asar` so T3 Code's OpenAI cloud voice provider sends audio transcriptions to `https://api.groq.com/openai/v1/audio/transcriptions` (`whisper-large-v3` by default), mapping `languages[]` → `language` and custom vocabulary `keywords[]` → `prompt`.
- **Custom Proxy / Endpoint URL:** Override the transcription endpoint with `--url` / `--groq-url` (or `T3CODE_GROQ_URL`), e.g. for a Cloudflare Worker proxy. Trailing slashes are trimmed and `/audio/transcriptions` is appended automatically when a base URL is provided. Arbitrary-length URLs are handled safely by rewriting the ASAR header, shifting subsequent entry offsets, and recomputing SHA-256 integrity hashes.
- **Branding-Only Mode (`--skip-asar`):** Skip `server.asar` transcription patching completely when you only want custom icon and/or app name overrides without touching voice transcription.
- **Automatic Update Survival:** Uses a `<20ms` fast-path state cache (`%LOCALAPPDATA%\T3Code-Groq\state.txt`) keyed on `server.asar`, `app.asar`, endpoint URL, model, branding overrides, and Start Menu shortcuts. Whenever T3 Code auto-updates, the next launch automatically re-patches and re-syncs everything.
- **Merged Taskbar & Start Menu Shortcuts:** `--create-shortcut` creates a Desktop shortcut and syncs the Start Menu shortcut with a dedicated `AppUserModelID` (`com.t3tools.t3code.groq` by default) and passes `T3CODE_DESKTOP_APP_USER_MODEL_ID` when launching T3 Code, so pinning to Start never collides with T3 Code's stock `T3 Code (Nightly)` shortcut while Taskbar pins still group cleanly with the running T3 Code window. `ToastActivatorCLSID` is also preserved.
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

## Usage Examples

### Standard Run (Default Official Groq URL + Branding)

```powershell
# Create a merged shortcut with default Groq Whisper endpoint + custom icon & name
.\T3Code-Groq.exe --create-shortcut --icon --name "My Editor"

# Or patch and launch directly
.\T3Code-Groq.exe --icon "C:\path\to\icon.ico" --name "My Editor"
```

### Custom Proxy URL Run (e.g., Cloudflare Worker)

Both full endpoint URLs and base URLs (without `/audio/transcriptions`) are accepted and normalized automatically:

```powershell
# Full endpoint URL
.\T3Code-Groq.exe --url https://<your-worker>.<your-subdomain>.workers.dev/openai/v1/audio/transcriptions

# Base URL (automatically appends /audio/transcriptions)
.\T3Code-Groq.exe --url https://<your-worker>.<your-subdomain>.workers.dev/openai/v1

# Persist in a merged Desktop & Start Menu shortcut
.\T3Code-Groq.exe --create-shortcut --url https://<your-worker>.<your-subdomain>.workers.dev/openai/v1/audio/transcriptions --icon --name "My Editor"
```

You can also set the `T3CODE_GROQ_URL` environment variable instead of passing `--url` / `--groq-url`.

### Branding-Only Run (Icon / Name Only, Skip Transcription Patch)

Use `--skip-asar` (or `--no-asar`) to bypass `server.asar` transcription patching while still applying custom icon and/or name overrides:

```powershell
# Patch only icon and name, then launch T3 Code
.\T3Code-Groq.exe --skip-asar --icon "C:\path\to\icon.ico" --name "My Editor"

# Create a merged shortcut that always skips server.asar patching
.\T3Code-Groq.exe --create-shortcut --skip-asar --icon --name "My Editor"
```

## CLI Options

| Flag | Description |
|---|---|
| *(none)* | Patch if needed, sync Start Menu shortcut, and launch T3 Code |
| `--url <url>`, `--groq-url <url>` | Custom transcription endpoint URL (default: `https://api.groq.com/openai/v1/audio/transcriptions` or `T3CODE_GROQ_URL`). Trailing slashes are trimmed and `/audio/transcriptions` is appended if omitted. |
| `--model <id>` | Groq Whisper model (default: `whisper-large-v3`, e.g. `whisper-large-v3-turbo`) |
| `--skip-asar`, `--no-asar` | Skip `server.asar` transcription patching (icon/name branding-only mode) |
| `--skip-icon` | Skip custom icon patching and shortcut icon override |
| `--skip-name` | Skip custom window title and shortcut name override |
| `--create-shortcut` | Create a merged Desktop shortcut and sync the Start Menu shortcut |
| `--icon [path]` | Override window, Alt+Tab, Taskbar, and Start icon (`.ico`, `.exe`, or `.dll`). Omit `path` to open a file picker. |
| `--name "<title>"` | Override shortcut name, Start Menu name, and Alt+Tab / window title |
| `--dry-run` | Verify patching and ASAR integrity on a temp copy without modifying the install |
| `--patch-only` | Patch if needed without launching T3 Code |
| `--restore` | Restore original `server.asar`, `app.asar`, and Start Menu shortcut from backups |
| `--install-dir <dir>` | Custom T3 Code install directory (default: `%LOCALAPPDATA%\Programs\t3code` or `T3CODE_INSTALL_DIR`) |
| `-h`, `--help` | Show CLI options and usage examples |

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
- `icon.txt`, `name.txt`, `aumid.txt`, `toast-clsid.txt` — saved overrides, detected `AppUserModelID`, and preserved `ToastActivatorCLSID`
- `startmenu.lnk.bak` — backup of the original Start Menu shortcut
