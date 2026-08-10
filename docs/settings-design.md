# Settings design

## Why the old Settings cannot be reused

MX-Cursivis Settings is a small topmost window with one giant scrolling page and approximately 2,700 lines of code-behind. It intermixes Logitech status, demo actions, hotkeys, several AI providers, URLs, ports, API keys, model downloads, runtime health, voice, browser automation, and permissions. Data is fragmented across multiple direct-written files and saved from async UI handlers without a staged transaction.

keyboard.wtf repeats several patterns through an HTML Settings surface: plain text shortcut inputs, immediate saves, thread-pool hotkey replacement, and an independent microphone test capture.

Those surfaces fail the new product because they do not provide:

- user-intent navigation;
- clear saved versus unsaved versus tested state;
- reliable recovery from invalid/corrupt values;
- reusable validated controls;
- accessibility and adaptive desktop layout;
- a coherent relationship with onboarding;
- honest component health;
- safe separation between UI, use cases, platform services, and persistence.

No XAML, HTML, styles, code-behind, layout, provider page, or persistence flow from the old Settings is ported.

## Information architecture

Use a WinUI `NavigationView` with persistent left navigation in wide mode and compact pane behavior at narrower widths.

1. **Overview** - verified setup health and direct repair/configure actions.
2. **OpenAI** - secure key lifecycle, quality profile, advanced exact models, connection/model tests.
3. **Interaction** - Smart/Guided default, orb visibility/placement, result behavior, startup, capture scope.
4. **Custom Quick Task** - guided describe/finalize/review/test/approve/save editor.
5. **Hotkeys** - recorder, configured/active state, conflicts, alternatives, restore defaults.
6. **Voice** - endpoint selector, real meter, language, voice, pause sensitivity, audio and Realtime tests.
7. **Browser Integration** - extension/native-host/pipe handshake health, permissions, setup, repair.
8. **Privacy & Safety** - screen behavior, memory, retention, confirmation boundaries, logs/diagnostics.
9. **Diagnostics** - sanitized health, versions, active hotkeys, recent errors, export/open/clear actions.
10. **About** - product/version/commit/license/docs/repository and real update status only if implemented.

The last appropriate section is restored on reopen. Search is omitted until an indexed destination map and reliable navigation are implemented.

## Shell behavior

- Header: section title, concise purpose, current section status.
- Content: page-owned `ScrollViewer` with one vertical scroll owner.
- Footer command area: Save and Discard for staged sections, with visible dirty/error/saving/saved state.
- Navigation and close protect unsaved changes through a content dialog.
- Safe immediate toggles use an application command and show failure inline; compound/sensitive values remain staged.
- Failed save preserves staged values and provides a retry path.
- Restore Defaults is section-scoped and confirms only when destructive.
- Restart messaging appears only when runtime behavior truly cannot be applied live.

## Design tokens

Tokens live in theme resource dictionaries and support light, dark, and high-contrast resources.

### Spacing

- `Space2 = 2`
- `Space4 = 4`
- `Space8 = 8`
- `Space12 = 12`
- `Space16 = 16`
- `Space24 = 24`
- `Space32 = 32`

### Shape and surfaces

- Small control radius: 4.
- Standard card radius: 8.
- Hero/preview radius: 12.
- Use native `Card`/settings-expander patterns or one clear surface layer; avoid nested double cards.
- Theme-aware strokes and system brushes; no hard-coded light-only colors.

### Typography

- Display: product/first-run hero only.
- Title: section heading.
- Subtitle: subsection heading.
- Body: setting label and content.
- Caption: status/help; never below accessible readable size.

### Semantic states

Each status combines icon, text, and color:

- neutral/unverified;
- testing/in progress;
- healthy/verified;
- warning/attention;
- error/action required;
- unsaved/dirty;
- unavailable/unsupported.

## Reusable controls

- `SettingsSectionHeader`
- `SettingsCard`
- `SettingsRow`
- `StatusIndicator`
- `InlineValidationMessage`
- `SaveStateBar`
- `HotkeyRecorder`
- `SecureKeyInput`
- `ConnectionTestPanel`
- `MicrophonePickerAndMeter`
- `QualityProfilePicker`
- `QuickTaskDefinitionEditor`
- `ConfirmationPolicySummary`
- `DiagnosticsEntry`

Controls expose view state and commands only. They do not register hotkeys, open devices, call OpenAI, connect to the extension, or write files.

## Section requirements

### Overview

Show OpenAI, quality profile, Realtime, microphone, hotkeys, Quick Task, extension, native host/pipe, screen capture, and startup. Initial state is Unverified, not green. Each problem links directly to Configure, Test, Repair, or Setup.

### OpenAI

- Existing key renders as `Saved key` with no recoverable plaintext.
- Replacement field is temporary and cleared after secure storage.
- Saved and Tested are independent timestamps/states.
- Delete requires confirmation and removes secure storage plus cached clients.
- Connection errors distinguish authentication, quota, rate limit, network, model access, malformed response, and cancellation.
- Ordinary users pick Economy/Balanced/Best. Advanced disclosure shows exact API IDs and runtime availability.
- Pricing is linked to current official OpenAI documentation, never hard-coded.

### Interaction

Expose default Smart/Guided mode, always-on/operation-only orb, placement/reset, launch at sign-in, result close behavior, context retention explanation, active-window/full-display capture preference, and navigation scope. Preview controls reflect actual staged state and are not decorative.

### Custom Quick Task

Guided flow:

1. Describe the task.
2. Generate Final Instruction.
3. Review/edit finalized instruction.
4. Inspect/edit supported context, output mode, and action-proposal ability.
5. Test with safe non-sensitive fixture.
6. Check explicit approval and Save.

Display rough description, generated draft, currently saved definition, and unsaved edits distinctly. No raw JSON in the ordinary editor. Restore Prompt Optimizer is a real section reset.

### Hotkeys

For every function show configured chord, active chord, conflict/verification state, and suggested alternatives. Recorder enters a visible capture state, Escape cancels recording, modifier-only or dangerous/reserved chords fail validation, and duplicates use canonical modifier ordering. Saving calls the transactional hotkey use case; failure leaves the old active shortcut intact and staged proposal visible.

### Voice

Use stable Windows endpoint IDs. One coordinator owns the device. The meter is backed by actual capture under a cancellable Settings test lease. Handle add/remove while open. Expose voice, language, pause sensitivity, Audio Test, and Realtime Connection Test with real states.

### Browser Integration

Connected means a current authenticated handshake, not a process or open port. Show extension version, protocol version, supported browser, native host, pipe health, last successful handshake, permissions explanation, Test, Repair/Reconnect, and installation instructions.

### Privacy & Safety

Explain one-shot versus Navigation Guidance, visible capture indicators, active-window/display scope, immediate stop, screenshot disposal, clipboard fallback, Quick Task policy, action confirmation, and the fact that the extension never receives the API key. Memory can be disabled, viewed, individually deleted, or cleared. Clear Memory/Logs use confirmation.

### Diagnostics

Show app/build version, current active hotkeys, sanitized OpenAI/browser/microphone/capture/storage/settings health, and recent categorized errors. Copy/Export Diagnostics uses central redaction and excludes keys, headers, raw text, images, audio, private prompts, and form values. Open Logs and Clear Logs must be functional.

### About

Show real version, build commit SHA, license, documentation, canonical repository, release channel when implemented, and concise OpenAI acknowledgment. Do not show a fake update button.

## Validation and persistence

View models maintain saved snapshot, editable snapshot, validation issues, dirty paths, test status, and save state. Validation runs locally where possible and is debounced/cancellable for external tests.

Save transaction:

1. Validate section and cross-setting constraints.
2. Prepare platform changes without removing working state.
3. Write versioned temp file in the destination directory.
4. Flush file and directory metadata as practical.
5. Atomically replace and retain bounded backup.
6. Commit platform change (for example, unregister previous hotkey only after the new one is active).
7. Publish immutable saved settings and health event.
8. Roll back both file and platform state on failure.

Corrupt settings are quarantined. Migrations are explicit and sanitized. A bad Quick Task recovers only that section to Prompt Optimizer.

## Accessibility and adaptive behavior

- Full keyboard navigation and visible focus.
- Automation name/help text for every control and status.
- No meaning conveyed by color alone.
- Narrator-friendly validation and save announcements without focus theft.
- High-contrast resources and reduced-motion preference.
- One logical tab sequence per page.
- Minimum useful window size and reachable controls at 100%, 125%, 150%, and 200% scaling.
- Wide: persistent pane and generous content width.
- Medium: compact pane and reduced page padding.
- Narrow: overlay navigation pane, single-column rows, footer commands remain reachable.
- Device lists and health tests remain responsive while asynchronous work runs.

## Onboarding relationship

Onboarding is a thin flow over the same OpenAI, audio, hotkey, browser, and privacy view models/use cases. It does not save to a separate file or perform direct platform operations. Optional steps can be skipped and Overview remains honest about what is unverified.
