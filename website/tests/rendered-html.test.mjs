import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

test("renders the production Cursivis landing page and release contract", async () => {
  const html = await readFile(new URL("../out/index.html", import.meta.url), "utf8");
  assert.match(html, /OpenAI, right where/);
  assert.match(html, /Download for Windows/);
  assert.match(html, /Cursivis-Setup-0\.1\.0-beta\.4-x64\.exe/);
  assert.match(html, /macOS/);
  assert.match(html, /Coming Soon/);
  assert.match(html, /Context Trigger/);
  assert.match(html, /Guided Mode/);
  assert.match(html, /Prompt Optimizer/);
  assert.match(html, /Live Mode/);
  assert.match(html, /Unknown Publisher/);
  assert.match(html, /API key/);
  assert.match(html, /stores it locally/);
  assert.doesNotMatch(html, /codex-preview|react-loading-skeleton|Your site is taking shape/);
});
