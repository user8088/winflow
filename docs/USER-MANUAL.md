# WinFlow User Manual

This guide explains how to install, configure, and use WinFlow on Windows.

## What WinFlow does

WinFlow is a **push-to-talk dictation** tool. You hold a key, speak naturally, and when you release the key your words are transcribed and pasted at the text cursor in whatever application is focused.

It works in text editors, browsers, chat apps, email clients, and terminals.

## System requirements

| Requirement | Details |
|-------------|---------|
| Operating system | Windows 10 or Windows 11 (64-bit recommended) |
| Microphone | Any working input device (built-in, headset, USB mic) |
| .NET runtime | .NET 10 (included when you build from source with the SDK; bundled in release builds) |
| Disk space | ~50 MB for the app; **~631 MB additional** if you download the offline model |
| Internet | Required for cloud mode and for downloading the offline model; not required for local dictation after the model is installed |
| OpenAI API key | Required for cloud mode only; not needed for local/offline mode |

## Installation

### From source (developers)

1. Install the [.NET 10 SDK](https://dotnet.microsoft.com/download).
2. Clone the repository:
   ```powershell
   git clone https://github.com/YOUR_USERNAME/winflow.git
   cd winflow
   ```
3. Build and run:
   ```powershell
   dotnet build WinFlow.slnx -c Release
   dotnet run --project src/WinFlow.App -c Release
   ```
4. The executable is also at:
   `src\WinFlow.App\bin\Release\net10.0-windows\WinFlow.App.exe`

### From a release build

When available, download `WinFlow.App.exe` (or the installer) from GitHub Releases and run it. Windows SmartScreen may warn about unsigned binaries from new publishers — this is normal for early releases.

### After launch

WinFlow has **no main window**. It runs in the **system tray**:

1. Look for a small **gray dot** near the clock.
2. If you do not see it, click the **^** (show hidden icons) arrow in the taskbar.
3. On first launch, a notification explains how to get started.

Only one instance can run at a time. If you launch WinFlow again, you will see a message that it is already running.

## Basic usage

### Dictate text

1. Click into any text field (Notepad, Word, VS Code, a browser, Slack, etc.).
2. **Hold Right Ctrl** on your keyboard.
3. Speak clearly while holding the key.
4. **Release Right Ctrl**.
5. Wait briefly — the tray icon turns orange while transcribing — then your text appears at the cursor.

### Visual feedback

| Tray icon color | Meaning |
|-----------------|---------|
| Gray | Idle (cloud mode) |
| Green | Idle (local/offline mode active) |
| Red | Recording — speak now |
| Orange | Transcribing |

A small **HUD pill** also appears near the bottom-center of your screen while recording or processing. It shows audio level bars during recording and a brief success (✓) or error message when done.

## Configuration

Right-click the tray icon to open the menu.

### Set OpenAI API key (cloud mode)

1. Right-click tray icon → **Set OpenAI API key…**
2. Paste your OpenAI API key (starts with `sk-`).
3. Click **Save**.

The key is stored in **Windows Credential Manager** (encrypted per user). It is never written to plain-text settings files.

To remove a key: open the same dialog and click **Remove**.

> **Note:** Cloud mode uses the OpenAI API and incurs usage charges on your OpenAI account. Check [OpenAI pricing](https://openai.com/pricing) for current rates.

### Download the offline model (local mode)

Local mode runs entirely on your PC — no API key, no internet after download.

1. Right-click tray icon → **Offline model…**
2. Review the install location. Click **Change…** to pick a different drive if needed (~631 MB free space required).
3. Click **Download**.
4. Wait for all files to download and verify. Downloads are **resumable** — you can close the window and continue later.
5. After installation, set transcription mode to **Local** or **Auto**.

Default install path: `%LOCALAPPDATA%\WinFlow\models\parakeet-tdt-0.6b-v2-int8`

### Transcription mode

Right-click tray icon → **Transcription mode**:

| Mode | Behavior |
|------|----------|
| **Cloud (OpenAI)** | Uses OpenAI Realtime streaming + batch fallback. Requires API key. Lowest latency. |
| **Local (offline, free)** | Uses on-device Parakeet model. Requires downloaded model. Works offline. |
| **Auto** | Uses local model when installed; otherwise uses cloud. Recommended for most users. |

The tray icon is **green** when local mode is the active backend.

### Input method

Right-click tray icon → **Input method**:

| Mode | Behavior |
|------|----------|
| **Auto (detect terminals)** | Pastes in most apps; types character-by-character in terminals and Electron apps (Cursor, VS Code) where synthetic paste fails |
| **Paste (Ctrl+V)** | Clipboard + synthetic Ctrl+V. Fast; may fail in some terminals |
| **Type (best for terminals/Cursor)** | SendInput Unicode keystrokes. Slower but reliable in terminals |

### Transcript correction

Right-click tray icon → **Transcript correction**:

| Mode | Behavior |
|------|----------|
| **Off (verbatim)** | Inject the raw STT transcript unchanged |
| **Auto-correct** | Fix messy or incomplete transcripts; skip already-clean takes |
| **Aggressive** | Always run correction, including stronger completion for broken English |

Cloud mode uses OpenAI; local mode uses an optional on-device correction model (download via **Correction model…**).

### Open recordings folder

If you enabled debug recording (see [Advanced](#advanced-options)), completed takes are saved as WAV files. Open them via:

Right-click tray icon → **Open recordings folder**

Default path: `%APPDATA%\WinFlow\recordings`

### Exit

Right-click tray icon → **Exit**

## Tips for best results

- **Speak while holding the key** — audio is only captured between press and release.
- **Hold for at least ~0.25 seconds** — very short taps are ignored as accidental presses.
- **Speak audibly** — silent or near-silent takes are rejected (no API call, no transcription).
- **Use a headset mic** for noisy environments. Cloud mode applies near-field noise reduction by default.
- **Focus the target field first** — WinFlow pastes into whatever app has keyboard focus.
- **First local dictation may be slower** — the ONNX model loads on first use (~1–2 seconds).

## Troubleshooting

### WinFlow is not in the tray

- Check the hidden icons overflow (**^** next to the clock).
- Open Task Manager and end any existing `WinFlow.App` process, then relaunch.

### "WinFlow could not install its keyboard hook"

Another app may be blocking low-level hooks, or you may need to run as a normal user (not inside a restricted remote session). Restart WinFlow. If the problem persists, reboot and try again.

### No text appears after dictating

| Symptom | Likely cause | What to do |
|---------|--------------|------------|
| Nothing happens, no error | Accidental tap / no speech detected | Hold longer and speak clearly |
| Toast: transcription failed | No API key, invalid key, or no network (cloud mode) | Set API key or switch to local mode |
| Toast: couldn't paste | Target app blocked paste, or no focused text field | Click into a text field; press **Ctrl+V** manually (text may be on clipboard) |
| Nothing in elevated app | UIPI blocks injection into admin apps | Run WinFlow as administrator, or dictate into a non-elevated window |

### Cloud mode errors

- **401 / unauthorized** — API key is invalid or expired. Update it in the tray menu.
- **Network errors** — Check internet connection. The batch fallback may still succeed if streaming fails.
- **Slow first dictation** — Normal; subsequent dictations are faster thanks to a pre-warmed WebSocket connection.

### Local mode errors

- **"No offline model installed"** — Download the model via **Offline model…** first.
- **"Not enough space on the disk"** — Free at least ~631 MB or choose a different drive with **Change…**.
- **Slow transcription** — Local mode transcribes after you release the key (batch). First run loads the model. A modern CPU is recommended.

### Microphone not working

- Check Windows **Settings → Privacy → Microphone** — allow desktop apps to access the microphone.
- Confirm the correct input device is set as default in **Settings → System → Sound**.
- Close other apps that may have exclusive access to the microphone.

### Clipboard side effects

WinFlow temporarily replaces your clipboard to paste text, then restores the previous clipboard text (~300 ms later). If you had an image or file copied, only text is preserved today — copy it again after dictating if needed.

## Advanced options

### Settings file

User preferences are stored at:

```
%APPDATA%\WinFlow\settings.json
```

Example:

```json
{
  "sttMode": "Auto",
  "nearFieldMic": true,
  "language": null,
  "modelDirectory": null,
  "inputMethod": "Auto",
  "correctionMode": "AutoCorrect"
}
```

| Field | Values | Description |
|-------|--------|-------------|
| `sttMode` | `Cloud`, `Local`, `Auto` | Transcription backend |
| `nearFieldMic` | `true` / `false` | `true` for headsets; `false` for laptop built-in mics (cloud noise reduction) |
| `language` | e.g. `"en"` or `null` | Reserved for future language selection |
| `modelDirectory` | path or `null` | Custom offline model install root |
| `inputMethod` | `Auto`, `Paste`, `Type` | How text is delivered to the focused app (see [Input method](#input-method)) |
| `correctionMode` | `Off`, `AutoCorrect`, `Aggressive` | Transcript cleanup before injection (see [Transcript correction](#transcript-correction)) |

You can edit this file while WinFlow is closed. Most users should use the tray menu instead.

### Environment variables (developers / debugging)

| Variable | Effect |
|----------|--------|
| `WINFLOW_FAKE_STT=1` | Skip real transcription (returns fake text; for testing) |
| `WINFLOW_SAVE_RECORDINGS=1` | Save every completed take as WAV |
| `WINFLOW_ALLOW_INJECTED=1` | Allow synthetic key events to trigger recording (automated tests) |
| `WINFLOW_MODELS_DIR` | Override default model directory (legacy; prefer settings UI) |

Example:

```powershell
$env:WINFLOW_SAVE_RECORDINGS = "1"
dotnet run --project src/WinFlow.App -c Release
```

### API key storage location

Keys are stored in Windows Credential Manager under the target name `WinFlow/OpenAI`. You can view or remove them in **Control Panel → Credential Manager → Windows Credentials**.

## Privacy

| Data | Where it goes |
|------|---------------|
| Audio (cloud mode) | Sent to OpenAI for transcription |
| Audio (local mode) | Processed entirely on your device |
| API key | Windows Credential Manager (local, encrypted) |
| Settings | `%APPDATA%\WinFlow\settings.json` (local) |
| Debug recordings | `%APPDATA%\WinFlow\recordings` (local, only if `WINFLOW_SAVE_RECORDINGS=1`) |

WinFlow does not include analytics or telemetry in the current release.

## Uninstalling

1. Right-click tray icon → **Exit**
2. Delete the application folder (or uninstall via Settings if you used an installer)
3. Optional cleanup:
   - `%APPDATA%\WinFlow\` — settings and recordings
   - `%LOCALAPPDATA%\WinFlow\models\` — offline model (~631 MB)
   - Windows Credential Manager → remove `WinFlow/OpenAI`

## Getting help

- [GitHub Issues](https://github.com/YOUR_USERNAME/winflow/issues) — bug reports and feature requests
- [ARCHITECTURE.md](../ARCHITECTURE.md) — technical design and roadmap

## Keyboard reference

| Action | Key |
|--------|-----|
| Start / stop recording | Hold **Right Ctrl** |
| Manual paste (if injection failed) | **Ctrl+V** |
| Open tray menu | Right-click tray icon |
