# WinFlow — Architecture & Implementation Plan

A Windows-native voice dictation app inspired by [freeflow](https://github.com/mrinalwadhwa/freeflow):
hold a hotkey, speak naturally (rambles, filler words, self-corrections and all), release, and
polished text appears at your cursor in **any** application — editor, terminal, browser, chat, email.

Goal: match freeflow's ~0.55s median key-release→text latency, and beat it on
local/offline capability, GPU-vendor coverage, and per-app customization.

---

## 1. What freeflow actually does (learnings from its source)

Reading the freeflow source (Swift, Apache-2.0) reveals the design decisions that make it good.
These are the things we must replicate, not just the feature list:

| Mechanism | Why it matters |
|---|---|
| **Streaming STT over a persistent WebSocket** (OpenAI Realtime API, pcm16 @ 24kHz) | Transcription happens *while you speak*; on key release only the tail is left to process |
| **Warm backup connection pool** | 91% of dictations skip the WebSocket/TLS handshake entirely — a second connection is always pre-opened |
| **Parallel batch fallback race** | Every dictation *also* records a full WAV; if streaming stalls or fails, a batch transcription request races it. User never sees a failure |
| **Polish heuristic gate** | ~83% of transcripts are already clean; a local heuristic detects this and skips the LLM entirely. Only messy transcripts hit `gpt-4o-nano` (320–780ms) |
| **App context captured in parallel** | While recording, it reads the focused app/field (and browser URL) so the polish prompt knows if you're in code, email, or chat |
| **Per-app injection strategies** | Three strategies (accessibility-set-value, clipboard+paste with restore, per-key synthesis), selected per app ID |
| **Adaptive silence gate** | Ambient RMS is measured during recording; threshold = `clamp(ambient × 1.2, 0.0005, 0.01)`, with a separate far-field path for built-in mics. Rejects accidental presses without eating whispers |
| **Transcript buffer + re-paste** | If injection fails (no focused field), text is kept and a shortcut re-pastes it |
| **Protocol-oriented core + thin app shell** | `FreeFlowKit` (all logic, fully mocked/tested) vs `FreeFlowApp` (menu bar, HUD, settings). ~90 test files against protocol mocks |
| **Local mode** (Parakeet STT + MLX LLM polish) | Fully offline path — but Apple-Silicon-only. **This is our biggest opportunity to do better.** |

---

## 2. Stack decision

### Chosen: **C# / .NET 10 (LTS)** — `WinFlow.Core` class library + `WinFlow.App` WPF shell

| Concern | Choice | Rationale |
|---|---|---|
| Language/runtime | **C# / .NET 10** | First-class Win32 interop (P/Invoke for hooks, SendInput, clipboard), mature async for the streaming pipeline, xUnit for the mock-driven test strategy freeflow proves out |
| UI framework | **WPF** | Best fit for a tray-resident app with a borderless, click-through, always-on-top HUD overlay. WinUI 3 still fights you on tray-only apps and non-activating overlay windows |
| Settings/Onboarding UI | **WebView2** (HTML/CSS/JS) | Mirrors freeflow's `settings.html` / `onboarding.html` approach — fast to iterate, easy to make beautiful |
| Audio capture | **NAudio** (WASAPI event-driven, shared mode) | Battle-tested; device enumeration + hot-swap notifications via `MMDeviceEnumerator` |
| Cloud STT | **OpenAI Realtime API** (WebSocket, `ClientWebSocket`) | Same protocol freeflow uses; the entire latency architecture ports 1:1 |
| Cloud polish | **OpenAI Chat Completions** (`gpt-4o-nano` class model) | Same as freeflow |
| Local STT | **sherpa-onnx** running **NVIDIA Parakeet TDT 0.6B v2 (ONNX, int8)** | Same model family freeflow uses locally, but ONNX Runtime runs on *any* CPU well (real-time × ~10 on modern CPUs), with optional DirectML for any GPU vendor. Beats freeflow's Apple-Silicon-only story |
| Local polish | **LLamaSharp** (llama.cpp) + **Qwen3-0.6B/1.7B GGUF** | Optional; heuristic gate means most dictations don't need it. Vulkan/CUDA backends for GPU, CPU fallback |
| VAD (nice-to-have) | **Silero VAD** (ONNX, ~2MB) | Auto-stop in toggle mode; smarter silence rejection |
| Secrets | **Windows Credential Manager** (`CredWrite`/`CredRead`) | Direct Keychain equivalent |
| Auto-update | **Velopack** | The modern Squirrel successor; delta updates, GitHub Releases backend — Sparkle equivalent |
| Distribution | GitHub Releases + **winget** manifest | Homebrew-cask equivalent |
| Tests | **xUnit + NSubstitute** | Port freeflow's mock-per-interface test approach |

### Alternatives considered

- **Rust + Tauri** — great runtime footprint, but the HUD overlay, low-level keyboard hook,
  UI Automation, and WASAPI stories all require the same Win32 work with less ergonomic
  interop, and the ecosystem for UIA text injection is thin. Slower to ship, no capability gain.
- **Electron** — global hold-to-talk hooks, text injection, and a click-through overlay all
  require native modules anyway; 150MB+ baseline and worse latency characteristics. Rejected.
- **C++/Qt** — maximum control, slowest iteration, worst testing ergonomics. Rejected.

---

## 3. macOS → Windows subsystem map

| Subsystem | freeflow (macOS) | WinFlow (Windows) |
|---|---|---|
| Global hold-to-talk hotkey | `CGEventTap` | `SetWindowsHookEx(WH_KEYBOARD_LL)` on a dedicated thread with message pump; key-down starts, key-up commits; event swallowed so it doesn't type |
| Audio capture | `AVAudioEngine` + CoreAudio | WASAPI shared-mode event-driven capture (NAudio), resampled to mono pcm16 @ 24kHz |
| Device list & hot-swap | `CoreAudioDeviceProvider` | `MMDeviceEnumerator` + `IMMNotificationClient` (default-device change, unplug mid-recording) |
| Text injection | `AXUIElement` set-value / pasteboard+⌘V / CGEvent keystrokes | UIA `ValuePattern`/`TextPattern` / clipboard+Ctrl-V (save & restore) / `SendInput` with `KEYEVENTF_UNICODE` |
| App context for polish | `AXAppContextProvider`, `BrowserURLReader` | `GetForegroundWindow` + process exe + UIA focused element (control type, name); browser URL via UIA on the address bar |
| API key storage | Keychain | Credential Manager (DPAPI-backed) |
| HUD overlay | `NSPanel` overlay window | WPF borderless window: `WS_EX_LAYERED \| WS_EX_TRANSPARENT \| WS_EX_NOACTIVATE \| WS_EX_TOOLWINDOW`, topmost, per-monitor-DPI aware |
| Menu bar item | `NSStatusItem` | Tray icon (H.NotifyIcon), flyout menu |
| Onboarding/Settings | HTML in WKWebView + JS bridge | HTML in WebView2 + `window.chrome.webview` bridge |
| Permissions | Mic + Accessibility prompts | Mic privacy consent only (`Windows.Media.Capture` probe / graceful WASAPI failure). **No accessibility permission needed on Windows — one less onboarding hurdle** |
| Sounds | `SoundFeedbackProvider` | Same concept; short WAV cues via NAudio |
| Updater | Sparkle | Velopack |

---

## 4. System architecture

```
┌────────────────────────────────────────────────────────────────────────┐
│ WinFlow.App (WPF shell — thin)                                         │
│  TrayIconController · HudWindow (overlay + waveform) · SettingsWindow  │
│  OnboardingWindow (WebView2) · UpdaterService (Velopack) · DI wiring   │
└───────────────────────────────┬────────────────────────────────────────┘
                                │ interfaces only
┌───────────────────────────────▼────────────────────────────────────────┐
│ WinFlow.Core (class library — all logic, fully testable)               │
│                                                                        │
│  IHotkeyProvider ──► RecordingCoordinator (state machine)              │
│                          │   idle → recording → processing →           │
│                          │   injecting → idle | injectionFailed        │
│                          ▼                                             │
│                     DictationPipeline (orchestrator)                   │
│      ┌──────────────┬───────┴────────┬──────────────────┐              │
│      ▼              ▼                ▼                  ▼              │
│  IAudioProvider  IAppContext     IStreamingSTT      ISttProvider       │
│  (WASAPI 24k     Provider        (Realtime WS +     (batch WAV         │
│   pcm16 ring     (UIA, exe,       warm backup        fallback race)    │
│   buffer + RMS)   browser URL)    connection)                          │
│                                      │                                 │
│                                      ▼                                 │
│                               PolishPipeline                           │
│                     heuristic gate ──► IPolishClient                   │
│                     (skip ~83%)        (OpenAI | local LLM | none)     │
│                                      │                                 │
│                                      ▼                                 │
│                                ITextInjector                           │
│                    strategy per app: uia | clipboard | sendinput       │
│                                      │                                 │
│                                      ▼                                 │
│                    TranscriptBuffer (history + re-paste on failure)    │
│                                                                        │
│  Local engines: SherpaOnnxSttEngine (Parakeet TDT int8)                │
│                 LlamaPolishEngine (Qwen3 GGUF) · LocalModelManager     │
│  Infra: CredentialStore · SettingsStore (JSON, %APPDATA%)              │
│         SoundFeedback · Log (ETW/file) · MicDiagnostics                │
└────────────────────────────────────────────────────────────────────────┘
```

### The dictation flow (hot path)

1. **Key-down** (LL hook, <1ms callback — just posts to a channel):
   start WASAPI capture, open/reuse warm Realtime session, kick off app-context read
   in parallel, show HUD in `recording` state.
2. **While held**: audio ring buffer → resample → stream pcm16 chunks over the WebSocket;
   RMS levels drive the HUD waveform and feed the adaptive silence gate; full audio also
   accumulates in a WAV buffer for the fallback race.
3. **Key-up**: silence gate check (adaptive threshold from ambient RMS — port freeflow's
   exact constants: `ambient × 1.2`, floor `0.0005`, ceiling `0.01`, far-field path `0.001`).
   Commit the streaming session; simultaneously arm the batch fallback with a deadline.
   Whichever transcript arrives first (and is well-formed) wins.
4. **Polish**: heuristic gate (length, filler-word density, self-correction markers,
   target-app category). Clean → skip. Messy → polish LLM with app-context-aware prompt.
5. **Inject** via the per-app strategy; on failure, keep transcript in buffer, HUD shows
   re-paste hint. Open the next warm backup connection in the background.

### Core interfaces (mirroring FreeFlowKit's protocols)

`IHotkeyProvider`, `IAudioProvider`, `IAudioDeviceProvider`, `IStreamingDictationProvider`,
`IDictationProvider`, `IPolishChatClient`, `ITextInjector`, `IAppContextProvider`,
`IPermissionProvider`, `IPipelineProvider` — each with a mock in `WinFlow.Core.Tests`.
This is what made freeflow testable to ~90 test files; we keep it.

---

## 5. Windows-specific engineering challenges (and answers)

1. **LL keyboard hook timeouts.** Windows silently removes hooks whose callback exceeds
   `LowLevelHooksTimeout` (~300ms default). The hook callback must only enqueue and return;
   all work happens on the pipeline thread. Also: register a watchdog that re-installs the
   hook if it's dropped, and prefer `Raw Input` as a diagnostic cross-check.
2. **Hotkey choice & suppression.** Default: **hold Right-Ctrl** (push-to-talk) with
   tap-to-toggle as an alternate mode; fully rebindable (including F13–F24 and mouse
   side-buttons later). Swallow the key in the hook so held modifiers don't leak into the
   target app; on key-up, verify modifier state is clean before injecting (send a
   synthetic key-up if needed — classic SendInput+modifier race).
3. **Clipboard paste without clobbering.** Save clipboard (all formats we can round-trip,
   or at minimum `CF_UNICODETEXT` + sequence number), set text, `SendInput` Ctrl+V, wait for
   the clipboard sequence number / a short delay, restore. Handle `OpenClipboard` contention
   with bounded retries. Mark our clipboard data with a custom format so clipboard-history
   tools can be told to ignore it.
4. **Injection targets that fight back.**
   - *Terminals* (Windows Terminal, ConHost, VS Code terminal): clipboard-paste strategy.
   - *Elevated apps*: UIPI blocks injection from a non-elevated process. Detect
     (`GetForegroundWindow` → process elevation check) and show "target is elevated" HUD
     hint with transcript kept in buffer. (Optional later: UIAccess signing.)
   - *Secure desktop / UAC prompts / lock screen*: detect and refuse gracefully.
   - *Electron/browser fields*: UIA patterns are unreliable → clipboard strategy default.
   - Per-app strategy table keyed by process name, user-overridable in settings.
5. **Audio robustness** (freeflow's #1 reported pain point — we can do better):
   device hot-swap mid-recording (reopen on new default, splice buffers), exclusive-mode
   denial, Bluetooth headsets switching to HFP profile (detect 8/16kHz capture and warn),
   software gain for quiet mics, mic-diagnostic store like freeflow's for support.
6. **Latency budget** (target ≤0.6s median key-up→injected):
   warm WS pool (open 2nd connection after each dictation) · capture pre-roll ~150ms ring
   buffer so the first syllable is never clipped · stream during hold so key-up only flushes
   the tail · polish skip via heuristic · injection is O(1) for clipboard strategy.
   Instrument every stage (ETW events + local histogram like freeflow's BENCHMARK.md).
7. **Unsigned-binary friction.** SmartScreen will warn on downloads until reputation builds.
   Ship via winget (bypasses most of it) and budget for an Azure Trusted Signing cert.

---

## 6. Where WinFlow beats freeflow

1. **Local-first on any hardware.** Parakeet TDT via ONNX Runtime int8 runs faster than
   real-time on ordinary CPUs; optional DirectML/Vulkan uses *any* GPU (NVIDIA/AMD/Intel).
   freeflow's local mode requires Apple Silicon. Fully offline dictation is a first-class
   mode, not an afterthought — and it's free (no API key needed to start).
2. **Zero-permission onboarding.** Windows needs no accessibility grant — install, add key
   (or pick local mode), dictate. Freeflow needs two system permission prompts.
3. **Per-app profiles.** Polish style, language, vocabulary, and injection strategy per
   application (casual in Slack, no polish + verbatim in terminals, formal in Outlook).
4. **Custom vocabulary / replacements.** User dictionary (names, jargon, snail-case →
   `snake_case`) applied post-transcription, plus prompt-injected hint terms.
5. **Dictation history.** Searchable local history window (opt-in, local only) with
   one-click re-copy — recovers freeflow's "injection failed" case more gracefully.
6. **Streaming partial injection option.** For long dictations, optionally inject
   confirmed segments as you speak instead of waiting for key-up.
7. **Provider abstraction from day one.** OpenAI Realtime is the default, but `ISttProvider`
   keeps the door open for Deepgram/Groq/Azure without architectural change.

---

## 7. Milestones

**M0 — Skeleton (prove the risky parts)**
Solution scaffold (`WinFlow.Core`, `WinFlow.App`, `WinFlow.Core.Tests`) · tray icon ·
LL keyboard hook with hold-to-talk state machine · WASAPI capture → 24kHz pcm16 →
WAV dump · RMS meter. *Exit: hold key, speak, release → valid WAV on disk, no dropped hooks.*

**M1 — First real dictation (cloud hot path)**
OpenAI Realtime streaming provider + warm backup connection · batch fallback race ·
clipboard injection with save/restore · minimal HUD overlay (recording/processing states) ·
Credential Manager key storage. *Exit: median key-up→text < 1s in Notepad, VS Code, browser, terminal.*

**M2 — Polish & robustness**
PolishPipeline with heuristic gate + context-aware prompts (port freeflow's prompt set) ·
UIA app-context provider · per-app injection strategy table + UIA/SendInput strategies ·
adaptive silence gate · transcript buffer + re-paste shortcut · settings UI (WebView2) ·
sound feedback · elevated-target detection. *Exit: freeflow feature parity minus local mode.*

**M3 — Local mode (the differentiator)**
LocalModelManager (download/verify models to `%LOCALAPPDATA%`) · sherpa-onnx Parakeet STT
(streaming + batch) · optional LLamaSharp polish · mode switcher (cloud/local/auto) ·
Silero VAD auto-stop for toggle mode. *Exit: fully offline dictation, ≤1.5s median on CPU.*

**M4 — Ship**
Onboarding flow · Velopack auto-update · installer + winget manifest · custom vocabulary ·
per-app profiles · history window · latency benchmark harness (BENCHMARK.md equivalent) ·
docs. *Exit: public release.*

---

## 8. Testing strategy

- **Unit**: every `I*Provider` mocked (port freeflow's `Mock*` suite); state-machine tests
  for RecordingCoordinator; heuristic-gate scenario table (port `PolishScenarioData`).
- **Integration**: pipeline runs against mock STT with scripted latency/failure injection
  (streaming stall → fallback race wins; 401 → session recovery; silence → gate rejects).
- **Injection matrix**: scripted smoke test over Notepad, WordPad, VS Code, Windows
  Terminal, Chrome, Edge, Slack — run manually per release via a checklist harness.
- **Latency**: instrumented timestamps at hook→capture-start, key-up→transcript,
  transcript→injected; percentile report per run.

## 9. Repo layout

```
winflow/
├── src/
│   ├── WinFlow.App/            # WPF shell: tray, HUD, settings host, DI, updater
│   ├── WinFlow.Core/           # all pipeline logic, interfaces, engines, services
│   └── WinFlow.Core.Tests/     # xUnit + NSubstitute
├── assets/                     # icons, sounds, settings.html, onboarding.html
├── models/.gitignore           # local models live in %LOCALAPPDATA%, never in repo
├── docs/                       # BENCHMARK.md, INJECTION-MATRIX.md
├── scripts/                    # release, winget manifest gen
└── ARCHITECTURE.md             # this file
```

License: Apache-2.0 (same as freeflow; we're a from-scratch Windows sibling, credit it in the README).
