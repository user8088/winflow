## WinFlow 1.1.1

Recommended release. Fixes dictation in Slack, browsers, and Electron apps, plus keyboard and performance issues from earlier builds.

### Downloads

| Installer | Size | Description |
|-----------|------|-------------|
| **WinFlow-Setup-1.1.1.exe** | ~74 MB | **Recommended.** Models download on first use (~1.1 GB, resumable). |
| **WinFlow-Setup-Full-1.1.1.exe** | ~992 MB | Everything bundled — fully offline right after install. |

Both are per-user installs (no admin). You choose the install location.

> **SmartScreen:** The installer is unsigned. Click **More info → Run anyway** if Windows warns you.

### What's fixed in 1.1.1

- **Lone "v" in Slack/browsers** — Auto mode now types Unicode text in Electron apps and browsers instead of broken Ctrl+V paste
- **Keyboard interference** — No stuck Ctrl or random shortcuts after recording
- **Cursor/terminal dictation** — Reliable keystroke injection with thread attachment
- **First dictation delay** — Background warmup pre-loads mic, models, and cloud connection
- **Long paragraphs** — Faster transcription and chunked correction

### Known issues in older builds

| Version | Status |
|---------|--------|
| **1.0.0** | Known issues: slow first dictation, keyboard can get stuck after recording, Ctrl+V paste types only "v" in Slack/browsers |
| **1.1.0** | Tag only — release was never fully published; same issues as above except performance warmup |

---

## Installation & setup

See the [README setup demo](https://github.com/user8088/winflow#demo) or screenshots below.

### 1. Tray menu

![WinFlow tray menu](https://github.com/user8088/winflow/raw/v1.1.1/docs/images/tray.png)

### 2. OpenAI API key (cloud mode)

![Set OpenAI API key](https://github.com/user8088/winflow/raw/v1.1.1/docs/images/open_ai.png)

### 3. Offline speech model

![Offline speech model](https://github.com/user8088/winflow/raw/v1.1.1/docs/images/Offline_model.png)

### 4. Transcription mode

![Transcription mode](https://github.com/user8088/winflow/raw/v1.1.1/docs/images/transcription_mode.png)

### 5. Input method — use **Auto** for Slack & browsers

![Input method](https://github.com/user8088/winflow/raw/v1.1.1/docs/images/Input_Method.png)

### 6. Dictate

Focus any text field, hold **Right Ctrl**, speak, release.

---

## Model licenses

- On-device speech: NVIDIA Parakeet TDT 0.6B v2 (CC-BY-4.0) via sherpa-onnx
- Offline correction: Qwen2.5-0.5B-Instruct (Apache 2.0)
