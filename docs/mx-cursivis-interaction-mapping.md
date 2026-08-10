# MX-Cursivis interaction mapping

Behavioral reference: read-only `UnknownGod2011/MX--Cursivis` at commit
`08f294ff5643a67bd64a5064a572a1408c9ec3af`. The legacy code is a behavior
reference only; Cursivis Next keeps the domain, application, infrastructure,
and WinUI boundaries described in `architecture.md`.

| Legacy flow | Proven behavior to retain | Cursivis Next owner | Intentional improvement |
| --- | --- | --- | --- |
| `TriggerController.HandleTapAsync` | Track the external source, show the orb immediately, capture selected text, and fall back to image lasso when no text exists. | `ContextInteractionCoordinator`, `ISelectionCaptureService`, and Windows capture adapters | One authoritative lifecycle, no fixed capture sleeps, bounded context lifetime, and stale async completions ignored. |
| `SelectionDetector` | Preserve clipboard contents while using a sentinel plus clipboard sequence changes for the fallback copy path. | `WindowsProtectedClipboardSelectionReader` | Layered browser/UIA/clipboard capture and no restoration over a newer user clipboard value. |
| `ResolveActionDecision` and `BuildActionMenuOptions` | Smart Mode runs the best operation automatically; Guided Mode uses deterministic context-specific choices. | `ContextTriggerService`, `GuidedOperationCatalog`, and the orb Guided view | Typed operations and strict Responses schemas; no model request just to build the menu. |
| `ResultPanelWindow.MoreOptionsRequested` and `ReRunLastSelectionAsync` | More Options reuses the captured text/image and source identity instead of recapturing or starting an unrelated flow. | `ContextInteractionSession` and `ExecuteGuidedAsync` | Reuse is validated by the same context fingerprint and explicit expiry result. |
| `CompleteSuccessfulRunAsync` | Store the latest context/result, copy the final successful output once, show completion on the orb, then show the Result Panel. | `ContextInteractionCoordinator` and `IResultClipboardService` | Exact-once copy receipts, bounded retry on contention, explicit failure notice, and no copy of partial/error/cancelled content. |
| `OrbOverlayWindow` | The orb owns workflow status, Smart/Guided choice, custom command capture, cancellation, Settings, and action selection. | `ContextOrbWindow` plus presentation state/view model | Compact borderless utility overlay, semantic states, keyboard-accessible operation list, per-monitor DPI placement, and restrained motion. |
| `ResultPanelWindow` | Borderless tool window, cursor anchoring, header drag, native edge/corner resize, scrolling, Escape/Close, and transient dismissal. | `ContextResultWindow`, `NativeOverlayWindow`, and overlay interaction coordinator | Multi-monitor work-area clamping, remembered size, child-overlay-aware dismissal, and source focus restoration. |
| `GlobalMouseWheelService` and `App.GlobalMouseWheelServiceOnMouseButtonPressed` | A low-level mouse hook dismisses the visible result when a button is pressed outside it. | `NativePointerMonitor` and pure `OverlayDismissalPolicy` | Hook only while needed; include the orb/Guided child surface and drag/resize state; no activation-delay timer. |
| `HandleLongPressAsync` and `VoiceCommandPromptService` | One-shot Talk captures a spoken or typed instruction, then runs the ordinary captured-context operation. | One-shot voice command service feeding `ExecuteGuidedAsync` | Transcription and ordinary Responses remain separate from a realtime session. |
| `LiveModeCoordinator` and live conversation transport | Live Mode owns a long-lived bidirectional audio session, interruption, tools, permissions, and explicit stop/cleanup. | Dedicated `ILiveModeSessionController` over `IRealtimeGateway` | OpenAI Realtime is reachable only through Live Mode; ordinary text, image, refine, Quick Task, and one-shot voice use typed non-realtime gateways. |
| `CompanionThemeService` | Apply theme to all companion windows as soon as the effective theme changes. | Shared overlay resources and runtime theme coordinator | Light, dark, and high-contrast resources update all visible overlay surfaces without recreating the interaction. |

The authoritative session identity is the `ContextSnapshot.Fingerprint`. A
Guided operation, refinement, insert/replace request, or child overlay may use
the current session only while its context remains unexpired and the target
identity is still valid for the requested mutation.
