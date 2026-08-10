# Product requirements

## Product statement

Cursivis Next adds OpenAI directly to the user's cursor and active workflow. It removes the repeated copy-switch-paste-explain-copy-back loop without giving a model uncontrolled access to the computer.

Core interaction:

1. User selects or points at context.
2. User presses a global trigger or selects a mode from the orb.
3. Cursivis captures the smallest relevant context.
4. Cursivis determines or asks for intent.
5. Cursivis returns useful content.
6. If requested, Cursivis proposes and safely applies a typed action.

## Non-negotiable constraints

- Windows-first standalone product; no Logitech dependency.
- OpenAI is the only AI provider.
- Inspiration repositories remain read-only and are not copied into this repository.
- No arbitrary generated scripts, shell commands, JavaScript, selectors, or coordinate macros.
- No secret in source, logs, diagnostics, extension, installer, screenshots, tests, or Git history.
- Mocked tests are the default; live tests require `CURSIVIS_RUN_LIVE_TESTS=1`.
- Do not claim success without real build/runtime/API evidence.

## Primary capabilities

### Context Trigger (`Ctrl+Alt+O`)

- Show visible orb feedback in under 50 ms for the warm running process.
- Capture active application identity and immutable context fingerprint.
- Selection priority: authenticated browser bridge, Windows UI Automation, protected clipboard fallback.
- Selected text routes to Smart or Guided mode.
- No selection opens region capture, preferably under 250 ms warm.
- A click or tiny movement performs local pixel color detection and returns hex, RGB, nearest name, copy, and swatch.
- Escape or global Cancel stops capture immediately and disposes image data.

### Realtime Live Mode (`Ctrl+Alt+P`)

- Bidirectional OpenAI Realtime audio with server/semantic VAD.
- Visible mic level, partial user transcript, response transcript, and streamed audio.
- Same key ends the session; Cancel is universal.
- Barge-in cancels playback and model output promptly.
- Explicit tools obtain selection, browser, app, or screen context.
- One-shot screen analysis and Navigation Guidance are separate visible modes.
- Complex action plans are generated through a structured Responses request because Realtime tool arguments do not provide Structured Outputs.

### Direct Take Action (`Ctrl+Alt+I`)

- Gather only current selection, active browser/focused field, unexpired latest result, and active application.
- Produce a typed versioned plan with goal, fingerprint, locally derived risk, confirmation decision, bounded steps, and expected outcomes.
- Preview medium/high-risk plans.
- Validate and execute deterministically, verify meaningful state after each step, stop on mismatch, and return an execution receipt.
- Never reuse stale context from another application.

### Smart Dictation (`Ctrl+Alt+U`)

- Start recording immediately with visible partials and level.
- Stop on natural pause or same-trigger press.
- Preserve names, URLs, code, and numbers; remove fillers/false starts and honor corrections.
- Insert polished text into the original target only after target revalidation.
- Use UI Automation first and protected clipboard fallback second.
- Verify insertion and expose Undo.

### Custom Quick Task (`Ctrl+Alt+Y`)

- Default task is Prompt Optimizer.
- Preserve every requirement, constraint, named entity, link, deadline, contradiction, and requested output.
- Remove repetition and ambiguity only when safe; invent nothing.
- Output only the optimized prompt unless explanation is requested.
- One persistent user-defined task is supported now; interfaces allow multiple named tasks later.
- Rough task description is finalized through a cost-efficient OpenAI request, reviewed and manually editable, then explicitly approved and atomically saved.
- A saved task can propose a Take Action plan but cannot bypass policy.
- Text-only task with no selection opens a compact input panel; image-capable task opens region capture; unsupported context is explained rather than reinterpreted.

## Smart and Guided modes

Smart Mode returns strict `SmartResult` data containing context type, intent, confidence, final content, suggested action summary, risk hint, and confirmation hint. Hints never authorize execution. Below the confidence threshold, present Guided Mode.

Guided Mode:

- derives likely actions locally by context type;
- always provides custom task input;
- supports keyboard navigation;
- exposes common options immediately;
- preserves one bounded context snapshot for More Options;
- clearly expires stale selection;
- offers copy, insert, replace, refine, or Take Action.

## Browser and forms

- Manifest V3 extension acts in the real active tab.
- Request optional host permissions per site and inject on demand.
- Extension never receives the OpenAI key.
- Dedicated selected-text request returns bounded nearby semantic context only when needed.
- Form discovery normalizes visible label, description, required state, type, options, current value, and stable target ID.
- Deterministic executors support text, textarea, email, number, radio, checkbox, select, and safe date fields.
- Verify each filled value and highlight filled fields.
- Never invent identity answers, bypass CAPTCHA, fill passwords/payment/government IDs by default, upload silently, or submit a form silently.
- Submit is a separate explicit, freshly confirmed terminal action.

## Orb and result experience

Authoritative interaction states:

`Idle`, `TextSelectionDetected`, `OpeningSnip`, `SelectingRegion`, `ImageCaptured`, `ColorDetected`, `Listening`, `Transcribing`, `Thinking`, `Generating`, `RunningQuickTask`, `Speaking`, `Guiding`, `WaitingForUser`, `ActionProposed`, `AwaitingConfirmation`, `Executing`, `Verifying`, `Done`, `Cancelled`, `Error`.

The orb always combines visual treatment with a readable label. It never steals focus while idle, is draggable, remembers/clamps placement across displays, and can be always-on or operation-only.

The result panel remains within the active monitor and supports keyboard navigation, controlled scrolling, streaming text, Copy, Replace Selection, Insert at Cursor, Refine, More Options, custom follow-up, Take Action, and Close.

## Settings and onboarding

Settings sections are fixed for this milestone: Overview, OpenAI, Interaction, Custom Quick Task, Hotkeys, Voice, Browser Integration, Privacy & Safety, Diagnostics, About.

First-run onboarding reuses the same services and controls for API key, microphone, default mode, hotkey verification, extension, and privacy. Optional steps can be skipped.

## Security, privacy, and memory

- Production key: current-user DPAPI or Credential Manager; masked, replaceable, deletable, testable, never revealed.
- Development `.env`: ignored local live-test input only; never auto-imported into production storage.
- Browser transport: Native Messaging plus same-user authenticated named pipe, strict schemas, limits, nonces, expiry, rate limits, and protocol/version handshake.
- Screen capture is explicit and visible. Navigation capture is event-driven, deduplicated, bounded, and immediately stoppable.
- Default logs exclude raw selection, prompt, Quick Task source, screenshots, audio, transcripts, form values, keys, and authorization headers.
- Personalization memory is opt-in, short, bounded, inspectable, individually deletable, clearable, and disabled by default.

## Performance measures

Measure p50 and p95, including ordinary failures:

- warm orb visibility;
- browser and UIA selection;
- no-selection snip opening;
- result request dispatch and end-to-end response;
- Settings open/navigation/save;
- Prompt Optimizer and Quick Task dispatch;
- image preprocessing;
- bridge round trip and action verification;
- dictation insertion;
- Realtime connection;
- navigation-step processing.

Cold start is reported separately from the warm <50 ms orb target. “Classification under 200 ms” means local context classification, not a network round trip.

## Acceptance stance

Every subsystem is graded PASS, PARTIAL, FAIL, or NOT TESTED with evidence. Production-grade is not claimed if installer, Realtime, Prompt Optimizer, Take Action verification, hotkey persistence, Settings, browser execution, secrets, or final remote synchronization remain unverified.
