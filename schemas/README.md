# Contract schemas

These files are the canonical cross-process and model-output contracts. C# and TypeScript projections must preserve the same version, required fields, closed enum sets, size limits, and `additionalProperties: false` behavior.

Model output, browser messages, and Realtime tool arguments are untrusted until they pass the corresponding schema plus local policy validation. Unknown versions and unknown action kinds fail closed.
