# Reference audit

Date: 2026-07-15

This audit was completed before product implementation. Both inspiration repositories were inspected read-only. Neither repository is a source dependency or writable remote for Cursivis Next.

## Audit basis

| Repository | Audited revision | Scope |
| --- | --- | --- |
| `UnknownGod2011/MX--Cursivis` | `08f294ff5643a67bd64a5064a572a1408c9ec3af` on `main` | Cursor workflows, selection, overlays, browser actions, Settings, IPC, installer, startup |
| `UnknownGod2011/keyboard.wtf` | `946a156e132a04ef4353152fd1ed2f744c3c1b91` on `main` | Voice, dictation, hotkeys, partials, interruption, cancellation, startup, allowlisted actions |

The existing local keyboard.wtf checkout had unpublished changes, so the audit used `origin/main:<path>` rather than treating local modifications as canonical.

## Executive verdict

The old products prove that a cursor-first workflow can feel useful, but their code should not be ported. Preserve behavioral ideas, not implementations.

Concepts worth retaining:

- Immediate non-activating feedback in the active workflow.
- Smart versus Guided intent semantics.
- In-memory region capture and local pixel sampling.
- Same-key voice start/finish and a universal emergency stop.
- Visible microphone level and partial transcription.
- Server VAD, streamed response audio, and barge-in.
- Acting in the user's real authenticated Chromium tab.
- DOM/accessibility targeting plus per-field verification.
- Current-user encrypted secret storage.
- Pinned release downloads, checksums, transactional staging, backup, and rollback.

Implementations that must not be reused:

- Logitech/plugin dependencies or MX-specific runtime contracts.
- Gemini, provider pools, OpenAI-compatible proxies, Ollama, or local LLM paths.
- Existing WPF/HTML Settings layouts or their code-behind.
- Clipboard-first selection with fixed sleeps.
- Unauthenticated localhost HTTP or WebSocket control endpoints.
- Model-controlled risk and confirmation flags.
- Arbitrary model-generated scripts, selectors, commands, or coordinate macros.
- Multiple hotkey owners and non-transactional replacement.
- Raw audio/transcript debug persistence.
- Full-page DOM capture under `<all_urls>`.
- Submit-as-Next form logic.
- God services, broad exception swallowing, or direct non-atomic JSON writes.

## MX-Cursivis file-level map

### Selection and foreground context

| Legacy file | Observed behavior/problem | Cursivis Next replacement |
| --- | --- | --- |
| `Services/SelectionDetector.cs` | Saves a WPF clipboard object, writes a sentinel, foregrounds the target, waits 110 ms, sends Ctrl+C, polls up to 950 ms, retries, waits again, and restores unconditionally. | `ContextCapturePipeline`: authenticated browser selection, bounded UI Automation, then sequence-aware protected clipboard fallback. |
| `Services/ClipboardService.cs` | Restoration is not protected against an independent user clipboard change. | Deep-copy a supported format allowlist and restore only when Cursivis still owns the observed sequence. |
| `Services/WindowFocusTracker.cs` | Polls foreground state on the UI thread every 45 ms. | `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)` adapter producing immutable `TargetIdentity` snapshots. |
| `Controllers/TriggerController.cs` | Can fall back to a previously seen window without proving it remains the target. | Trigger-time HWND/PID/process/bounds/timestamp fingerprint with expiry and revalidation before mutation. |
| `Native/NativeMethods.cs` | Win32 calls are mixed into orchestration. | Keep P/Invoke in `Cursivis.Windows.Platform` behind narrow interfaces. |

The legacy trigger path contains several fixed waits and can overwrite a user's newer clipboard content. It also has no fast Chromium selected-text endpoint and no UI Automation selection path.

### Smart and Guided modes

| Legacy file | Observed behavior/problem | Replacement |
| --- | --- | --- |
| `Controllers/TriggerController.cs` | Suggest and analyze flows are sequential; Guided withholds options and may auto-commit after a timer. | Deterministic options appear immediately; one structured Responses request handles intent plus result where practical. |
| `backend/gemini-agent/src/app.js` | Routing, dynamic options, and generation can cause three or more serial model operations. | One OpenAI gateway call for classification and final content, with local heuristics for obvious cases. |
| `backend/gemini-agent/src/contentClassifier.js` | Provider-specific classification is separated from final generation. | Typed `SmartResult` contract returned by the main request. |
| `backend/gemini-agent/src/geminiService.js` | Gemini-specific prompts and retries. | Concise versioned OpenAI prompt resources with strict schemas and bounded retry policy. |

Smart auto-replacement in the old controller trusts confidence without proving the original selection still exists. Cursivis Next never mutates text until the target and context fingerprint are current.

### Region capture and color

| Legacy file | Observed behavior/problem | Replacement |
| --- | --- | --- |
| `Views/LassoOverlayWindow.xaml(.cs)` | Virtual-desktop overlay; thin drags can be mistaken for clicks. | One DPI-aware overlay per monitor and click-versus-drag classification from pointer movement history. |
| `Services/LassoSelectionService.cs` | Fixed 80 ms wait after overlay close. | Capture lifecycle driven by compositor/window completion, never a fixed sleep. |
| `Services/ScreenCaptureService.cs` | Synchronous GDI capture with broad error swallowing. | Windows Graphics Capture with bounded GDI fallback, off the UI thread. |
| `Controllers/TriggerController.cs` | Pixel result is essentially a hex string. | Local `{ hex, rgb, nearestName, swatch }`; no model call. |

The useful behavior to retain is memory-only image handling. Screenshots are never persisted by default.

### Orb and result panel

| Legacy file | Observed behavior/problem | Replacement |
| --- | --- | --- |
| `Models/OrbState.cs` | Only Idle, Processing, Completed, and Listening. | One authoritative reducer with the full product state set and validated transitions. |
| `Views/OrbOverlayWindow.xaml(.cs)` | Non-activating and draggable, but primary-mode coverage and placement are incomplete. | Specialized WinUI `OrbWindow` projected from immutable state, active-monitor clamping, remembered placement, accessible labels. |
| `Views/ResultPanelWindow.xaml(.cs)` | Calls `Activate()`, hides on deactivation, rebuilds the complete document every 12-20 ms, and copies results before the user asks. | No-activate presentation by default, incremental text blocks, explicit clipboard mutation, bounded context lifetime. |

Undo in the legacy product does not retain the execution channel. Cursivis Next records channel-specific `ExecutionReceipt` evidence and uses the same channel for undo.

### Talk and Take Action

| Legacy file | Observed behavior/problem | Replacement |
| --- | --- | --- |
| `Services/VoiceCaptureService.cs` | Adaptive capture is useful, but streaming is disabled and partials are not shown. | OpenAI transcription/Realtime transport with visible partial/final aggregation. |
| `Services/VoiceCommandPromptService.cs` | Writes raw WAV and transcript JSON under `%TEMP%` by default. | No raw retention by default; explicit time-limited diagnostics mode only. |
| `Models/BrowserActionPlan.cs` | Plan shape exists but local safety is incomplete. | Versioned immutable action plan with local validation, risk derivation, fingerprint, step bounds, and expected outcomes. |
| `backend/gemini-agent/src/browserActionPlanner.js` | Boolean-casts model `requiresConfirmation`; risky behavior is inferred from words. | Central deterministic policy engine; the model cannot downgrade risk. |
| `Services/ExtensionAutomationClient.cs` | Execution channel exists but success and undo semantics drift. | `IBrowserExecutor` returns typed per-step evidence and a channel-bound undo receipt. |
| `Services/ActiveBrowserAutomationService.cs` | Enumerates large UIA trees, waits, foregrounds the browser, and duplicates form policy. | Narrow, bounded fallback only; shared action semantics across every executor. |
| `desktop/browser-action-agent/src/server.js` | Unauthenticated local Playwright service, separate profile, and zero-step success. | No resident Playwright service in the product. Browser extension is primary; Playwright is test-only. |

### Forms

The legacy fallback plan sets `requiresConfirmation:false`, and both extension and UI Automation paths can treat `Submit` as `Next`. That is a critical safety defect.

Cursivis Next rules:

1. Discover visible fields into a typed local schema.
2. Use OpenAI only for interpretation or answer generation.
3. Map answers to stable field identifiers.
4. Fill and verify each value.
5. Stop with unresolved questions visible.
6. Never include Submit/Done in pagination aliases.
7. Submission is a separate terminal step requiring a fresh explicit request and confirmation.

### Browser bridge and IPC

| Legacy file | Observed behavior/problem | Replacement |
| --- | --- | --- |
| `desktop/browser-native-host/src/host.js` | Exposes active context and plan execution on `127.0.0.1:48830`, no token/origin/nonce/size/schema protection, and `Access-Control-Allow-Origin:*`. | MV3 Native Messaging host allowlisted to the extension ID; same-user authenticated named pipe to Cursivis. |
| `desktop/browser-extension-chromium/background.js` | Prefers insecure HTTP over Native Messaging. | Native Messaging only; version/capability handshake and explicit disconnection state. |
| `desktop/browser-extension-chromium/content.js` | `<all_urls>`, all frames, up to 10,000 body characters, serial frame queries, no dedicated selected-text contract. | Optional per-site permissions, on-demand injection, selected text plus bounded nearby semantic context. |
| `Services/TriggerIpcServer.cs` | Unauthenticated WebSocket accepts actionable default DTOs and no replay protection. | Same-user ACL named pipe, versioned DTOs, limits, correlation IDs, expiry, replay rejection, acknowledgements. |

### Hotkeys, startup, installer, and contracts

| Legacy file | Observed behavior/problem | Replacement |
| --- | --- | --- |
| `desktop/cursivis-hotkey-host/.../Program.cs` and companion handlers | Duplicate hotkey owners and inconsistent semantics. | Exactly one UI-thread hidden-window `HotkeyCoordinator`. |
| `Services/HotkeyHostService.cs` | Failed configured shortcut can silently fall back. | Transactional validate/register/persist/unregister; keep the previous active binding on failure. |
| `Services/StartupRegistrationService.cs` | Startup ownership is split across components. | One per-user startup setting owned by the installed host. |
| `desktop/cursivis-setup/src/Cursivis.Setup/SetupForm.cs` | Strong pinned URL/hash/staging/rollback behavior. | Preserve the transaction design in a single installer with ARP registration, repair, uninstall, and user-data choice. |
| `scripts/install-cursivis-runtime.ps1` | Diverges from setup, downloads components without full integrity guarantees, and has weaker rollback. | One deterministic installer path; no production npm install or mutable runtime downloads. |
| `shared/ipc-protocol/schema/trigger-event.schema.json` | Stale sources/press types and nonnegative coordinates break multi-monitor semantics. | One canonical schema set generating C# and TypeScript types. |

## keyboard.wtf file-level map

### Voice and dictation

| Legacy file | Observed behavior/problem | Replacement |
| --- | --- | --- |
| `src/Services/AudioRecorderService.cs` | Immediate shared capture and levels are useful; device removal and recovery are weak. | `IAudioSessionCoordinator` with stable endpoint IDs, one capture lease, and explicit device lifecycle. |
| `src/Services/VoiceCaptureService.cs` | Same-key toggle, pause detection, partials, and cancellation are good UX; service combines too many responsibilities. | Separate capture, VAD, transcript aggregator, cleanup, insertion, and undo use cases. |
| `src/Services/VoskRecognitionService.cs` | Synchronous partial recognition on the NAudio callback and repeated string rebuilding. | Bounded audio channel feeding OpenAI streaming transcription off the capture callback. |
| `src/Services/WhisperRecognitionService.cs` | Reprocesses the complete WAV and may consume all processors. | Commit streamed transcript; bounded Audio Transcriptions fallback only. |
| `src/Services/CommandRegistry.cs` | Strong filler/correction cleanup instructions, but orchestration and provider work are mixed and raw transcripts are logged. | Versioned prompt resource, no content logging, `SmartDictationUseCase`. |
| `src/Destinations/TypeOutDestination.cs` | Overwrites clipboard, waits 200 ms, sends paste, and reports success without verification. | UIA insertion first; protected clipboard fallback, target revalidation, verification, undo receipt. |

### Realtime and interruption

| Legacy file | Observed behavior/problem | Replacement |
| --- | --- | --- |
| `src/Services/GeminiLiveConversationService.cs` | Demonstrates persistent duplex audio, server VAD, playback clear, and tool calls, but is an approximately 1,100-line god service. | Separate Realtime transport, supervisor, audio sender, capture/playback, transcript aggregator, tool dispatcher, cancellation registry, and UI projection. |
| `src/Models/KeyboardWtfState.cs` | Phase enum coexists with static booleans and mutable globals. | Authoritative interaction reducer with operation generation IDs. |
| `src/VoiceOverlayForm.cs` | Useful non-activating labels, colors, partial text, and meter; fixed banner polls every 80 ms on the primary screen. | Event-driven orb window on the active monitor with controls and accessible state text. |

Specific lifecycle hazards to eliminate:

- Unbounded `async void` audio sends and no backpressure.
- Mutable socket/session fields used by late callbacks.
- Racy repeated start/stop.
- `CloseAsync(..., CancellationToken.None)` and receive tasks not awaited.
- Tool execution awaited inside the only receive loop.
- Tool-cancel messages that only log rather than cancel.
- API key in a WebSocket query string.
- Independent microphone objects for dictation, Live, and Settings tests.

### Hotkeys and allowlisted actions

| Legacy file | Observed behavior/problem | Replacement |
| --- | --- | --- |
| `src/Services/HotkeyService.cs` | Native registration and active/configured reporting are useful; unregister-all replacement has no rollback. | Typed chord plus temporary registration ID, atomic persistence, rollback, `MOD_NOREPEAT`. |
| `src/Services/WebSettingsService.cs` | Persists before registration and reconfigures from a thread-pool handler. | Hotkey broker owns thread affinity; Settings calls an application use case and stages changes. |
| `src/Resources/web/settings.html` | Plain text shortcut inputs with immediate save. | Reusable WinUI hotkey recorder with conflict and alternative states. |
| `src/Services/JarvisPermissionPolicy.cs` | Centralization is valuable but binary Ask/AutoExecute is too coarse. | Central no/conditional/always-confirm policy derived from typed actions. |
| `src/Services/BrowserLauncherService.cs` | Strong HTTP(S) validation and no silent browser substitution. | Preserve as narrow deterministic URL policy. |
| `src/Services/AppResolverService.cs` | Trusted locations and ambiguity handling are strong. | Preserve conceptually for the small explicit capability set only. |

The new default hotkeys intentionally avoid keyboard.wtf's common `Ctrl+Alt+K`, `Ctrl+Alt+D`, `Ctrl+Alt+Q`, and `Ctrl+Alt+X` chords.

## Dedicated critique of the old Settings UI

The MX-Cursivis Settings window is a 456x700 topmost window with one large vertical scroller and approximately 2,700 lines of code-behind. It mixes Logitech status, triggers, demo controls, hotkeys, Gemini/OpenAI-compatible/Ollama/provider configuration, tokens, model downloads, raw runtime health, voice, browser behavior, and permissions. Placeholder controls and internal ports/provider terms are exposed directly.

It is not a design reference because it has:

- no persistent navigation or task-oriented hierarchy;
- no useful Overview, Privacy, Diagnostics, About, or Browser Integration page;
- immediate async saves with weak failure recovery;
- no staged/saved/tested distinction;
- fragmented `settings.json`, `runtime-profile.json`, `live-mode.json`, and hotkey files;
- non-atomic writes and no explicit migration model;
- no safe corrupt-settings recovery;
- runtime actions mixed with ordinary preferences;
- provider and implementation detail that ordinary users should never need to understand.

keyboard.wtf's web Settings repeats several problems: plain-text hotkeys, immediate saves, direct service manipulation, and separate microphone ownership.

## Replacement Settings information architecture

The rebuilt WinUI Settings app uses exactly these sections:

1. Overview
2. OpenAI
3. Interaction
4. Custom Quick Task
5. Hotkeys
6. Voice
7. Browser Integration
8. Privacy & Safety
9. Diagnostics
10. About

Each section has its own view model and reusable validated rows. Sensitive/compound changes are staged with explicit Save and Discard. The UI independently represents configured, saved, active, tested, unavailable, conflicted, and unsaved states. Onboarding reuses the same application services and controls. The view layer never manipulates OpenAI clients, browser transports, hotkeys, audio devices, or files directly.

## Audit-driven architectural rules

1. Trigger -> immutable context -> local/OpenAI intent -> result or typed plan -> local policy -> confirmation -> deterministic executor -> verified receipt -> UI/undo.
2. Browser extension first, UI Automation second, protected clipboard third.
3. One hotkey owner, one audio capture coordinator, one authoritative interaction state machine.
4. Native Messaging plus authenticated same-user named pipe; no unauthenticated localhost service.
5. The model proposes; deterministic code validates, classifies risk, executes, and verifies.
6. Submission, sending, purchases, deletion, account changes, permissions, legal acceptance, and secret entry are isolated fresh-confirmation actions.
7. Screenshots, selected text, audio, prompts, form contents, and Quick Task input are not logged by default.
8. Settings and Quick Task configuration are versioned and atomically persisted; secrets are separate and encrypted.
