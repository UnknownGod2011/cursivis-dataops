(() => {
  if (globalThis.__cursivisContentScriptLoaded) return;
  globalThis.__cursivisContentScriptLoaded = true;

  const SUPPORTED_FIELDS = new Set(["text", "textarea", "email", "number", "radio", "checkbox", "select", "date"]);
  const SENSITIVE_PATTERN = /(password|passcode|credit|card|cvv|cvc|iban|routing|account.?number|social.?security|ssn|passport|government.?id|aadhaar|otp|one.?time)/i;
  let lastDiscoveredForm = null;

  chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
    if (message?.source !== "cursivis-extension") return false;
    if (message.type === "ping") {
      sendResponse({ ok: true });
      return false;
    }
    Promise.resolve(handleRequest(message.type, message.payload ?? {}))
      .then(payload => sendResponse({ ok: true, payload }))
      .catch(error => sendResponse({ ok: false, message: safeError(error) }));
    return true;
  });

  function handleRequest(type, payload) {
    if (type === "get_selection") return getSelection(payload);
    if (type === "discover_form") return discoverForm();
    if (type === "execute") return executeCommand(payload);
    throw new Error("Unsupported browser request.");
  }

  function getSelection(payload) {
    const maximum = clamp(payload.maximumCharacters, 1, 50_000, 50_000);
    const selection = globalThis.getSelection();
    const raw = selection?.toString() ?? "";
    const selectedText = raw.slice(0, maximum);
    const anchor = selection?.anchorNode?.parentElement ?? document.activeElement;
    const focused = describeFocusedElement(document.activeElement);
    let nearby = null;
    if (payload.includeNearbySemanticContext && anchor) {
      const text = (anchor.closest("article,main,section,form") ?? anchor).innerText ?? "";
      nearby = text.replace(/\s+/g, " ").trim().slice(0, clamp(payload.maximumNearbyCharacters, 0, 8_000, 8_000));
    }
    return {
      tab: tabIdentity(payload.tab),
      selectedText,
      nearbySemanticContext: nearby,
      focusedElement: focused,
      isTruncated: raw.length > maximum
    };
  }

  function discoverForm() {
    const form = document.activeElement?.closest("form") ?? visibleElements("form")[0];
    if (!form) throw new Error("No visible form was found in the active tab.");
    const fields = fieldElements(form).slice(0, 300).map((element, index) => describeField(element, index));
    const controls = actionElements(form).slice(0, 50).map((element, index) => describeControl(element, index));
    const snapshot = {
      tab: tabIdentity(),
      formId: stableId(form, 0),
      accessibleName: cleanText(form.getAttribute("aria-label") || form.querySelector("legend,h1,h2,h3")?.textContent),
      fields,
      controls,
      contextFingerprint: fingerprint(`${location.origin}|${location.pathname}|${fields.map(field => `${field.stableTargetId}:${field.kind}:${field.currentValue ?? ""}`).join("|")}|${controls.map(control => `${control.stableTargetId}:${control.kind}`).join("|")}`)
    };
    lastDiscoveredForm = {
      formId: snapshot.formId,
      contextFingerprint: snapshot.contextFingerprint,
      urlOrigin: location.origin
    };
    return snapshot;
  }

  function executeCommand(command) {
    if (command?.tab && !sameTab(command.tab)) return failed(command?.stepId, "stale_tab", "The active tab changed before the action ran.");
    if (!lastDiscoveredForm ||
        lastDiscoveredForm.urlOrigin !== location.origin ||
        command.contextFingerprint !== lastDiscoveredForm.contextFingerprint) {
      return failed(command?.stepId, "stale_form", "The visible form changed before the action ran.");
    }
    const fields = fieldElements(document);
    const element = fields.find((candidate, index) => stableId(candidate, index) === command.stableTargetId)
      ?? actionElements(document).find((candidate, index) => stableId(candidate, index) === command.stableTargetId);
    if (!element) return failed(command?.stepId, "target_missing", "The requested page element is no longer available.");
    if (isSensitive(element)) return failed(command.stepId, "sensitive_field", "Cursivis will not fill this sensitive field.");

    switch (command.kind) {
      case "set_value":
        if (!(element instanceof HTMLInputElement || element instanceof HTMLTextAreaElement)) return failed(command.stepId, "unsupported_target", "The target cannot accept text.");
        setNativeValue(element, String(command.value ?? ""));
        return verify(command.stepId, element.value === String(command.value ?? ""), element.value);
      case "set_checked":
        if (!(element instanceof HTMLInputElement) || !["checkbox", "radio"].includes(element.type)) return failed(command.stepId, "unsupported_target", "The target is not a checkable field.");
        element.checked = Boolean(command.checked);
        element.dispatchEvent(new Event("input", { bubbles: true }));
        element.dispatchEvent(new Event("change", { bubbles: true }));
        return verify(command.stepId, element.checked === Boolean(command.checked), String(element.checked));
      case "select_option":
        if (!(element instanceof HTMLSelectElement)) return failed(command.stepId, "unsupported_target", "The target is not a select field.");
        element.value = String(command.value ?? "");
        element.dispatchEvent(new Event("change", { bubbles: true }));
        return verify(command.stepId, element.value === String(command.value ?? ""), element.value);
      case "focus":
        element.focus({ preventScroll: false });
        return verify(command.stepId, document.activeElement === element, null);
      case "highlight":
        element.scrollIntoView({ block: "center", behavior: "smooth" });
        element.animate([{ outline: "3px solid #8b5cf6" }, { outline: "0 solid transparent" }], { duration: 1200 });
        return verify(command.stepId, true, null);
      case "click_navigation":
        if (!(element instanceof HTMLAnchorElement || element instanceof HTMLButtonElement)) return failed(command.stepId, "unsafe_click_target", "Only visible navigation links or buttons can be clicked.");
        element.click();
        return verify(command.stepId, true, null);
      case "submit_search":
        if (!(element instanceof HTMLButtonElement || (element instanceof HTMLInputElement && element.type === "submit"))) return failed(command.stepId, "unsafe_search_target", "The target is not an explicit search control.");
        if (!isSearchForm(element.form ?? element.closest("form"))) return failed(command.stepId, "unsafe_search_target", "The visible form is not an ordinary search form.");
        element.click();
        return verify(command.stepId, true, null);
      case "submit":
        if (!command.confirmationId || command.confirmationId.length < 20) return failed(command.stepId, "fresh_confirmation_required", "Submitting requires fresh confirmation in Cursivis.");
        if (!(element instanceof HTMLButtonElement || (element instanceof HTMLInputElement && element.type === "submit"))) return failed(command.stepId, "unsafe_submit_target", "The target is not an explicit submit control.");
        element.click();
        return verify(command.stepId, true, null);
      default:
        return failed(command.stepId, "unsupported_command", "The requested browser command is not supported.");
    }
  }

  function describeField(element, index) {
    const kind = fieldKind(element);
    const label = labelFor(element);
    const options = element instanceof HTMLSelectElement
      ? [...element.options].filter(option => !option.disabled).map(option => cleanText(option.textContent)).filter(Boolean).slice(0, 100)
      : [];
    return {
      stableTargetId: stableId(element, index),
      kind,
      label,
      description: cleanText(element.getAttribute("aria-description") || describedByText(element)),
      isRequired: element.required || element.getAttribute("aria-required") === "true",
      isSensitive: isSensitive(element),
      currentValue: isSensitive(element) ? null : currentValue(element),
      options
    };
  }

  function describeControl(element, index) {
    const isSubmit = element.matches("button[type='submit'],input[type='submit']");
    const kind = isSubmit
      ? (isSearchForm(element.form ?? element.closest("form")) ? "search_submit" : "submit")
      : "navigation";
    const label = labelFor(element);
    return {
      stableTargetId: stableId(element, index),
      kind,
      label,
      isHighImpact: kind === "submit"
    };
  }

  function isSearchForm(form) {
    if (!(form instanceof HTMLFormElement)) return false;
    const action = String(form.getAttribute("action") || "").toLowerCase();
    return form.getAttribute("role") === "search" ||
      action.includes("/search") ||
      Boolean(form.querySelector("input[type='search'],input[name='q'],textarea[name='q']"));
  }

  function fieldElements(root) {
    return visibleElements("input:not([type='hidden']):not([type='password']),textarea,select", root)
      .filter(element => SUPPORTED_FIELDS.has(fieldKind(element)));
  }

  function actionElements(root) {
    return visibleElements("button[type='submit'],input[type='submit'],a[href]", root);
  }

  function visibleElements(selector, root = document) {
    return [...root.querySelectorAll(selector)].filter(element => {
      const rect = element.getBoundingClientRect();
      const style = getComputedStyle(element);
      return rect.width > 0 && rect.height > 0 && style.visibility !== "hidden" && style.display !== "none" && !element.disabled;
    });
  }

  function fieldKind(element) {
    if (element instanceof HTMLTextAreaElement) return "text_area";
    if (element instanceof HTMLSelectElement) return "select";
    if (!(element instanceof HTMLInputElement)) return "unsupported";
    return ["email", "number", "radio", "checkbox", "date"].includes(element.type) ? element.type : "text";
  }

  function describeFocusedElement(element) {
    if (!(element instanceof Element)) return null;
    return {
      elementType: element.tagName.toLowerCase(),
      accessibleName: labelFor(element),
      isEditable: element.matches("input,textarea,select,[contenteditable='true']"),
      isSensitive: isSensitive(element)
    };
  }

  function isSensitive(element) {
    if (element instanceof HTMLInputElement && element.type === "password") return true;
    const text = [element.id, element.getAttribute("name"), element.getAttribute("autocomplete"), element.getAttribute("aria-label"), labelFor(element)].filter(Boolean).join(" ");
    return SENSITIVE_PATTERN.test(text);
  }

  function labelFor(element) {
    const aria = element.getAttribute?.("aria-label");
    if (aria) return cleanText(aria);
    if (element.id) {
      const explicit = document.querySelector(`label[for="${CSS.escape(element.id)}"]`);
      if (explicit) return cleanText(explicit.textContent);
    }
    return cleanText(element.closest?.("label")?.textContent || element.getAttribute?.("placeholder") || element.getAttribute?.("name")) ?? "Unlabeled field";
  }

  function describedByText(element) {
    return (element.getAttribute("aria-describedby") ?? "").split(/\s+/).map(id => document.getElementById(id)?.textContent ?? "").join(" ");
  }

  function currentValue(element) {
    if (element instanceof HTMLInputElement && ["checkbox", "radio"].includes(element.type)) return String(element.checked);
    return "value" in element ? String(element.value ?? "") : null;
  }

  function stableId(element, index) {
    const descriptor = [element.tagName, element.id, element.getAttribute("name"), element.getAttribute("type"), element.getAttribute("role"), labelFor(element), index].join("|");
    return `target-${fingerprint(descriptor)}`;
  }

  function fingerprint(text) {
    let hash = 2166136261;
    for (let index = 0; index < text.length; index += 1) {
      hash ^= text.charCodeAt(index);
      hash = Math.imul(hash, 16777619);
    }
    return (hash >>> 0).toString(16).padStart(8, "0");
  }

  function tabIdentity(provided) {
    return {
      tabId: Number(provided?.tabId ?? -1),
      navigationGeneration: Math.floor(performance.timeOrigin),
      urlOrigin: location.origin,
      isActive: document.visibilityState === "visible",
      isTopFrame: globalThis.top === globalThis
    };
  }

  function sameTab(tab) {
    return tab.urlOrigin === location.origin && tab.isActive !== false && globalThis.top === globalThis;
  }

  function setNativeValue(element, value) {
    const prototype = element instanceof HTMLTextAreaElement ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
    Object.getOwnPropertyDescriptor(prototype, "value").set.call(element, value);
    element.dispatchEvent(new Event("input", { bubbles: true }));
    element.dispatchEvent(new Event("change", { bubbles: true }));
  }

  function verify(stepId, verified, observedValue) {
    return { stepId, executed: true, verified, observedValue, safeFailureCode: verified ? null : "postcondition_failed", safeMessage: verified ? null : "The page did not retain the requested value." };
  }

  function failed(stepId, code, message) {
    return { stepId: stepId ?? "unknown", executed: false, verified: false, observedValue: null, safeFailureCode: code, safeMessage: message };
  }

  function cleanText(value) {
    const text = String(value ?? "").replace(/\s+/g, " ").trim();
    return text ? text.slice(0, 500) : null;
  }

  function clamp(value, minimum, maximum, fallback) {
    const number = Number(value);
    return Number.isFinite(number) ? Math.min(maximum, Math.max(minimum, Math.trunc(number))) : fallback;
  }

  function safeError(error) {
    return error instanceof Error ? error.message.slice(0, 300) : "The page request failed.";
  }
})();
