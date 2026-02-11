using System.Diagnostics;
using System.Runtime.InteropServices;
using Easydict.WinUI.Models;

namespace Easydict.WinUI.Services.ScreenCapture;

/// <summary>
/// Win32 native overlay window for Snipaste-style screenshot region selection.
/// Uses GDI+ for rendering to avoid WinUI 3 window creation latency.
///
/// Interaction flow (Snipaste-style):
///   1. Freeze desktop with BitBlt → show full-screen overlay with dark mask
///   2. Mouse hover: auto-detect windows via WindowDetector, highlight region
///      (desktop/wallpaper windows are excluded to avoid full-screen selection)
///   3. Double-click on detected window: select it → Adjusting phase
///      Single-click on detected window: no action (double-click required)
///      Click+drag (anywhere): free-select rectangle (drag threshold = 5px)
///      Double-click on blank: enter track-mouse selection mode (click to finalize)
///   4. After selection (Adjusting): 8 resize handles, arrow-key fine-tuning,
///      cursor changes (move/resize/crosshair) based on position
///   5. Enter/double-click: confirm → return result
///      Right-click/Esc in Adjusting/Selecting: go back to Detecting
///      Right-click/Esc in Detecting: confirmation dialog → cancel
///   Tips overlay: each phase shows context-sensitive operation hints (localized)
///
/// All operations are non-blocking. The window runs its own message loop on
/// the calling thread and returns when the user confirms or cancels.
/// </summary>
public sealed class ScreenCaptureWindow : IDisposable
{
    // Window class and style constants
    private const string WindowClassName = "EasydictScreenCapture";
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_VISIBLE = 0x10000000;

    // Messages
    private const int WM_PAINT = 0x000F;
    private const int WM_ERASEBKGND = 0x0014;
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_MOUSEWHEEL = 0x020A;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_DESTROY = 0x0002;
    private const int WM_SETCURSOR = 0x0020;

    // Virtual keys
    private const int VK_ESCAPE = 0x1B;
    private const int VK_RETURN = 0x0D;
    private const int VK_LEFT = 0x25;
    private const int VK_UP = 0x26;
    private const int VK_RIGHT = 0x27;
    private const int VK_DOWN = 0x28;
    private const int VK_CONTROL = 0x11;
    private const int VK_SHIFT = 0x10;
    private const int VK_TAB = 0x09;

    // GDI constants
    private const int SRCCOPY = 0x00CC0020;
    private const uint SM_XVIRTUALSCREEN = 76;
    private const uint SM_YVIRTUALSCREEN = 77;
    private const uint SM_CXVIRTUALSCREEN = 78;
    private const uint SM_CYVIRTUALSCREEN = 79;

    // Overlay mask alpha (0-255). ~100 = ~40% opacity dark overlay.
    private const byte MaskAlpha = 100;

    // Selection handle size in pixels
    private const int HandleSize = 8;

    // Magnifier size (pixels captured around cursor, then scaled up)
    private const int MagSourceSize = 11; // 11×11 pixels
    private const int MagScale = 8;       // 8× zoom
    private const int MagDisplaySize = MagSourceSize * MagScale; // 88×88

    private nint _hwnd;
    private nint _desktopBitmapHandle;
    private nint _desktopDcHandle;
    private nint _oldDesktopBitmapHandle; // saved from SelectObject to restore before cleanup
    private int _desktopWidth;
    private int _desktopHeight;
    private int _virtualLeft;
    private int _virtualTop;

    private readonly WindowDetector _windowDetector = new();
    private WndProcDelegate? _wndProc; // prevent GC

    // Interaction state
    private SelectionPhase _phase = SelectionPhase.Detecting;
    private RECT? _detectedRegion;   // auto-detected window region
    private RECT _selection;          // current selection rectangle
    private POINT _dragStart;         // mouse-down position
    private bool _isDragging;
    private DragMode _dragMode = DragMode.None;
    private int _detectionDepth;      // scroll depth for window detection
    private bool _isMouseDown;        // mouse button held in Detecting phase
    private POINT _mouseDownPoint;    // where mouse went down (for drag threshold)
    private bool _ignoreNextMouseUp;  // skip mouse-up after double-click starts Selecting
    private bool _isDragSelecting;    // true = entered Selecting via click+drag (capture held)

    // Drag threshold in pixels — must move beyond this to start free-form selection
    private const int DragThreshold = 5;

    // Result
    private TaskCompletionSource<ScreenCaptureResult?>? _resultTcs;
    private bool _disposed;

    // Tips rendering
    private nint _tipsFont;
    private string _tipDetecting = string.Empty;
    private string _tipSelecting = string.Empty;
    private string _tipAdjusting = string.Empty;

    private enum SelectionPhase
    {
        Detecting,   // Mouse hovering, auto-detecting windows
        Selecting,   // Mouse dragging to create selection
        Adjusting,   // Selection made, can resize/move/confirm
    }

    private enum DragMode
    {
        None,
        Move,           // Dragging inside selection
        ResizeTopLeft, ResizeTop, ResizeTopRight,
        ResizeLeft, ResizeRight,
        ResizeBottomLeft, ResizeBottom, ResizeBottomRight,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
        public readonly bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public POINT pt;
    }

    /// <summary>
    /// Shows the capture overlay and waits for the user to select a region.
    /// Returns null if the user cancels. This runs a message loop and should be
    /// called from a thread that can pump messages (typically a dedicated STA thread
    /// so the UI thread is NOT blocked).
    /// </summary>
    public Task<ScreenCaptureResult?> CaptureAsync()
    {
        _resultTcs = new TaskCompletionSource<ScreenCaptureResult?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        // Run the capture on a dedicated STA thread so the main UI thread stays responsive
        var thread = new Thread(RunCaptureLoop)
        {
            IsBackground = true,
            Name = "ScreenCaptureThread"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        return _resultTcs.Task;
    }

    private void RunCaptureLoop()
    {
        try
        {
            CaptureDesktop();
            CreateOverlayWindow();
            _windowDetector.TakeSnapshot(_hwnd);
            InitializeTips();

            // Win32 message loop
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ScreenCapture] Error in capture loop: {ex.Message}");
            _resultTcs?.TrySetResult(null);
        }
        finally
        {
            Cleanup();
        }
    }

    private void CaptureDesktop()
    {
        _virtualLeft = GetSystemMetrics(SM_XVIRTUALSCREEN);
        _virtualTop = GetSystemMetrics(SM_YVIRTUALSCREEN);
        _desktopWidth = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        _desktopHeight = GetSystemMetrics(SM_CYVIRTUALSCREEN);

        var screenDc = GetDC(IntPtr.Zero);
        _desktopDcHandle = CreateCompatibleDC(screenDc);
        _desktopBitmapHandle = CreateCompatibleBitmap(screenDc, _desktopWidth, _desktopHeight);
        _oldDesktopBitmapHandle = SelectObject(_desktopDcHandle, _desktopBitmapHandle);

        BitBlt(_desktopDcHandle, 0, 0, _desktopWidth, _desktopHeight,
               screenDc, _virtualLeft, _virtualTop, SRCCOPY);
        ReleaseDC(IntPtr.Zero, screenDc);

        Debug.WriteLine($"[ScreenCapture] Desktop captured: {_desktopWidth}×{_desktopHeight} at ({_virtualLeft},{_virtualTop})");
    }

    private void CreateOverlayWindow()
    {
        _wndProc = WndProc;

        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandle(null),
            lpszClassName = WindowClassName,
            style = 0x0008, // CS_DBLCLKS
            hCursor = LoadCursor(IntPtr.Zero, 32515), // IDC_CROSS
        };

        RegisterClassEx(ref wc);

        _hwnd = CreateWindowEx(
            WS_EX_TOPMOST | WS_EX_TOOLWINDOW,
            WindowClassName,
            "Easydict Screen Capture",
            WS_POPUP | WS_VISIBLE,
            _virtualLeft, _virtualTop, _desktopWidth, _desktopHeight,
            IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);

        SetForegroundWindow(_hwnd);
        SetFocus(_hwnd);
    }

    private nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case WM_PAINT:
                OnPaint(hwnd);
                return IntPtr.Zero;

            case WM_ERASEBKGND:
                return (nint)1; // We handle all painting

            case WM_MOUSEMOVE:
                OnMouseMove(GetLParamPoint(lParam));
                return IntPtr.Zero;

            case WM_LBUTTONDOWN:
                OnLeftButtonDown(GetLParamPoint(lParam));
                return IntPtr.Zero;

            case WM_LBUTTONUP:
                OnLeftButtonUp(GetLParamPoint(lParam));
                return IntPtr.Zero;

            case WM_LBUTTONDBLCLK:
                if (_phase == SelectionPhase.Detecting)
                {
                    if (_detectedRegion.HasValue)
                    {
                        // Double-click on detected window: select it → Adjusting phase
                        _selection = _detectedRegion.Value;
                        _phase = SelectionPhase.Adjusting;
                        _isMouseDown = false;
                        _ignoreNextMouseUp = true;
                        InvalidateRect(_hwnd, IntPtr.Zero, false);
                    }
                    else
                    {
                        // Double-click on blank: enter track-mouse selection mode
                        var dblPt = GetLParamPoint(lParam);
                        _selection = new RECT
                        {
                            Left = dblPt.X, Top = dblPt.Y,
                            Right = dblPt.X, Bottom = dblPt.Y
                        };
                        _phase = SelectionPhase.Selecting;
                        _isDragSelecting = false; // track-mouse mode (no capture)
                        _isMouseDown = false;
                        _ignoreNextMouseUp = true;
                        _detectedRegion = null;
                        InvalidateRect(_hwnd, IntPtr.Zero, false);
                    }
                }
                else if (_phase == SelectionPhase.Adjusting)
                {
                    ConfirmSelection();
                }
                return IntPtr.Zero;

            case WM_RBUTTONDOWN:
                if (_phase == SelectionPhase.Adjusting)
                {
                    // Go back to Detecting (Snipaste: right-click = step back)
                    _phase = SelectionPhase.Detecting;
                    _detectedRegion = null;
                    _detectionDepth = 0;
                    InvalidateRect(_hwnd, IntPtr.Zero, false);
                }
                else if (_phase == SelectionPhase.Selecting)
                {
                    // Cancel current drag, back to Detecting
                    ReleaseCapture();
                    _isDragSelecting = false;
                    _isMouseDown = false;
                    _phase = SelectionPhase.Detecting;
                    _detectedRegion = null;
                    InvalidateRect(_hwnd, IntPtr.Zero, false);
                }
                else
                {
                    // Detecting phase: exit with confirmation
                    RequestCancelWithConfirmation();
                }
                return IntPtr.Zero;

            case WM_MOUSEWHEEL:
                // WM_MOUSEWHEEL lParam contains screen coordinates, not client coordinates.
                // Convert to client-relative (overlay-relative) by subtracting virtual desktop origin.
                var wheelScreenPt = GetLParamPoint(lParam);
                var wheelClientPt = new POINT
                {
                    X = wheelScreenPt.X - _virtualLeft,
                    Y = wheelScreenPt.Y - _virtualTop
                };
                OnMouseWheel(GetWheelDelta(wParam), wheelClientPt);
                return IntPtr.Zero;

            case WM_KEYDOWN:
                OnKeyDown((int)wParam);
                return IntPtr.Zero;

            case WM_SETCURSOR:
                if (UpdateCursor())
                    return (nint)1; // We handled the cursor
                return DefWindowProc(hwnd, msg, wParam, lParam);

            case WM_DESTROY:
                PostQuitMessage(0);
                return IntPtr.Zero;

            default:
                return DefWindowProc(hwnd, msg, wParam, lParam);
        }
    }

    private void OnPaint(nint hwnd)
    {
        var ps = new PAINTSTRUCT();
        var hdc = BeginPaint(hwnd, ref ps);

        // 1. Draw frozen desktop as background
        BitBlt(hdc, 0, 0, _desktopWidth, _desktopHeight, _desktopDcHandle, 0, 0, SRCCOPY);

        // 2. Draw semi-transparent dark overlay (using GDI alpha blend)
        DrawDarkOverlay(hdc);

        // 3. Draw selection region (clear area + border)
        if (_phase == SelectionPhase.Detecting && _detectedRegion.HasValue)
        {
            DrawSelectionRegion(hdc, _detectedRegion.Value);
        }
        else if (_phase is SelectionPhase.Selecting or SelectionPhase.Adjusting)
        {
            var sel = NormalizeRect(_selection);
            DrawSelectionRegion(hdc, sel);

            if (_phase == SelectionPhase.Adjusting)
            {
                DrawResizeHandles(hdc, sel);
            }

            DrawSizeLabel(hdc, sel);
        }

        // 4. Draw magnifier (always visible when detecting or selecting)
        if (_phase is SelectionPhase.Detecting or SelectionPhase.Selecting)
        {
            GetCursorPos(out var cursorPos);
            var localX = cursorPos.X - _virtualLeft;
            var localY = cursorPos.Y - _virtualTop;
            DrawMagnifier(hdc, localX, localY);
        }

        // 5. Draw operation tips overlay (always on top of everything)
        DrawTips(hdc);

        EndPaint(hwnd, ref ps);
    }

    private void DrawDarkOverlay(nint hdc)
    {
        // Create a temporary DC with a 1×1 black bitmap, then AlphaBlend it
        var memDc = CreateCompatibleDC(hdc);
        var bmp = CreateCompatibleBitmap(hdc, 1, 1);
        var old = SelectObject(memDc, bmp);

        // Fill with black
        var blackBrush = GetStockObject(4); // BLACK_BRUSH
        var rc = new RECT { Left = 0, Top = 0, Right = 1, Bottom = 1 };
        FillRect(memDc, ref rc, blackBrush);

        var blend = new BLENDFUNCTION
        {
            BlendOp = 0,              // AC_SRC_OVER
            BlendFlags = 0,
            SourceConstantAlpha = MaskAlpha,
            AlphaFormat = 0
        };
        AlphaBlend(hdc, 0, 0, _desktopWidth, _desktopHeight, memDc, 0, 0, 1, 1, blend);

        SelectObject(memDc, old);
        DeleteObject(bmp);
        DeleteDC(memDc);
    }

    private void DrawSelectionRegion(nint hdc, RECT sel)
    {
        // Clear the overlay in the selection area by re-drawing the desktop bitmap there
        BitBlt(hdc, sel.Left, sel.Top, sel.Width, sel.Height,
               _desktopDcHandle, sel.Left, sel.Top, SRCCOPY);

        // Draw border
        var pen = CreatePen(0, 2, 0x00FF8C00); // RGB orange-ish (reversed for GDI: 0x00BBGGRR)
        var oldPen = SelectObject(hdc, pen);
        var oldBrush = SelectObject(hdc, GetStockObject(5)); // HOLLOW_BRUSH

        Rectangle(hdc, sel.Left, sel.Top, sel.Right, sel.Bottom);

        SelectObject(hdc, oldPen);
        SelectObject(hdc, oldBrush);
        DeleteObject(pen);
    }

    private void DrawResizeHandles(nint hdc, RECT sel)
    {
        var brush = CreateSolidBrush(0x00FF8C00); // Orange handles
        var handles = GetHandleRects(sel);
        foreach (var h in handles)
        {
            var r = new RECT { Left = h.Left, Top = h.Top, Right = h.Right, Bottom = h.Bottom };
            FillRect(hdc, ref r, brush);
        }
        DeleteObject(brush);
    }

    private void DrawSizeLabel(nint hdc, RECT sel)
    {
        var text = $"{sel.Width} × {sel.Height}";
        var labelX = sel.Left;
        var labelY = sel.Top > 20 ? sel.Top - 20 : sel.Bottom + 4;

        SetBkMode(hdc, 1); // TRANSPARENT
        SetTextColor(hdc, 0x00FFFFFF); // White text
        TextOut(hdc, labelX, labelY, text, text.Length);
    }

    private void DrawMagnifier(nint hdc, int cx, int cy)
    {
        // Position magnifier to the lower-right of cursor, with screen edge avoidance
        var magX = cx + 20;
        var magY = cy + 20;
        var totalH = MagDisplaySize + 30; // Extra space for coordinate text

        if (magX + MagDisplaySize + 4 > _desktopWidth) magX = cx - MagDisplaySize - 24;
        if (magY + totalH + 4 > _desktopHeight) magY = cy - totalH - 24;

        // Background for magnifier panel
        var bgBrush = CreateSolidBrush(0x00303030); // Dark gray
        var panelRect = new RECT
        {
            Left = magX - 2, Top = magY - 2,
            Right = magX + MagDisplaySize + 2, Bottom = magY + totalH + 2
        };
        FillRect(hdc, ref panelRect, bgBrush);
        DeleteObject(bgBrush);

        // Stretch source pixels around cursor into magnifier area
        var srcX = Math.Clamp(cx - MagSourceSize / 2, 0, _desktopWidth - MagSourceSize);
        var srcY = Math.Clamp(cy - MagSourceSize / 2, 0, _desktopHeight - MagSourceSize);

        SetStretchBltMode(hdc, 3); // COLORONCOLOR
        StretchBlt(hdc, magX, magY, MagDisplaySize, MagDisplaySize,
                   _desktopDcHandle, srcX, srcY, MagSourceSize, MagSourceSize, SRCCOPY);

        // Draw crosshair in center of magnifier
        var crossPen = CreatePen(0, 1, 0x0000FF00); // Green
        var oldPen = SelectObject(hdc, crossPen);
        var centerX = magX + MagDisplaySize / 2;
        var centerY = magY + MagDisplaySize / 2;
        MoveToEx(hdc, centerX - MagScale, centerY, IntPtr.Zero);
        LineTo(hdc, centerX + MagScale, centerY);
        MoveToEx(hdc, centerX, centerY - MagScale, IntPtr.Zero);
        LineTo(hdc, centerX, centerY + MagScale);
        SelectObject(hdc, oldPen);
        DeleteObject(crossPen);

        // Draw magnifier border
        var borderPen = CreatePen(0, 1, 0x00808080);
        oldPen = SelectObject(hdc, borderPen);
        var oldBrush = SelectObject(hdc, GetStockObject(5)); // HOLLOW_BRUSH
        Rectangle(hdc, magX, magY, magX + MagDisplaySize, magY + MagDisplaySize);
        SelectObject(hdc, oldPen);
        SelectObject(hdc, oldBrush);
        DeleteObject(borderPen);

        // Draw coordinate text below magnifier
        var coordText = $"({cx + _virtualLeft}, {cy + _virtualTop})";
        SetBkMode(hdc, 1); // TRANSPARENT
        SetTextColor(hdc, 0x00FFFFFF);
        TextOut(hdc, magX, magY + MagDisplaySize + 4, coordText, coordText.Length);

        // Get pixel color at cursor and display it
        var pixelColor = GetPixel(_desktopDcHandle, cx, cy);
        if (pixelColor != 0xFFFFFFFF) // CLR_INVALID
        {
            var r = pixelColor & 0xFF;
            var g = (pixelColor >> 8) & 0xFF;
            var b = (pixelColor >> 16) & 0xFF;
            var colorText = $"#{r:X2}{g:X2}{b:X2}";
            TextOut(hdc, magX, magY + MagDisplaySize + 18, colorText, colorText.Length);

            // Color swatch
            var swatchBrush = CreateSolidBrush(pixelColor);
            var swatchRect = new RECT
            {
                Left = magX + 70, Top = magY + MagDisplaySize + 14,
                Right = magX + 88, Bottom = magY + MagDisplaySize + 28
            };
            FillRect(hdc, ref swatchRect, swatchBrush);
            DeleteObject(swatchBrush);
        }
    }

    // --- Tips initialization and rendering ---

    private void InitializeTips()
    {
        // Create a font scaled to display size for readability across DPI settings
        var fontHeight = Math.Max(16, _desktopHeight / 72);
        _tipsFont = CreateFont(
            -fontHeight, 0, 0, 0, 400, // height (negative = character height), weight = FW_NORMAL
            0, 0, 0,                     // italic, underline, strikeout
            1,                           // DEFAULT_CHARSET
            0, 0, 4, 0,                 // out precision, clip precision, ANTIALIASED_QUALITY, pitch
            "Segoe UI");

        // Cache localized tip strings
        var loc = LocalizationService.Instance;
        _tipDetecting = loc.GetString("ScreenCaptureTipDetecting");
        _tipSelecting = loc.GetString("ScreenCaptureTipSelecting");
        _tipAdjusting = loc.GetString("ScreenCaptureTipAdjusting");
    }

    private void DrawTips(nint hdc)
    {
        var text = _phase switch
        {
            SelectionPhase.Detecting => _tipDetecting,
            SelectionPhase.Selecting => _tipSelecting,
            SelectionPhase.Adjusting => _tipAdjusting,
            _ => string.Empty
        };

        if (string.IsNullOrEmpty(text)) return;

        var oldFont = SelectObject(hdc, _tipsFont);

        // Measure text dimensions
        GetTextExtentPoint32(hdc, text, text.Length, out var textSize);

        var padH = 16;
        var padV = 8;
        var panelWidth = textSize.cx + padH * 2;
        var panelHeight = textSize.cy + padV * 2;
        var panelX = (_desktopWidth - panelWidth) / 2;
        var panelY = 20; // 20px from top of screen

        // Draw semi-transparent dark background panel
        var memDc = CreateCompatibleDC(hdc);
        var bmp = CreateCompatibleBitmap(hdc, 1, 1);
        var oldBmp = SelectObject(memDc, bmp);

        var brush = CreateSolidBrush(0x00303030); // Dark gray
        var rc = new RECT { Left = 0, Top = 0, Right = 1, Bottom = 1 };
        FillRect(memDc, ref rc, brush);
        DeleteObject(brush);

        var blend = new BLENDFUNCTION
        {
            BlendOp = 0,              // AC_SRC_OVER
            BlendFlags = 0,
            SourceConstantAlpha = 200, // ~78% opacity
            AlphaFormat = 0
        };
        AlphaBlend(hdc, panelX, panelY, panelWidth, panelHeight, memDc, 0, 0, 1, 1, blend);

        SelectObject(memDc, oldBmp);
        DeleteObject(bmp);
        DeleteDC(memDc);

        // Draw white text centered in the panel
        SetBkMode(hdc, 1); // TRANSPARENT
        SetTextColor(hdc, 0x00FFFFFF);
        TextOut(hdc, panelX + padH, panelY + padV, text, text.Length);

        SelectObject(hdc, oldFont);
    }

    // --- Mouse event handlers ---

    private void OnMouseMove(POINT pt)
    {
        switch (_phase)
        {
            case SelectionPhase.Detecting:
                // Check if mouse-down is held and dragged beyond threshold → start free-form selection
                if (_isMouseDown)
                {
                    var dx = Math.Abs(pt.X - _mouseDownPoint.X);
                    var dy = Math.Abs(pt.Y - _mouseDownPoint.Y);
                    if (dx > DragThreshold || dy > DragThreshold)
                    {
                        _isMouseDown = false;
                        _isDragSelecting = true;
                        _selection = new RECT
                        {
                            Left = _mouseDownPoint.X, Top = _mouseDownPoint.Y,
                            Right = pt.X, Bottom = pt.Y
                        };
                        _phase = SelectionPhase.Selecting;
                        _detectedRegion = null;
                        InvalidateRect(_hwnd, IntPtr.Zero, false);
                        break;
                    }
                }

                // Auto-detect windows under cursor
                var region = _windowDetector.FindRegionAtPoint(
                    pt.X + _virtualLeft, pt.Y + _virtualTop, _detectionDepth);
                if (region.HasValue)
                {
                    var r = region.Value;
                    var newDetected = new RECT
                    {
                        Left = r.Left - _virtualLeft,
                        Top = r.Top - _virtualTop,
                        Right = r.Right - _virtualLeft,
                        Bottom = r.Bottom - _virtualTop
                    };
                    if (!_detectedRegion.HasValue || !RectsEqual(_detectedRegion.Value, newDetected))
                    {
                        _detectedRegion = newDetected;
                        InvalidateRect(_hwnd, IntPtr.Zero, false);
                    }
                }
                else if (_detectedRegion.HasValue)
                {
                    _detectedRegion = null;
                    InvalidateRect(_hwnd, IntPtr.Zero, false);
                }
                else
                {
                    // Repaint for magnifier update
                    InvalidateRect(_hwnd, IntPtr.Zero, false);
                }
                break;

            case SelectionPhase.Selecting:
                _selection.Right = pt.X;
                _selection.Bottom = pt.Y;
                InvalidateRect(_hwnd, IntPtr.Zero, false);
                break;

            case SelectionPhase.Adjusting when _isDragging:
                ApplyDrag(pt);
                InvalidateRect(_hwnd, IntPtr.Zero, false);
                break;
        }
    }

    private void OnLeftButtonDown(POINT pt)
    {
        switch (_phase)
        {
            case SelectionPhase.Detecting:
                // Record mouse-down; actual action deferred until mouse-up (click)
                // or mouse-move (drag beyond threshold)
                _isMouseDown = true;
                _mouseDownPoint = pt;
                SetCapture(_hwnd);
                break;

            case SelectionPhase.Selecting when !_isDragSelecting:
                // Double-click initiated selection: click to finalize
                _selection.Right = pt.X;
                _selection.Bottom = pt.Y;
                _selection = NormalizeRect(_selection);
                if (_selection.Width >= 3 && _selection.Height >= 3)
                {
                    _phase = SelectionPhase.Adjusting;
                }
                else
                {
                    _phase = SelectionPhase.Detecting;
                    _detectedRegion = null;
                }
                InvalidateRect(_hwnd, IntPtr.Zero, false);
                break;

            case SelectionPhase.Adjusting:
                var sel = NormalizeRect(_selection);
                _dragMode = HitTestHandles(sel, pt);
                if (_dragMode != DragMode.None)
                {
                    _isDragging = true;
                    _dragStart = pt;
                    SetCapture(_hwnd);
                }
                else if (sel.Contains(pt.X, pt.Y))
                {
                    _isDragging = true;
                    _dragMode = DragMode.Move;
                    _dragStart = pt;
                    SetCapture(_hwnd);
                }
                else
                {
                    // Click outside selection → restart
                    _phase = SelectionPhase.Detecting;
                    _detectedRegion = null;
                    _detectionDepth = 0;
                    InvalidateRect(_hwnd, IntPtr.Zero, false);
                }
                break;
        }
    }

    private void OnLeftButtonUp(POINT pt)
    {
        // Skip mouse-up that follows a double-click entering Selecting mode
        if (_ignoreNextMouseUp)
        {
            _ignoreNextMouseUp = false;
            return;
        }

        // Click-without-drag in Detecting phase — no action (double-click required to select)
        if (_isMouseDown && _phase == SelectionPhase.Detecting)
        {
            _isMouseDown = false;
            ReleaseCapture();
            return;
        }

        if (_phase == SelectionPhase.Selecting && _isDragSelecting)
        {
            ReleaseCapture();
            _isDragSelecting = false;
            _selection.Right = pt.X;
            _selection.Bottom = pt.Y;
            _selection = NormalizeRect(_selection);

            if (_selection.Width < 3 || _selection.Height < 3)
            {
                // Too small, go back to detecting
                _phase = SelectionPhase.Detecting;
            }
            else
            {
                _phase = SelectionPhase.Adjusting;
            }
            InvalidateRect(_hwnd, IntPtr.Zero, false);
        }
        else if (_isDragging)
        {
            ReleaseCapture();
            _isDragging = false;
            _dragMode = DragMode.None;
            _selection = NormalizeRect(_selection);
            InvalidateRect(_hwnd, IntPtr.Zero, false);
        }
    }

    private void OnMouseWheel(int delta, POINT pt)
    {
        if (_phase != SelectionPhase.Detecting) return;

        var maxDepth = _windowDetector.GetMaxDepthAtPoint(pt.X + _virtualLeft, pt.Y + _virtualTop);
        if (delta > 0)
            _detectionDepth = Math.Max(0, _detectionDepth - 1); // Scroll up → deeper (child)
        else
            _detectionDepth = Math.Min(maxDepth, _detectionDepth + 1); // Scroll down → shallower (parent)

        // Force re-detect at new depth
        _detectedRegion = null;
        OnMouseMove(pt);
    }

    private void OnKeyDown(int vk)
    {
        switch (vk)
        {
            case VK_ESCAPE:
                if (_phase == SelectionPhase.Adjusting || _phase == SelectionPhase.Selecting)
                {
                    // Step back to Detecting (Snipaste behavior)
                    if (_isDragSelecting) ReleaseCapture();
                    _isDragSelecting = false;
                    _isMouseDown = false;
                    _phase = SelectionPhase.Detecting;
                    _detectedRegion = null;
                    _detectionDepth = 0;
                    InvalidateRect(_hwnd, IntPtr.Zero, false);
                }
                else
                {
                    // In Detecting phase: exit with confirmation
                    RequestCancelWithConfirmation();
                }
                break;

            case VK_RETURN:
                if (_phase == SelectionPhase.Adjusting)
                    ConfirmSelection();
                break;

            case VK_LEFT or VK_RIGHT or VK_UP or VK_DOWN when _phase == SelectionPhase.Adjusting:
                HandleArrowKey(vk);
                break;
        }
    }

    private void HandleArrowKey(int vk)
    {
        var ctrl = (GetKeyState(VK_CONTROL) & 0x8000) != 0;
        var shift = (GetKeyState(VK_SHIFT) & 0x8000) != 0;

        int dx = 0, dy = 0;
        if (vk == VK_LEFT) dx = -1;
        else if (vk == VK_RIGHT) dx = 1;
        else if (vk == VK_UP) dy = -1;
        else if (vk == VK_DOWN) dy = 1;

        if (ctrl)
        {
            // Expand: move the relevant edge outward
            if (dx < 0) _selection.Left += dx;
            else if (dx > 0) _selection.Right += dx;
            if (dy < 0) _selection.Top += dy;
            else if (dy > 0) _selection.Bottom += dy;
        }
        else if (shift)
        {
            // Shrink: move the relevant edge inward
            if (dx < 0) _selection.Right += dx;
            else if (dx > 0) _selection.Left += dx;
            if (dy < 0) _selection.Bottom += dy;
            else if (dy > 0) _selection.Top += dy;
        }
        else
        {
            // Move entire selection
            _selection.Left += dx;
            _selection.Right += dx;
            _selection.Top += dy;
            _selection.Bottom += dy;
        }

        _selection = NormalizeRect(_selection);
        ClampSelectionToBounds();
        InvalidateRect(_hwnd, IntPtr.Zero, false);
    }

    /// <summary>
    /// Clamp the selection rectangle so it stays within the captured desktop area.
    /// For move operations, shifts the entire selection. For resize, clamps individual edges.
    /// </summary>
    private void ClampSelectionToBounds()
    {
        // Clamp edges to the desktop bitmap area [0, desktopWidth) x [0, desktopHeight)
        if (_selection.Left < 0)
        {
            _selection.Right -= _selection.Left; // shift right by overshoot
            _selection.Left = 0;
        }
        if (_selection.Top < 0)
        {
            _selection.Bottom -= _selection.Top;
            _selection.Top = 0;
        }
        if (_selection.Right > _desktopWidth)
        {
            _selection.Left -= _selection.Right - _desktopWidth;
            _selection.Right = _desktopWidth;
        }
        if (_selection.Bottom > _desktopHeight)
        {
            _selection.Top -= _selection.Bottom - _desktopHeight;
            _selection.Bottom = _desktopHeight;
        }

        // Final clamp in case the selection is larger than the desktop
        _selection.Left = Math.Clamp(_selection.Left, 0, _desktopWidth);
        _selection.Top = Math.Clamp(_selection.Top, 0, _desktopHeight);
        _selection.Right = Math.Clamp(_selection.Right, 0, _desktopWidth);
        _selection.Bottom = Math.Clamp(_selection.Bottom, 0, _desktopHeight);
    }

    private void ConfirmSelection()
    {
        var sel = NormalizeRect(_selection);
        if (sel.Width < 1 || sel.Height < 1)
        {
            CancelCapture();
            return;
        }

        // Extract pixels from frozen desktop bitmap
        var result = ExtractRegion(sel);
        _resultTcs?.TrySetResult(result);
        DestroyWindow(_hwnd);
    }

    private void CancelCapture()
    {
        _resultTcs?.TrySetResult(null);
        DestroyWindow(_hwnd);
    }

    /// <summary>
    /// Show a confirmation dialog before cancelling. If user says Yes, cancel.
    /// </summary>
    private void RequestCancelWithConfirmation()
    {
        var message = LocalizationService.Instance.GetString("ScreenCaptureExitConfirm");

        // MB_YESNO = 0x04, MB_ICONQUESTION = 0x20, MB_TOPMOST = 0x40000
        var result = MessageBoxW(_hwnd, message, "Easydict",
            0x00000004u | 0x00000020u | 0x00040000u);
        if (result == 6) // IDYES
        {
            CancelCapture();
        }
    }

    /// <summary>
    /// Update the mouse cursor based on current phase and position.
    /// Returns true if we handled the cursor, false to let Windows handle it.
    /// </summary>
    private bool UpdateCursor()
    {
        GetCursorPos(out var screenPt);
        var pt = new POINT
        {
            X = screenPt.X - _virtualLeft,
            Y = screenPt.Y - _virtualTop
        };

        int cursorId;

        if (_phase == SelectionPhase.Adjusting)
        {
            var sel = NormalizeRect(_selection);

            if (_isDragging)
            {
                // While dragging, keep the cursor matching the drag mode
                cursorId = GetCursorIdForDragMode(_dragMode);
            }
            else
            {
                // Hit-test handles first, then interior, then outside
                var mode = HitTestHandles(sel, pt);
                if (mode != DragMode.None)
                {
                    cursorId = GetCursorIdForDragMode(mode);
                }
                else if (sel.Contains(pt.X, pt.Y))
                {
                    cursorId = 32646; // IDC_SIZEALL (move)
                }
                else
                {
                    cursorId = 32515; // IDC_CROSS
                }
            }
        }
        else
        {
            cursorId = 32515; // IDC_CROSS for Detecting and Selecting
        }

        SetCursor(LoadCursor(IntPtr.Zero, cursorId));
        return true;
    }

    private static int GetCursorIdForDragMode(DragMode mode) => mode switch
    {
        DragMode.Move => 32646,                                             // IDC_SIZEALL
        DragMode.ResizeTopLeft or DragMode.ResizeBottomRight => 32642,      // IDC_SIZENWSE
        DragMode.ResizeTopRight or DragMode.ResizeBottomLeft => 32643,      // IDC_SIZENESW
        DragMode.ResizeTop or DragMode.ResizeBottom => 32645,               // IDC_SIZENS
        DragMode.ResizeLeft or DragMode.ResizeRight => 32644,               // IDC_SIZEWE
        _ => 32515,                                                          // IDC_CROSS
    };

    private ScreenCaptureResult ExtractRegion(RECT sel)
    {
        var width = sel.Width;
        var height = sel.Height;

        // Create a memory DC and bitmap for the selection
        var memDc = CreateCompatibleDC(_desktopDcHandle);
        var hBitmap = CreateCompatibleBitmap(_desktopDcHandle, width, height);
        var oldBmp = SelectObject(memDc, hBitmap);

        BitBlt(memDc, 0, 0, width, height, _desktopDcHandle, sel.Left, sel.Top, SRCCOPY);

        // Extract pixel data
        var bmi = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height, // Top-down
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0 // BI_RGB
            }
        };

        var pixelData = new byte[width * height * 4];
        GetDIBits(memDc, hBitmap, 0, (uint)height, pixelData, ref bmi, 0);

        SelectObject(memDc, oldBmp);
        DeleteObject(hBitmap);
        DeleteDC(memDc);

        return new ScreenCaptureResult
        {
            PixelData = pixelData,
            PixelWidth = width,
            PixelHeight = height,
            ScreenRect = new OcrRect(
                sel.Left + _virtualLeft,
                sel.Top + _virtualTop,
                width, height)
        };
    }

    // --- Helper methods ---

    private void ApplyDrag(POINT pt)
    {
        var dx = pt.X - _dragStart.X;
        var dy = pt.Y - _dragStart.Y;
        _dragStart = pt;

        switch (_dragMode)
        {
            case DragMode.Move:
                _selection.Left += dx; _selection.Right += dx;
                _selection.Top += dy; _selection.Bottom += dy;
                break;
            case DragMode.ResizeTopLeft: _selection.Left += dx; _selection.Top += dy; break;
            case DragMode.ResizeTop: _selection.Top += dy; break;
            case DragMode.ResizeTopRight: _selection.Right += dx; _selection.Top += dy; break;
            case DragMode.ResizeLeft: _selection.Left += dx; break;
            case DragMode.ResizeRight: _selection.Right += dx; break;
            case DragMode.ResizeBottomLeft: _selection.Left += dx; _selection.Bottom += dy; break;
            case DragMode.ResizeBottom: _selection.Bottom += dy; break;
            case DragMode.ResizeBottomRight: _selection.Right += dx; _selection.Bottom += dy; break;
        }

        ClampSelectionToBounds();
    }

    private static DragMode HitTestHandles(RECT sel, POINT pt)
    {
        var handles = GetHandleRects(sel);
        DragMode[] modes =
        [
            DragMode.ResizeTopLeft, DragMode.ResizeTop, DragMode.ResizeTopRight,
            DragMode.ResizeLeft, DragMode.ResizeRight,
            DragMode.ResizeBottomLeft, DragMode.ResizeBottom, DragMode.ResizeBottomRight
        ];

        for (int i = 0; i < handles.Length; i++)
        {
            if (handles[i].Contains(pt.X, pt.Y))
                return modes[i];
        }
        return DragMode.None;
    }

    private static RECT[] GetHandleRects(RECT sel)
    {
        int hs = HandleSize;
        int hh = hs / 2;
        int mx = (sel.Left + sel.Right) / 2;
        int my = (sel.Top + sel.Bottom) / 2;

        return
        [
            MakeHandleRect(sel.Left, sel.Top, hh),         // TopLeft
            MakeHandleRect(mx, sel.Top, hh),               // Top
            MakeHandleRect(sel.Right, sel.Top, hh),        // TopRight
            MakeHandleRect(sel.Left, my, hh),              // Left
            MakeHandleRect(sel.Right, my, hh),             // Right
            MakeHandleRect(sel.Left, sel.Bottom, hh),      // BottomLeft
            MakeHandleRect(mx, sel.Bottom, hh),            // Bottom
            MakeHandleRect(sel.Right, sel.Bottom, hh),     // BottomRight
        ];
    }

    private static RECT MakeHandleRect(int cx, int cy, int hh) => new()
    {
        Left = cx - hh, Top = cy - hh, Right = cx + hh, Bottom = cy + hh
    };

    private static RECT NormalizeRect(RECT r) => new()
    {
        Left = Math.Min(r.Left, r.Right),
        Top = Math.Min(r.Top, r.Bottom),
        Right = Math.Max(r.Left, r.Right),
        Bottom = Math.Max(r.Top, r.Bottom)
    };

    private static bool RectsEqual(RECT a, RECT b)
        => a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

    private static POINT GetLParamPoint(nint lParam) => new()
    {
        X = (short)(lParam.ToInt64() & 0xFFFF),
        Y = (short)((lParam.ToInt64() >> 16) & 0xFFFF)
    };

    private static int GetWheelDelta(nint wParam) => (short)((wParam.ToInt64() >> 16) & 0xFFFF);

    private void Cleanup()
    {
        if (_tipsFont != IntPtr.Zero)
        {
            DeleteObject(_tipsFont);
            _tipsFont = IntPtr.Zero;
        }

        // Restore the original bitmap before deleting the DC and our bitmap.
        // GDI requires that objects are deselected from a DC before deletion.
        if (_desktopDcHandle != IntPtr.Zero && _oldDesktopBitmapHandle != IntPtr.Zero)
        {
            SelectObject(_desktopDcHandle, _oldDesktopBitmapHandle);
            _oldDesktopBitmapHandle = IntPtr.Zero;
        }
        if (_desktopDcHandle != IntPtr.Zero)
        {
            DeleteDC(_desktopDcHandle);
            _desktopDcHandle = IntPtr.Zero;
        }
        if (_desktopBitmapHandle != IntPtr.Zero)
        {
            DeleteObject(_desktopBitmapHandle);
            _desktopBitmapHandle = IntPtr.Zero;
        }

        try { UnregisterClass(WindowClassName, GetModuleHandle(null)); }
        catch (ExternalException) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Cleanup();
    }

    // --- P/Invoke declarations ---

    private delegate nint WndProcDelegate(nint hwnd, uint msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PAINTSTRUCT
    {
        public nint hdc;
        public bool fErase;
        public RECT rcPaint;
        public bool fRestore;
        public bool fIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        // bmiColors intentionally omitted for 32-bit BGRA
    }

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(uint nIndex);
    [DllImport("user32.dll")] private static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd, nint hdc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint hdc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleBitmap(nint hdc, int cx, int cy);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint hdc, nint h);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(nint hdc, int x, int y, int cx, int cy, nint hdcSrc, int x1, int y1, int rop);
    [DllImport("gdi32.dll")] private static extern bool StretchBlt(nint hdcDest, int xDest, int yDest, int wDest, int hDest, nint hdcSrc, int xSrc, int ySrc, int wSrc, int hSrc, int rop);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint ho);
    [DllImport("gdi32.dll")] private static extern nint CreatePen(int iStyle, int cWidth, uint color);
    [DllImport("gdi32.dll")] private static extern nint CreateSolidBrush(uint color);
    [DllImport("gdi32.dll")] private static extern nint GetStockObject(int i);
    [DllImport("gdi32.dll")] private static extern bool Rectangle(nint hdc, int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(nint hdc, int mode);
    [DllImport("gdi32.dll")] private static extern uint SetTextColor(nint hdc, uint color);
    [DllImport("gdi32.dll")] private static extern bool MoveToEx(nint hdc, int x, int y, nint lppt);
    [DllImport("gdi32.dll")] private static extern bool LineTo(nint hdc, int x, int y);
    [DllImport("gdi32.dll")] private static extern uint GetPixel(nint hdc, int x, int y);
    [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(nint hdc, int mode);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(nint hdc, nint hbmp, uint start, uint cLines, byte[] lpvBits, ref BITMAPINFO lpbmi, uint usage);
    [DllImport("user32.dll")] private static extern bool FillRect(nint hdc, ref RECT lprc, nint hbr);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern bool TextOut(nint hdc, int x, int y, string lpString, int c);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateFont(int cHeight, int cWidth, int cEscapement, int cOrientation, int cWeight, uint bItalic, uint bUnderline, uint bStrikeOut, uint iCharSet, uint iOutPrecision, uint iClipPrecision, uint iQuality, uint iPitchAndFamily, string pszFaceName);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern bool GetTextExtentPoint32(nint hdc, string lpString, int c, out SIZE lpSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }
    [DllImport("user32.dll")] private static extern bool InvalidateRect(nint hwnd, nint lpRect, bool bErase);
    [DllImport("user32.dll")] private static extern nint BeginPaint(nint hwnd, ref PAINTSTRUCT lpPaint);
    [DllImport("user32.dll")] private static extern bool EndPaint(nint hwnd, ref PAINTSTRUCT lpPaint);
    [DllImport("user32.dll")] private static extern nint SetCapture(nint hwnd);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll")] private static extern short GetKeyState(int nVirtKey);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int nExitCode);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint SetFocus(nint hwnd);
    [DllImport("user32.dll")] private static extern nint LoadCursor(nint hInstance, int lpCursorName);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClass(string lpClassName, nint hInstance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateWindowEx(int dwExStyle, string lpClassName, string lpWindowName, int dwStyle, int x, int y, int nWidth, int nHeight, nint hWndParent, nint hMenu, nint hInstance, nint lpParam);
    [DllImport("user32.dll")] private static extern nint DefWindowProc(nint hwnd, uint msg, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG lpMsg, nint hwnd, uint wMsgFilterMin, uint wMsgFilterMax);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG lpMsg);
    [DllImport("user32.dll")] private static extern nint DispatchMessage(ref MSG lpMsg);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? lpModuleName);
    [DllImport("user32.dll")] private static extern nint SetCursor(nint hCursor);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int MessageBoxW(nint hwnd, string text, string caption, uint type);
    [DllImport("msimg32.dll")] private static extern bool AlphaBlend(nint hdcDest, int xoriginDest, int yoriginDest, int wDest, int hDest, nint hdcSrc, int xoriginSrc, int yoriginSrc, int wSrc, int hSrc, BLENDFUNCTION ftn);
}
