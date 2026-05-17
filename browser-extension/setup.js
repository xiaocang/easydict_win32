// Easydict Setup Page — hash-based section switching, i18n, and retry logic.

const NATIVE_HOST_NAME = "com.easydict.bridge";

// Apply i18n strings from _locales
function applyLocalization() {
  document.querySelectorAll("[data-i18n]").forEach((el) => {
    const key = el.getAttribute("data-i18n");
    const msg = chrome.i18n.getMessage(key);
    if (msg) {
      if (el.tagName === "TITLE") {
        document.title = msg;
      } else {
        el.textContent = msg;
      }
    }
  });
}

// Show the correct section based on URL hash
function showSection() {
  const hash = location.hash.replace("#", "") || "not-installed";
  document.querySelectorAll(".section").forEach((s) => s.classList.remove("active"));
  const target = document.getElementById(`section-${hash}`);
  if (target) {
    target.classList.add("active");
  } else {
    // Default to not-installed
    document.getElementById("section-not-installed")?.classList.add("active");
  }
}

// Show status message next to the retry button
function showStatus(sectionId, type, messageKey) {
  const el = document.getElementById(`status-${sectionId}`);
  if (!el) return;
  el.className = `status ${type}`;
  const msg = chrome.i18n.getMessage(messageKey);
  el.textContent = msg || messageKey;
}

// Retry native messaging and close tab on success
function retry(sectionId) {
  showStatus(sectionId, "", "");

  try {
    chrome.runtime.sendNativeMessage(
      NATIVE_HOST_NAME,
      { action: "ocr-translate" },
      (response) => {
        if (chrome.runtime.lastError) {
          console.error("[Easydict] Retry: native host not found:", chrome.runtime.lastError?.message);
          showStatus(sectionId, "error", "setupRetryFailed");
          return;
        }
        if (response?.success) {
          showStatus(sectionId, "success", "setupRetrySuccess");
          setTimeout(() => window.close(), 1500);
        } else {
          console.error("[Easydict] Retry: app not running or bridge error, response:", response);
          showStatus(sectionId, "error", "setupRetryAppNotRunning");
        }
      }
    );
  } catch (e) {
    console.error("[Easydict] Retry: sendNativeMessage not available:", e);
    showStatus(sectionId, "error", "setupRetryFailed");
  }
}

// --- KISS setup helpers ---

function populateKissSetupFromQuery() {
  // The desktop app may launch us with ?url=…&token=… so we can pre-fill the fields.
  try {
    const params = new URLSearchParams(location.search || "");
    const url = params.get("url");
    const token = params.get("token");
    const endpointInput = document.getElementById("kiss-endpoint");
    const tokenInput = document.getElementById("kiss-token");
    const hookEl = document.getElementById("kiss-hook");
    if (url && endpointInput) endpointInput.value = url;
    if (token && tokenInput) tokenInput.value = token;
    if (hookEl && url) {
      // Rewrite the model placeholder if a model query param is supplied; otherwise leave default.
      const model = params.get("model") || "easydict-openai";
      hookEl.value = hookEl.value.replace(/"easydict-openai"/, `"${model}"`);
    }
  } catch (e) {
    console.warn("[Easydict] kiss-setup query parse failed:", e);
  }
}

function wireKissSetupButtons() {
  document.querySelectorAll(".copy-btn").forEach((btn) => {
    btn.addEventListener("click", async () => {
      const id = btn.getAttribute("data-target");
      if (!id) return;
      const el = document.getElementById(id);
      if (!el) return;
      const value = "value" in el ? el.value : el.textContent || "";
      if (!value) return;
      try {
        await navigator.clipboard.writeText(value);
        const original = btn.textContent;
        const copiedMsg = chrome.i18n.getMessage("kissSetupCopied") || "Copied!";
        btn.textContent = copiedMsg;
        setTimeout(() => { btn.textContent = original; }, 1200);
      } catch (e) {
        console.error("[Easydict] clipboard write failed:", e);
      }
    });
  });

  document.getElementById("open-easydict-settings")?.addEventListener("click", () => {
    try {
      // easydict://settings/local-api is registered by the desktop app's protocol handler.
      // Use chrome.tabs to launch via a redirector URL so the system handles the protocol.
      window.location.href = "easydict://settings/local-api";
    } catch (e) {
      console.error("[Easydict] launch easydict:// failed:", e);
    }
  });
}

// Initialize
applyLocalization();
showSection();
window.addEventListener("hashchange", showSection);
populateKissSetupFromQuery();
wireKissSetupButtons();

document.getElementById("retry-btn-install")?.addEventListener("click", () => retry("install"));
document.getElementById("retry-btn-running")?.addEventListener("click", () => retry("running"));
