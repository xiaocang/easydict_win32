// Easydict OCR Translate — Browser Extension Background Script
// Works with both Manifest V3 (Chrome) and V2 (Firefox).
//
// Adds a right-click context menu item (localized via _locales).
// On click, triggers OCR screen capture in the Easydict desktop app
// via Native Messaging. Requires native host installed via tray menu.

const NATIVE_HOST_NAME = "com.easydict.rs.bridge";
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
  sendNativeMessage({ action: "ocr-translate" }, (response, error) => {
    if (error) {
      console.error("[Easydict] Native messaging unavailable:", error?.message || error);
      openSetupPage("not-installed");
    } else if (!response?.success) {
      console.error("[Easydict] App not running or bridge error, response:", response);
      openSetupPage("not-running");
    }
  });
}

function sendNativeMessage(message, callback) {
  try {
    chrome.runtime.sendNativeMessage(NATIVE_HOST_NAME, message, (response) =>
      callback(response, chrome.runtime.lastError)
    );
  } catch (error) {
    callback(undefined, error);
  }
}
