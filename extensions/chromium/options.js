const connectionStatus = document.querySelector("#connection-status");
const connectionDetail = document.querySelector("#connection-detail");
const siteStatus = document.querySelector("#site-status");
const siteDetail = document.querySelector("#site-detail");
const announcement = document.querySelector("#announcement");

document.querySelector("#connect").addEventListener("click", async () => {
  setStatus(connectionStatus, "Testing…", "neutral");
  const result = await chrome.runtime.sendMessage({ type: "cursivis.connect" });
  renderBridge(result);
});
document.querySelector("#grant").addEventListener("click", () => updatePermission(true));
document.querySelector("#revoke").addEventListener("click", () => updatePermission(false));

refresh();

async function refresh() {
  renderBridge(await chrome.runtime.sendMessage({ type: "cursivis.getBridgeState" }));
  const target = await activeOrigin();
  if (!target) {
    siteDetail.textContent = "No supported active web page is available.";
    setStatus(siteStatus, "Unavailable", "neutral");
    return;
  }
  siteDetail.textContent = target.origin;
  const granted = await chrome.permissions.contains({ origins: [target.pattern] });
  setStatus(siteStatus, granted ? "Granted" : "Not granted", granted ? "good" : "neutral");
}

async function updatePermission(grant) {
  const target = await activeOrigin();
  if (!target) {
    announcement.textContent = "Open an http or https page before changing site access.";
    return;
  }
  const changed = grant
    ? await chrome.permissions.request({ origins: [target.pattern] })
    : await chrome.permissions.remove({ origins: [target.pattern] });
  announcement.textContent = changed
    ? `${grant ? "Granted" : "Removed"} access for ${target.origin}.`
    : `Access for ${target.origin} was not changed.`;
  await refresh();
}

async function activeOrigin() {
  const [tab] = await chrome.tabs.query({ active: true, lastFocusedWindow: true });
  if (!/^https?:\/\//i.test(tab?.url ?? "")) return null;
  const url = new URL(tab.url);
  return { origin: url.origin, pattern: `${url.origin}/*` };
}

function renderBridge(result) {
  const connected = result?.state === "connected";
  setStatus(connectionStatus, connected ? "Connected" : result?.state === "error" ? "Error" : "Disconnected", connected ? "good" : result?.state === "error" ? "bad" : "neutral");
  connectionDetail.textContent = result?.message ?? "The native host has not been tested.";
}

function setStatus(element, text, className) {
  element.textContent = text;
  element.className = `status ${className}`;
}
