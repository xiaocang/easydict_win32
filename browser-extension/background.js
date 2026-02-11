// Easydict OCR Translate — Browser Extension Background Script
// Works with both Manifest V3 (Chrome) and V2 (Firefox).
//
// Adds a single right-click context menu item "OCR 截图翻译".
// On click, opens the easydict://ocr-translate protocol URL.
// The OS routes it to the Easydict MSIX app, which triggers screen capture + OCR.
//
// First-time behavior: the browser will show a confirmation dialog
// ("Allow this site to open the easydict app?"). After the user clicks Allow,
// subsequent clicks work silently.

const PROTOCOL_URL = "easydict://ocr-translate";

// Manifest V3 uses chrome.runtime.onInstalled; V2 fires it too.
// Both APIs share the same chrome.contextMenus namespace.
chrome.runtime.onInstalled.addListener(() => {
  chrome.contextMenus.create({
    id: "easydict-ocr-translate",
    title: "OCR 截图翻译",
    contexts: ["all"],
  });
});

chrome.contextMenus.onClicked.addListener((info, tab) => {
  if (info.menuItemId !== "easydict-ocr-translate") return;

  // Open the protocol URL in a new tab. The browser intercepts the custom
  // protocol and hands it to the OS, which activates the MSIX app.
  // The tab auto-navigates to about:blank or shows a brief prompt.
  chrome.tabs.create({ url: PROTOCOL_URL, active: false }, (newTab) => {
    // Close the helper tab after a short delay so the user doesn't see it.
    if (newTab?.id) {
      setTimeout(() => {
        chrome.tabs.remove(newTab.id).catch(() => {});
      }, 1000);
    }
  });
});
