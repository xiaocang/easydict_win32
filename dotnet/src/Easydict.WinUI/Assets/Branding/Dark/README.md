# Dark icon assets

Generated from `../AppIconSource.png`, preserving the complete artwork and transparent background, with a light outline for dark surfaces.
The ICO contains 16, 24, 32, 48, 64, 128 and 256 pixel PNG frames. Runtime code selects this file directly; it does not convert HICONs or rasterize the artwork.

Regenerate from the repository root:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File dotnet/scripts/generate-app-icon-ico.ps1 -SourcePng dotnet/src/Easydict.WinUI/Assets/Branding/AppIconSource.png -OutputIco dotnet/src/Easydict.WinUI/Assets/Branding/Dark/AppIcon.ico -OutputTrayPng dotnet/src/Easydict.WinUI/Assets/Branding/Dark/TrayIcon.png -OutputPngDirectory dotnet/src/Easydict.WinUI/Assets/Branding/Dark -DarkMode
```

Rendering uses explicit source and destination pixel rectangles so source DPI metadata and desktop scaling cannot crop the image.
