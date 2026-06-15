// Easydict OCR Translate — Browser Extension Background Script
// Works with both Manifest V3 (Chrome) and V2 (Firefox).
//
// Adds a right-click context menu item (localized via _locales).
// On click, triggers OCR screen capture in the Easydict desktop app
// via Native Messaging. Requires native host installed via tray menu.

const RUST_NATIVE_HOST_NAME = "com.easydict.rs.bridge";
const LEGACY_NATIVE_HOST_NAME = "com.easydict.bridge";
const NATIVE_HOST_NAMES = [RUST_NATIVE_HOST_NAME, LEGACY_NATIVE_HOST_NAME];
const MENU_OCR = "easydict-ocr-translate";
const SETUP_RATE_LIMIT_MS = 10_000;

let lastSetupOpenedAt = 0;

chrome.runtime.onInstalled.addListener(() => {
  chrome.contextMenus.create({
    id: MENU_OCR,
    title: chrome.i18n.getMessage("contextMenuTitle"),
    contexts: ["all"],
  });
});

chrome.contextMenus.onClicked.addListener((info, _tab) => {
  if (info.menuItemId === MENU_OCR) {
    triggerOcrTranslate();
  }
});

function openSetupPage(hash) {
  const now = Date.now();
  if (now - lastSetupOpenedAt < SETUP_RATE_LIMIT_MS) return;
  lastSetupOpenedAt = now;
  chrome.tabs.create({ url: chrome.runtime.getURL(`setup.html#${hash}`) });
}

function triggerOcrTranslate() {
  sendNativeMessageWithFallback({ action: "ocr-translate" }, (response, error) => {
    if (error) {
      console.error("[Easydict] Native messaging unavailable:", error?.message || error);
      openSetupPage("not-installed");
    } else if (!response?.success) {
      console.error("[Easydict] App not running or bridge error, response:", response);
      openSetupPage("not-running");
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
