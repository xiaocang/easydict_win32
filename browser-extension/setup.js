// Easydict Setup Page — hash-based section switching, i18n, and retry logic.

const RUST_NATIVE_HOST_NAME = "com.easydict.rs.bridge";
const LEGACY_NATIVE_HOST_NAME = "com.easydict.bridge";
const NATIVE_HOST_NAMES = [RUST_NATIVE_HOST_NAME, LEGACY_NATIVE_HOST_NAME];

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

  sendNativeMessageWithFallback({ action: "ocr-translate" }, (response, error) => {
    if (error) {
      console.error("[Easydict] Retry: native host not found:", error?.message || error);
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
  });
}

function sendNativeMessageWithFallback(message, callback) {
  sendNativeMessageToHost(0, message, callback);
}

function sendNativeMessageToHost(index, message, callback) {
  const hostName = NATIVE_HOST_NAMES[index];
  if (!hostName) {
    callback(undefined, new Error("No Easydict native messaging host configured"));
    return;
  }

  try {
    chrome.runtime.sendNativeMessage(hostName, message, (response) => {
      const error = chrome.runtime.lastError;
      if (error && index + 1 < NATIVE_HOST_NAMES.length) {
        sendNativeMessageToHost(index + 1, message, callback);
        return;
      }

      callback(response, error);
    });
  } catch (error) {
    if (index + 1 < NATIVE_HOST_NAMES.length) {
      sendNativeMessageToHost(index + 1, message, callback);
      return;
    }

    callback(undefined, error);
  }
}

// Initialize
applyLocalization();
showSection();
window.addEventListener("hashchange", showSection);

document.getElementById("retry-btn-install")?.addEventListener("click", () => retry("install"));
document.getElementById("retry-btn-running")?.addEventListener("click", () => retry("running"));
