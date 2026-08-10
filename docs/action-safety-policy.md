# Action safety policy

## Principle

OpenAI may propose typed actions. Deterministic local code validates, classifies, confirms, executes, and verifies them. Model output, webpage content, Quick Task instructions, and Realtime tool arguments are untrusted data.

## Risk tiers

### Normally no confirmation

- Read selected text.
- Analyze an explicitly selected image.
- Run a non-executing Quick Task.
- Copy generated text after the user presses Copy.
- Open Guided Mode.
- Inspect bounded non-sensitive active-tab structure.
- Fill one non-sensitive field when directly requested and no submission occurs.
- Replace the currently selected text after the user presses Replace.

### Context-dependent confirmation

- Click a navigation button.
- Insert into a live compose field.
- Replace a large selection.
- Fill multiple fields.
- Open a new external URL.
- Start Navigation Guidance.
- Capture beyond one explicit screen request.
- Execute a plan proposed by a Quick Task.

### Always fresh confirmation

- Submit forms.
- Send email or messages.
- Purchase or schedule financial operations.
- Modify accounts, permissions, credentials, or secrets.
- Delete data.
- Post publicly.
- Accept legal terms.
- Upload private files.
- Any irreversible/high-impact operation.

## Validation

Reject unknown versions/types/target strategies, scripts, commands, offscreen destructive targets, stale fingerprints, cross-origin assumptions, inactive tabs/windows, oversized plans, unsupported fields, replayed confirmation IDs, or plans whose locally derived risk is lower than their steps require.

## Confirmation

Confirmation binds to an immutable plan ID, context fingerprint, visible summary, exact destination, risk, and expiry. Any plan/context change invalidates it. Confirmation cannot be cached for an always-confirm action.

## Execution and verification

Each step has preconditions, deterministic executor, expected outcome, verification method, and optional undo. After every meaningful step, inspect the DOM/accessibility/value/URL/title/screenshot as appropriate. Sending an input event is not success. Stop on mismatch and return the exact failed step with evidence safe for display.

Form fill defaults to review, never submit. Success requires at least one intended verified state transition; zero executed steps is failure.
