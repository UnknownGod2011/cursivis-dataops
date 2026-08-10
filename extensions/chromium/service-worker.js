const NATIVE_HOST = "app.cursivis.next.bridge";
const PROTOCOL_VERSION = 1;
const MAX_SELECTION = 50_000;
const MAX_CONTEXT = 8_000;
const REQUEST_TIMEOUT_MS = 5_000;

let nativePort = null;
let sessionToken = null;
let connecting = null;
const pending = new Map();

chrome.runtime.onInstalled.addListener(() => {
  chrome.storage.local.set({ bridgeState: "disconnected", protocolVersion: PROTOCOL_VERSION });
});

chrome.action.onClicked.addListener(() => chrome.runtime.openOptionsPage());

chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
  if (message?.type === "cursivis.getBridgeState") {
    getBridgeState().then(sendResponse);
    return true;
  }
  if (message?.type === "cursivis.connect") {
    connectNative().then(() => getBridgeState()).then(sendResponse).catch(error => {
      sendResponse({ state: "error", message: safeError(error) });
    });
    return true;
  }
  return false;
});

async function connectNative() {
  if (nativePort && sessionToken) return;
  if (connecting) return connecting;

  connecting = new Promise((resolve, reject) => {
    try {
      nativePort = chrome.runtime.connectNative(NATIVE_HOST);
      nativePort.onMessage.addListener(handleNativeMessage);
      nativePort.onDisconnect.addListener(() => {
        const message = chrome.runtime.lastError?.message ?? "Native host disconnected.";
        nativePort = null;
        sessionToken = null;
        chrome.storage.local.set({ bridgeState: "disconnected", bridgeMessage: message });
        for (const request of pending.values()) request.reject(new Error("Native host disconnected."));
        pending.clear();
      });

      const correlationId = createId();
      const timeout = setTimeout(() => reject(new Error("Native host handshake timed out.")), REQUEST_TIMEOUT_MS);
      pending.set(correlationId, {
        resolve: value => { clearTimeout(timeout); resolve(value); },
        reject: error => { clearTimeout(timeout); reject(error); }
      });

      nativePort.postMessage(envelope("hello", correlationId, {
        extensionId: chrome.runtime.id,
        extensionVersion: chrome.runtime.getManifest().version,
        nonce: createNonce(),
        expiresAt: new Date(Date.now() + 30_000).toISOString(),
        requestedCapabilities: ["selection", "form_discovery", "safe_form_fill"]
      }, null));
    } catch (error) {
      nativePort = null;
      reject(error);
    }
  }).finally(() => { connecting = null; });

  return connecting;
}

async function handleNativeMessage(message) {
  try {
    validateEnvelope(message);
    if (message.type === "welcome") {
      if (typeof message.payload?.sessionToken !== "string" || message.payload.sessionToken.length < 40) {
        throw new Error("Native host returned an invalid session token.");
      }
      sessionToken = message.payload.sessionToken;
      await chrome.storage.local.set({
        bridgeState: "connected",
        bridgeMessage: "Authenticated desktop connection",
        extensionVersion: chrome.runtime.getManifest().version,
        protocolVersion: PROTOCOL_VERSION,
        lastHandshake: new Date().toISOString()
      });
      settle(message.correlationId, message.payload, null);
      return;
    }
    if (message.type === "error") {
      settle(message.correlationId, null, new Error(message.payload?.message ?? "Cursivis browser request failed."));
      return;
    }

    const response = await dispatchDesktopRequest(message);
    nativePort?.postMessage(envelope(response.type, message.correlationId, response.payload, sessionToken));
  } catch (error) {
    if (nativePort && message?.correlationId) {
      nativePort.postMessage(envelope("error", message.correlationId, {
        code: "extension_validation_failed",
        message: safeError(error),
        retryable: false
      }, sessionToken));
    }
  }
}

async function dispatchDesktopRequest(message) {
  if (!sessionToken || message.sessionToken !== sessionToken) {
    throw new Error("Desktop session authentication failed.");
  }
  const tab = await getActiveTab();
  await requireSitePermission(tab.url);
  await ensureContentScript(tab.id);

  const contentMessage = {
    source: "cursivis-extension",
    type: message.type,
    payload: { ...message.payload, maximumCharacters: Math.min(message.payload?.maximumCharacters ?? MAX_SELECTION, MAX_SELECTION), maximumNearbyCharacters: MAX_CONTEXT }
  };
  const result = await chrome.tabs.sendMessage(tab.id, contentMessage);
  if (!result?.ok) throw new Error(result?.message ?? "The active tab rejected the request.");

  const responseTypes = {
    get_selection: "selection",
    discover_form: "form",
    execute: "step_result"
  };
  const responseType = responseTypes[message.type];
  if (!responseType) throw new Error("Unsupported desktop request type.");
  return { type: responseType, payload: result.payload };
}

async function getActiveTab() {
  const [tab] = await chrome.tabs.query({ active: true, lastFocusedWindow: true });
  if (!tab?.id || !/^https?:\/\//i.test(tab.url ?? "")) throw new Error("The active tab is not an accessible web page.");
  return tab;
}

async function requireSitePermission(urlText) {
  const url = new URL(urlText);
  const originPattern = `${url.origin}/*`;
  if (!(await chrome.permissions.contains({ origins: [originPattern] }))) {
    throw new Error("This site has not been granted Cursivis access. Open extension options to grant it.");
  }
}

async function ensureContentScript(tabId) {
  try {
    const response = await chrome.tabs.sendMessage(tabId, { source: "cursivis-extension", type: "ping" });
    if (response?.ok) return;
  } catch { /* Inject only after the per-site permission check succeeds. */ }
  await chrome.scripting.executeScript({ target: { tabId }, files: ["content-script.js"] });
}

function handleNativeDisconnect() {
  nativePort = null;
  sessionToken = null;
}

function settle(correlationId, value, error) {
  const request = pending.get(correlationId);
  if (!request) return;
  pending.delete(correlationId);
  error ? request.reject(error) : request.resolve(value);
}

function envelope(type, correlationId, payload, token) {
  return { protocolVersion: PROTOCOL_VERSION, type, correlationId, sentAt: new Date().toISOString(), payload, sessionToken: token };
}

function validateEnvelope(message) {
  if (!message || message.protocolVersion !== PROTOCOL_VERSION || typeof message.type !== "string" || typeof message.correlationId !== "string") {
    throw new Error("Native host returned an invalid protocol message.");
  }
}

function createId() {
  return crypto.randomUUID().replaceAll("-", "");
}

function createNonce() {
  const bytes = crypto.getRandomValues(new Uint8Array(32));
  return btoa(String.fromCharCode(...bytes)).replaceAll("+", "-").replaceAll("/", "_").replaceAll("=", "");
}

function safeError(error) {
  return error instanceof Error ? error.message.slice(0, 300) : "The browser bridge request failed.";
}

async function getBridgeState() {
  const stored = await chrome.storage.local.get(["bridgeState", "bridgeMessage", "extensionVersion", "protocolVersion", "lastHandshake"]);
  return {
    state: stored.bridgeState ?? "disconnected",
    message: stored.bridgeMessage ?? "Not connected",
    extensionVersion: stored.extensionVersion ?? chrome.runtime.getManifest().version,
    protocolVersion: stored.protocolVersion ?? PROTOCOL_VERSION,
    lastHandshake: stored.lastHandshake ?? null
  };
}
