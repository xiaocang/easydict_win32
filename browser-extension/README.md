# Easydict OCR Translate — Browser Extension

Adds "Easydict OCR 截图翻译" to the browser right-click context menu.
Clicking it triggers screen capture + OCR translation in the Easydict desktop app.

## How it works

1. User right-clicks anywhere in the browser → selects "Easydict OCR 截图翻译"
2. Extension triggers Easydict via one of two channels:
   - **Native Messaging** (preferred): sends message directly to bridge exe → instant, no UI flash
   - **Protocol fallback**: opens `easydict://ocr-translate` → OS routes to Easydict desktop app
3. Easydict enters screen capture mode → user selects region → OCR + translate

## Two integration modes

### Protocol mode (zero setup)

Works after installing the extension + Easydict desktop app.
Protocol registration is available in both modes:
- MSIX: registered by package manifest
- Inno/unpackaged: registered by installer and self-repaired at app startup
First-time: browser shows a confirmation dialog ("Allow this site to open the easydict app?").
After clicking Allow, subsequent clicks work silently.

### Native Messaging mode (enhanced, recommended)

After installing the extension, click **浏览器支持 → 安装 Chrome/Firefox 支持** in the
Easydict system tray menu. This deploys a native bridge and registers it with the browser.
No protocol dialog, instant response, no tab flash.

## Installation

### Chrome / Edge (Manifest V3)

1. Open `chrome://extensions/` (or `edge://extensions/`)
2. Enable **Developer mode** (top-right toggle)
3. Click **Load unpacked**
4. Select this `browser-extension/` folder

Uses `manifest.json` (Manifest V3).

### Firefox (Manifest V2)

1. Copy `manifest.v2.json` → `manifest.json` (overwrite the V3 version)
2. Open `about:debugging#/runtime/this-firefox`
3. Click **Load Temporary Add-on**
4. Select the `manifest.json` file

For permanent install, package as `.xpi`:
```bash
cd browser-extension
zip -r easydict-ocr.xpi manifest.json background.js icons/
```

## Prerequisites

- **Easydict for Windows** must be installed (MSIX or Inno installer)
- The `easydict://` protocol must be registered (MSIX auto-registers; Inno registers and app startup self-repairs if needed)
- For Native Messaging: install browser support via tray menu (optional but recommended)

## Files

| File | Description |
|------|-------------|
| `manifest.json` | Chrome/Edge Manifest V3 |
| `manifest.v2.json` | Firefox Manifest V2 (copy to `manifest.json` for Firefox) |
| `background.js` | Shared background script — tries Native Messaging, falls back to protocol |
| `icons/` | Extension icons (16, 48, 128 px) |
