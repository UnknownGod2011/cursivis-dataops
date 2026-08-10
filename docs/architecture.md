# Architecture

## Decision summary

| Decision | Choice | Reason |
| --- | --- | --- |
| Windows UI | C# WinUI 3 | Native Windows overlays, Fluent controls, accessibility, multi-window support |
| Target | .NET 8, `net8.0-windows10.0.26100.0`, minimum OS API `10.0.19041.0` | Installed supported LTS toolchain; launch verified on Windows build 26200 |
| Windows App SDK | Pinned stable `1.8.260317003` | The default 2.2 alpha-template path built but crashed at runtime |
| Deployment | Unpackaged, self-contained, x64 | Direct executable verification, global hotkeys/tray/startup/native-host integration, no machine-global Windows App SDK runtime dependency |
| Installer | Per-user installer with transactional staging and uninstall | Avoid elevation where practical; support startup and native-host registration |
| Browser transport | MV3 Native Messaging -> native host -> same-user named pipe | No unauthenticated fixed localhost port; extension ID allowlist and typed handshake |
| OpenAI integration | Official .NET SDK behind interfaces, plus protocol-level contract tests | Responses, Realtime, and transcription without leaking provider types into domain |
| State | One reducer/state machine with operation generation IDs | Prevent stale async work and contradictory booleans |
| Schemas | Canonical versioned JSON Schemas generating C# and TypeScript | Prevent browser/action/IPC contract drift |

## Toolchain evidence

A clean Microsoft WinUI template probe was built and launched before selecting the product baseline.

- Default latest template/runtime: build succeeded; direct launch failed with `REGDB_E_CLASSNOTREG` for Windows App Runtime deployment.
- Latest 2.2 self-contained path: build succeeded; startup faulted in `Microsoft.UI.Xaml.dll`.
- Pinned Windows App SDK 1.8 self-contained unpackaged path with template title bar simplified to the stable native window title bar: build succeeded with zero warnings/errors and a responsive top-level window titled `CursivisWinUIStableProbe` was observed.

The product scaffold stays template-first, applies the same minimal stable-title-bar correction, then layers architecture incrementally with build-and-launch checks.

## Dependency direction

```text
Cursivis.Domain
    ^
Cursivis.Application
    ^
+---+----------------+------------------+-------------------+
|                    |                  |                   |
OpenAI adapter   Storage adapter   Browser adapter   Windows adapter
                         ^                  ^                   ^
                         +------------------+-------------------+
                                            |
                                      WinUI composition
```

Domain and Application never depend on WinUI, Win32, browser APIs, storage implementations, or OpenAI SDK types.

## Repository shape

```text
Cursivis.sln
Directory.Build.props
Directory.Packages.props
apps/
  windows/
    Cursivis.Windows.App/
    Cursivis.Windows.Platform/
    Cursivis.Windows.NativeHost/
    Cursivis.Windows.Installer/
  cursivis-mac/
src/
  Cursivis.Domain/
  Cursivis.Application/
  Cursivis.Contracts/
  Cursivis.Infrastructure.OpenAI/
  Cursivis.Infrastructure.Storage/
  Cursivis.Infrastructure.BrowserBridge/
extensions/
  chromium/
schemas/
tests/
  Cursivis.UnitTests/
  Cursivis.IntegrationTests/
  Cursivis.Windows.E2E/
  browser-extension-tests/
docs/
scripts/
samples/
```

## Core domain contracts

- `InteractionState`, `InteractionSnapshot`, `InteractionEvent`, `OperationId`.
- `TargetIdentity`, `ContextSnapshot`, `ContextFingerprint`, `ContextKind`.
- `SmartResult`, `GuidedOperation`, `CursivisResult`, `SuggestedAction`.
- `QuickTaskDefinition`, `QuickTaskContextType`, `QuickTaskOutputMode`, execution request/result.
- `ActionPlan`, `ActionStep`, `ActionTarget`, `RiskLevel`, `PolicyDecision`, `ExecutionReceipt`, `UndoReceipt`.
- `ApplicationSettings`, section settings, change set, validation issue, migration result.
- `ModelDescriptor`, `ModelCapabilities`, `QualityProfile`, `ModelAvailability`.
- Browser handshake, context, form, command, step result, and health contracts.

All persisted/transported contracts carry a schema version. Unknown required fields, unknown action types, and unsupported target strategies fail closed.

## Application use cases

- `CaptureContext`
- `RunSmartMode`
- `RunGuidedOperation`
- `AnalyzeImage`
- `FinalizeQuickTaskConfiguration`
- `ApproveAndSaveQuickTask`
- `RunQuickTask`
- `ChangeHotkey`
- `Load/Validate/Save/ResetSettings`
- `TestOpenAiConfiguration`
- `TestBrowserIntegration`
- `TestMicrophoneConfiguration`
- `ProposeAction`
- `ConfirmAndExecuteAction`
- `RunSmartDictation`
- `Start/StopRealtimeSession`
- `AnalyzeScreenOnce`
- `Start/Advance/StopNavigationGuidance`

The hotkey handler dispatches commands only. It contains no saved prompt, OpenAI request, persistence, or UI logic.

## Runtime composition

One resident WinUI process owns:

- single-instance coordination;
- message-only window and global hotkeys;
- interaction reducer;
- operation cancellation registry;
- tray integration;
- specialized Settings, orb, result, snip, Quick Task input, Live, confirmation, and navigation windows;
- named-pipe endpoint for the native browser host;
- health aggregation and structured logging.

Potentially hung UI Automation calls execute on a dedicated STA worker with strict deadlines and a circuit breaker. The interface allows moving that worker into a restartable broker process if real providers prove non-cancellable.

One `IAudioSessionCoordinator` owns a stable device endpoint and grants exclusive capture leases to Settings tests, Dictation, or Live Mode. Capture callbacks enqueue into bounded channels and never perform network or transcription work directly.

## Interaction flow

```text
Hotkey/orb
  -> render immediate state
  -> capture immutable TargetIdentity
  -> ContextCapturePipeline
       1. browser selection
       2. UI Automation
       3. protected clipboard
       4. region capture when no text
  -> mode use case
  -> local deterministic result or OpenAI gateway
  -> schema validation
  -> result projection
  -> optional ActionPlan
       -> local validation and policy
       -> preview / confirmation
       -> deterministic executor
       -> postcondition verification
       -> ExecutionReceipt / UndoReceipt
```

Every async event carries an `OperationId`. The reducer ignores completion from an older operation generation.

## Context capture

### Browser fast path

Native host requests selected text from the active tab, along with tab ID, URL origin, navigation generation, focused element summary, and bounded nearby semantic context when requested.

### UI Automation

Read selection/text patterns from the trigger-time focused element on a dedicated STA worker. Apply an explicit deadline; never block the UI thread or silently extend it with sleeps.

### Protected clipboard

1. Read clipboard sequence number and deep-copy a bounded format allowlist.
2. Record target identity and install clipboard-change observation.
3. Send copy to the exact target.
4. Wait for a real sequence change with a short adaptive deadline.
5. Read text.
6. Restore only if the target and sequence still prove Cursivis owns the temporary clipboard state.

Unsupported delayed-rendered formats are documented; Cursivis never overwrites an independent user change to provide an illusion of perfect restoration.

## OpenAI infrastructure

Model registry defaults:

| Profile | API ID | Capabilities |
| --- | --- | --- |
| Economy | `gpt-5.6-luna` | text, vision input, structured outputs, functions |
| Balanced | `gpt-5.6-terra` | text, vision input, structured outputs, functions |
| Best Quality | `gpt-5.6-sol` | text, vision input, structured outputs, functions |
| Live | `gpt-realtime-2.1` | text/audio/image input, audio output, functions; no Structured Outputs |
| Economy Live | `gpt-realtime-2.1-mini` | account-gated; same validation rules |
| Streaming transcription | `gpt-realtime-whisper` | partial/final speech-to-text |
| Bounded transcription fallback | `gpt-4o-mini-transcribe` / `gpt-4o-transcribe` | uploaded bounded recordings |

The registry separates display name, exact ID, capability flags, quality/cost tier, allowed fallbacks, and last verified availability. No silent upgrade to a materially more expensive model.

Gateways:

- `IResponsesGateway`: Smart, Guided, image, Prompt Optimizer, Quick Task finalization/execution, complex action planning.
- `IRealtimeGateway`: duplex Live Mode sessions and narrow tool calls.
- `ITranscriptionGateway`: streaming and bounded dictation transcription.

Realtime function arguments are treated as untrusted JSON. Local schemas validate them; complex action planning is handed to `IResponsesGateway` for strict output.

The official current catalog confirms the requested model IDs: https://developers.openai.com/api/docs/models. Realtime capabilities: https://developers.openai.com/api/docs/models/gpt-realtime-2.1. Official .NET SDK: https://github.com/openai/openai-dotnet.

## Browser architecture

```text
Content script (active tab, optional site permission)
  <-> MV3 service worker
  <-> Chromium Native Messaging
  <-> Cursivis.Windows.NativeHost
  <-> same-user ACL named pipe + session token
  <-> Cursivis resident host
```

Handshake includes protocol version, extension version, allowed extension ID, nonce, expiry, process/user identity, and capability list. Messages have strict schemas, size limits, deadlines, correlation IDs, and replay rejection. The native host has no OpenAI key and no general filesystem or command capability.

## Persistence and secrets

- `settings.json`: versioned non-secret configuration, atomic temp-write/flush/replace/backup.
- `quick-task.json`: independently versioned task definition, same atomic strategy.
- `memory.json`: opt-in bounded preference records only.
- OpenAI key: current-user DPAPI-protected payload or Credential Manager entry referenced by opaque ID.
- Logs/diagnostics: structured metadata with central redaction; no raw user context by default.

Corrupt files are quarantined, valid fields are salvaged where safe, and section-specific recovery avoids resetting unrelated settings. A corrupt Quick Task falls back to Prompt Optimizer without blocking startup.

## Deployment

The application is published self-contained for `win-x64` with `<WindowsPackageType>None</WindowsPackageType>` and `<WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>`. The per-user installer writes under the user's application directory, registers one startup entry when enabled, registers the browser native host, creates Apps & Features metadata, starts onboarding, and supports repair/uninstall plus a clear user-data retention choice.

Code signing is an external release prerequisite. Unsigned local hackathon artifacts will be labeled honestly and may produce Windows trust warnings.

## Verification architecture

- Unit tests: domain reducers, policy, schemas, settings, migrations, hotkey transaction, Prompt Optimizer preservation.
- Mock integration: OpenAI success/failure, malformed output, Realtime events, browser bridge, audio lifecycle.
- Extension tests: deterministic local pages and Google-Forms-like fixtures.
- Windows E2E: actual Notepad/browser selection, overlays, hotkeys, Settings, microphone, app restart, installed artifact.
- Performance harness: warm/cold distributions with p50/p95.
- Live scripts: explicit environment gate, minimal fixtures, strict bounds, no raw sensitive response retention.
