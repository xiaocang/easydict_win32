// Easydict OCR Translate — Browser Extension Background Script
// Works with both Manifest V3 (Chrome) and V2 (Firefox).
//
// Adds a single right-click context menu item (localized via _locales).
// On click, triggers OCR screen capture in the Easydict desktop app.
//
// Two communication channels (tried in order):
//   1. Native Messaging — if the user installed native host via tray menu,
//      sends a message directly to the bridge exe (instant, no UI flash).
//   2. Protocol fallback — opens easydict://ocr-translate in a temp tab.
//      First time: browser shows a confirmation dialog. Subsequent: silent.

const NATIVE_HOST_NAME = "com.easydict.bridge";
const PROTOCOL_URL = "easydict://ocr-translate";

chrome.runtime.onInstalled.addListener(() => {
  chrome.contextMenus.create({
    id: "easydict-ocr-translate",
    title: chrome.i18n.getMessage("contextMenuTitle"),
    contexts: ["all"],
  });
});

chrome.contextMenus.onClicked.addListener((info, _tab) => {
  if (info.menuItemId !== "easydict-ocr-translate") return;
  triggerOcrTranslate();
});

function triggerOcrTranslate() {
  // Try Native Messaging first (preferred — no UI flash, no permission dialog)
  try {
    chrome.runtime.sendNativeMessage(
      NATIVE_HOST_NAME,
      { action: "ocr-translate" },
      (response) => {
        if (chrome.runtime.lastError || !response?.success) {
          // Native host not installed or Easydict not running — fall back to protocol
          console.log(
            "[Easydict] Native messaging unavailable, falling back to protocol:",
            chrome.runtime.lastError?.message
          );
          triggerViaProtocol();
        }
      }
    );
  } catch {
    // sendNativeMessage not available (e.g., no nativeMessaging permission) — use protocol
    triggerViaProtocol();
  }
}

function triggerViaProtocol() {
  // Open the protocol URL in a new background tab.
  // The browser hands the easydict:// URL to the OS, which activates the MSIX app.
  chrome.tabs.create({ url: PROTOCOL_URL, active: false }, (newTab) => {
    // Close the helper tab after a short delay so the user doesn't see it linger.
    if (newTab?.id) {
      setTimeout(() => {
        // chrome.tabs.remove returns a Promise in MV3 (Chrome) but undefined in MV2 (Firefox).
        // Guard against calling .catch() on undefined to avoid TypeError in Firefox.
        try {
          var result = chrome.tabs.remove(newTab.id);
          if (result && result.catch) result.catch(() => {});
        } catch {
          // Ignore tab removal errors
        }
      }, 1000);
    }
  });
}
