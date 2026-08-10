# Implementation plan

## Phase gates

### Phase 0 - Audit and design

- [x] Audit both inspiration repositories read-only.
- [x] Identify selection, Settings, browser, hotkey, audio, action, and installer issues.
- [x] Write PRD, architecture, Settings design, and action safety policy.
- [x] Verify local `.env` key presence without printing it.
- [x] Prove a clean WinUI build and real top-level launch.
- [x] Initialize canonical Git remote, commit, push, and verify SHA.

### Phase 1 - Foundation

- [x] Solution and dependency boundaries.
- [x] Domain/contracts and JSON schemas.
- [ ] DI composition root and resident tray wiring. Redacted diagnostics and single-instance/activation primitives are implemented.
- [x] Atomic settings, migrations, corrupt recovery.
- [x] Current-user secure key store and dev-only `.env` adapter.
- [ ] Model availability checks. The canonical model registry and validation tests are implemented; live availability remains opt-in.
- [x] Transactional hotkey coordinator.
- [x] Interaction reducer and cancellation registry.
- [x] Fluent Settings shell, all navigation destinations, reusable controls.
- [x] Onboarding foundation.

### Phase 2 - Context vertical slice

- [ ] Warm orb state projection.
- [ ] Foreground identity.
- [ ] UIA and protected clipboard selection.
- [ ] OpenAI Responses typed Smart result.
- [ ] Result panel, Copy, Replace, Cancel.
- [ ] Mock integration tests and p50/p95 benchmark.

### Phase 3 - Visual context

- [ ] No-selection transition.
- [ ] Multi-monitor region overlay.
- [ ] In-memory image capture/preprocessing.
- [ ] Vision request.
- [ ] Local click-to-color.
- [ ] Cancellation/privacy/multi-DPI tests.

### Phase 4 - Smart, Guided, Quick Task

- [ ] Deterministic Guided options and context expiry.
- [ ] Confidence fallback.
- [ ] Prompt Optimizer preservation tests.
- [ ] Quick Task finalization, review, approval, validation, persistence.
- [ ] Unsupported-context UX and safety enforcement.

### Phase 5 - Browser and Take Action

- [ ] MV3 extension and native host.
- [ ] Authenticated same-user pipe and handshake.
- [ ] Browser selected-text fast path.
- [ ] Form discovery and deterministic execution.
- [ ] Typed plan/policy/confirmation/verification/undo.
- [ ] Google-Forms-like and real active-tab verification.

### Phase 6 - Smart Dictation

- [ ] Audio session coordinator, VAD, partial transcription.
- [ ] Cleanup, target revalidation, insertion, verification, undo.
- [ ] Voice Settings and device lifecycle tests.

### Phase 7 - Realtime Live

- [ ] Transport/session supervisor.
- [ ] Bounded audio send/playback and interruption.
- [ ] Transcript aggregation and narrow tools.
- [ ] Selection and one-shot screen context.

### Phase 8 - Navigation Guidance

- [ ] Explicit visible start/stop.
- [ ] Event-driven capture and duplicate suppression.
- [ ] DOM/accessibility-first context.
- [ ] Step verification, inactivity timeout, immediate stop.

### Phase 9 - Packaging and hardening

- [ ] Per-user installer, startup, native-host registration, repair/uninstall.
- [ ] All Settings pages fully functional.
- [ ] Accessibility, scaling, multiple displays.
- [ ] CI, security scans, dependency scan, secret scan.
- [ ] Required documentation and release artifacts/checksums.

### Phase 10 - Final evaluation

- [ ] Clean clone/restore/build.
- [ ] Full mocked and opt-in live tests.
- [ ] Installed-runtime E2E, fault injection, repeated reliability, soak.
- [ ] Performance and UI/accessibility reports.
- [ ] Artifact inspection and evidence-backed subsystem grades.
- [ ] Fix critical/high defects.
- [ ] Push final release commit and verify remote SHA.
