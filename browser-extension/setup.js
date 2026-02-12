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
          showStatus(sectionId, "error", "setupRetryFailed");
          return;
        }
        if (response?.success) {
          showStatus(sectionId, "success", "setupRetrySuccess");
          setTimeout(() => window.close(), 1500);
        } else {
          showStatus(sectionId, "error", "setupRetryAppNotRunning");
        }
      }
    );
  } catch {
    showStatus(sectionId, "error", "setupRetryFailed");
  }
}

// Initialize
applyLocalization();
showSection();
window.addEventListener("hashchange", showSection);

document.getElementById("retry-btn-install")?.addEventListener("click", () => retry("install"));
document.getElementById("retry-btn-running")?.addEventListener("click", () => retry("running"));
