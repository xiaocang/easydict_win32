// Easydict OCR Translate — Browser Extension Background Script
// Works with both Manifest V3 (Chrome) and V2 (Firefox).
//
// Adds a right-click context menu item (localized via _locales).
// On click, triggers OCR screen capture in the Easydict desktop app
// via Native Messaging. Requires native host installed via tray menu.

const NATIVE_HOST_NAME = "com.easydict.bridge";
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
  try {
    chrome.runtime.sendNativeMessage(
      NATIVE_HOST_NAME,
      { action: "ocr-translate" },
      (response) => {
        if (chrome.runtime.lastError) {
          console.error(
            "[Easydict] Native messaging unavailable:",
            chrome.runtime.lastError?.message
          );
          openSetupPage("not-installed");
        } else if (!response?.success) {
          console.error("[Easydict] App not running or bridge error, response:", response);
          openSetupPage("not-running");
        }
      }
    );
  } catch {
    console.error("[Easydict] sendNativeMessage not available");
    openSetupPage("not-installed");
  }
}
