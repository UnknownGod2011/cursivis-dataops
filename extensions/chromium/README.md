# Cursivis Browser Bridge

The MV3 extension uses Chromium Native Messaging and requests host permission one origin at a time. It has no OpenAI credentials, no arbitrary script executor, no fixed localhost port, and no blanket content script.

The per-user installer must:

1. Package this directory with a stable signed extension identity.
2. Write the `app.cursivis.next.bridge` native-host manifest with the installed native-host executable path.
3. Restrict `allowed_origins` to that exact extension ID.
4. Register the manifest for supported Chromium browsers under the current user only.

The desktop app still validates protocol version, extension ID, nonce, expiry, session token, active tab identity, context fingerprint, policy, confirmation, and postconditions. Browser event delivery alone is never treated as verified success.
