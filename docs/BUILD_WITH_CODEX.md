# Build with Codex

This file records verified Codex-assisted milestones. It does not invent session IDs, model claims, tests, or remote state.

## Phase 0 - Audit and architecture

Completed with Codex on 2026-07-15.

Work:

- Read the complete 3,680-line product brief.
- Audited `UnknownGod2011/MX--Cursivis` at `08f294ff5643a67bd64a5064a572a1408c9ec3af`.
- Audited `UnknownGod2011/keyboard.wtf` at `946a156e132a04ef4353152fd1ed2f744c3c1b91`.
- Identified fixed-delay selection, unauthenticated localhost control, silent form submission, model-trusted risk, duplicate hotkey ownership, raw voice retention, Settings architecture, and startup/installer defects.
- Created the PRD, architecture, rebuilt Settings design, action safety policy, and phase plan.
- Verified the requested GPT-5.6 and GPT-Realtime-2.1 model IDs against current official OpenAI documentation.
- Diagnosed the local WinUI environment: default framework-dependent runtime failed registration; latest self-contained 2.2 path crashed in `Microsoft.UI.Xaml.dll`; pinned Windows App SDK 1.8 self-contained unpackaged path built and displayed a real responsive top-level window.

Validation:

- `.env` ignored and untracked.
- Filename-only secret scan passed.
- `git diff --cached --check` passed before the final commit.
- Local and remote branch SHA matched after push.

Milestone:

- Commit: `3a813fdd9584900809f6e229a811b50889504b74`
- Branch: `codex/build-cursivis-next`
- Remote: `https://github.com/UnknownGod2011/openai-cursivis`
- Draft PR: `https://github.com/UnknownGod2011/openai-cursivis/pull/1`

## Phase 1 - Desktop foundation checkpoint

Completed with Codex on 2026-07-16.

Work:

- Created the bounded .NET solution, domain, application, OpenAI, storage, browser-bridge, Windows platform, native-host, WinUI application, and test projects.
- Added typed interaction, context, settings, model, Quick Task, browser, and action contracts plus strict JSON schemas.
- Added atomic versioned settings persistence, migration and corrupt-file recovery, current-user DPAPI secret storage, and a separate development-only `.env` adapter.
- Added transactional global-hotkey, same-user single-instance/activation, foreground-window, authenticated pipe, native-messaging, and browser-session primitives.
- Added the OpenAI Responses gateway with strict structured output, disabled response storage, centralized model identifiers, and classified provider failures.
- Built the new multi-section WinUI Settings shell with reusable controls and ten destinations: Overview, OpenAI, Interaction, Custom Quick Task, Hotkeys, Voice, Browser Integration, Privacy & Safety, Diagnostics, and About.
- Fixed an Interaction page construction failure caused by XAML change events firing during `InitializeComponent` before the save-state control was available.

Validation:

- Release solution build passed with zero warnings and zero errors.
- Unit tests: 83 passed, 0 failed, 0 skipped.
- Integration tests: 24 passed, 0 failed, 0 skipped.
- Chromium extension manifest and JavaScript syntax validation passed.
- Staged secret scan passed; `.env` remained ignored and untracked.
- The actual self-contained Release `Cursivis.exe` launched successfully.
- Every Settings navigation destination was opened in the real application and rendered after the Interaction initialization fix.
- Development-only or not-yet-connected controls remain explicitly unavailable instead of reporting false health; they are not recorded as completed features.

Milestone:

- Commit: `e5ad1ed3df04a65c927f32083c50c64723f082af`
- Branch: `codex/build-cursivis-next`
- Remote SHA verified: `e5ad1ed3df04a65c927f32083c50c64723f082af`
- Authoritative product-goal file: `CURSIVIS_GOAL.txt`
- Product-goal SHA-256: `9CFF3303E313835E332EB6171BA042F182D82886C28001B728165057F9E0D603`

## Codex task reference

The work was performed in the current Codex desktop task. No durable Codex session ID was surfaced to the repository-writing workflow, so none is recorded here. The user should add the real task/session reference from the Codex UI when preparing final hackathon evidence.
