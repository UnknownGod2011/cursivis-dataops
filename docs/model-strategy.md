# OpenAI model strategy

Last catalog review: 2026-07-15.

The model registry is explicit, capability-based, and OpenAI-only. A profile never silently upgrades to a materially more expensive model. Runtime account access is tested separately from catalog validity.

| Product profile | API model ID | Required capabilities | Runtime status |
| --- | --- | --- | --- |
| Economy | `gpt-5.6-luna` | text, image input, Structured Outputs, functions | Not live-tested yet |
| Balanced | `gpt-5.6-terra` | text, image input, Structured Outputs, functions | Not live-tested yet |
| Best quality | `gpt-5.6-sol` | text, image input, Structured Outputs, functions | Not live-tested yet |
| Live | `gpt-realtime-2.1` | text/audio/image input, audio output, functions | Not live-tested yet |
| Economy live | `gpt-realtime-2.1-mini` | text/audio/image input, audio output, functions | Not live-tested yet; account access may be restricted |
| Streaming transcription | `gpt-realtime-whisper` | streaming speech-to-text | Not live-tested yet |
| Bounded transcription | `gpt-4o-mini-transcribe` | uploaded bounded audio | Not live-tested yet |
| Quality transcription | `gpt-4o-transcribe` | uploaded bounded audio | Not live-tested yet |

Catalog evidence:

- [OpenAI model catalog](https://developers.openai.com/api/docs/models)
- [`gpt-5.6-luna`](https://developers.openai.com/api/docs/models/gpt-5.6-luna)
- [`gpt-5.6-terra`](https://developers.openai.com/api/docs/models/gpt-5.6-terra)
- [`gpt-realtime-2.1`](https://developers.openai.com/api/docs/models/gpt-realtime-2.1)
- [Official OpenAI .NET SDK](https://github.com/openai/openai-dotnet)

## Routing rules

- Smart, Guided, image analysis, Prompt Optimizer, Quick Task finalization/execution, and complex action planning use the Responses API.
- Every model-produced decision or plan uses a strict JSON Schema response and is parsed again locally.
- Live Mode uses the Realtime API for duplex audio and narrow function calls.
- Realtime function arguments are untrusted JSON. Complex planning is handed to a Structured Outputs Responses request before local policy evaluation.
- Transcription uses the streaming model while Live/Dictation is active and a bounded transcription model for short upload fallback.

## Fallback rules

- Best quality may fall back to Balanced, then Economy, only after the user-visible availability test fails.
- Balanced may fall back to Economy.
- Streaming transcription may fall back to the economy bounded transcription path when streaming is unavailable and the recording is within configured bounds.
- Live models never silently fall back to non-Realtime behavior; the UI reports Live unavailable.
- Authentication, permission, and billing/usage failures stop the test flow rather than triggering repeated calls.

## Verification record

Mocked and contract tests run by default and spend no API credits. Live verification requires `CURSIVIS_RUN_LIVE_TESTS=1` plus the ignored root `.env`. This table must be updated with exact date, selected IDs, and observed outcome after those tests run; failures remain recorded rather than being rewritten as success.
