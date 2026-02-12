# Easydict OCR Translate — Browser Extension

Adds "Easydict OCR 截图翻译" to the browser right-click context menu.
Clicking it triggers screen capture + OCR translation in the Easydict desktop app.

## How it works

1. User right-clicks anywhere in the browser → selects "Easydict OCR 截图翻译"
2. Extension sends a Native Messaging request to the Easydict bridge exe
3. Easydict enters screen capture mode → user selects region → OCR + translate

Native Messaging requires the native host to be installed. In the Easydict system tray menu,
click **浏览器支持 → 安装 Chrome/Firefox 支持** to deploy the native bridge and register it
with the browser.

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
- **Native host** must be installed via tray menu: 浏览器支持 → 安装 Chrome/Firefox 支持

## Files

| File | Description |
|------|-------------|
| `manifest.json` | Chrome/Edge Manifest V3 |
| `manifest.v2.json` | Firefox Manifest V2 (copy to `manifest.json` for Firefox) |
| `background.js` | Background script — triggers OCR via Native Messaging |
| `icons/` | Extension icons (16, 48, 128 px) |
