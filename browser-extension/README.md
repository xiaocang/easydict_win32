# Easydict OCR Translate — Browser Extension

Adds "OCR 截图翻译" to the browser right-click context menu.
Clicking it triggers screen capture + OCR translation in the Easydict desktop app.

## How it works

1. User right-clicks anywhere in the browser → selects "OCR 截图翻译"
2. Extension opens `easydict://ocr-translate` protocol URL
3. Windows routes the protocol to the Easydict MSIX app
4. Easydict enters screen capture mode → user selects region → OCR + translate

**First-time note**: The browser will show a confirmation dialog
("Allow this site to open the easydict app?"). Click **Allow** —
subsequent clicks will work silently.

## Installation

### Chrome (Manifest V3)

1. Open `chrome://extensions/`
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

### Edge

Same as Chrome — Edge supports Manifest V3 extensions natively.

## Prerequisites

- **Easydict for Windows** must be installed (MSIX from Microsoft Store or sideload)
- The `easydict://` protocol is registered automatically by the MSIX package

## Files

| File | Description |
|------|-------------|
| `manifest.json` | Chrome/Edge Manifest V3 |
| `manifest.v2.json` | Firefox Manifest V2 (copy to `manifest.json` for Firefox) |
| `background.js` | Shared background script (works with both V2 and V3) |
| `icons/` | Extension icons (16, 48, 128 px) |
