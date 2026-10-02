using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Dotty.Runtime.Tabs;

namespace Dotty.Silk;

/// <summary>
/// Removes the caption from a GLFW HWND while leaving its native resize frame
/// and non-client window management in place.
/// </summary>
internal static unsafe partial class Win32WindowFrame
{
    private const int GwlpWndProc = -4;
    private const uint WmNcCalcSize = 0x0083;
    private const uint WmNcHitTest = 0x0084;
    private const uint WmNcMouseMove = 0x00A0;
    private const uint WmNcLButtonDown = 0x00A1;
    private const uint WmNcLButtonUp = 0x00A2;
    private const uint WmNcMouseLeave = 0x02A2;
    private const uint WmMouseMove = 0x0200;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmCaptureChanged = 0x0215;
    private const uint WmCancelMode = 0x001F;
    private const uint WmClose = 0x0010;
    private const int HtClient = 1;
    private const int HtCaption = 2;
    private const int HtTop = 12;
    private const int HtTopLeft = 13;
    private const int HtTopRight = 14;
    private const int HtLeft = 10;
    private const int HtRight = 11;
    private const int HtBottom = 15;
    private const int HtBottomLeft = 16;
    private const int HtBottomRight = 17;
    private const int HtMinButton = 8;
    private const int HtMaxButton = 9;
    private const int HtClose = 20;
    private const int SwMinimize = 6;
    private const int SwMaximize = 3;
    private const int SwRestore = 9;
    private const int SmCyFrame = 33;
    private const int SmCxPaddedBorder = 92;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const uint TmeLeave = 0x00000002;
    private const uint TmeNonClient = 0x00000010;

    private static nint _windowHandle;
    private static nint _previousWindowProc;
    private static delegate* managed<int, int, int> _hitTestCallback;
    private static delegate* managed<int, void> _hoverCallback;
    private static TabBarHitType _hoveredButton;
    private static int _pressedButton;

    internal static bool Install(
        nint windowHandle,
        delegate* managed<int, int, int> hitTestCallback,
        delegate* managed<int, void> hoverCallback)
    {
        if (!OperatingSystem.IsWindows() || windowHandle == 0)
            return false;

        _windowHandle = windowHandle;
        _hitTestCallback = hitTestCallback;
        _hoverCallback = hoverCallback;
        nint newWindowProc = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint>)&WindowProc;
        nint previous = SetWindowLongPtrW(windowHandle, GwlpWndProc, newWindowProc);
        if (previous == 0)
        {
            _windowHandle = 0;
            _hitTestCallback = null;
            _hoverCallback = null;
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        _previousWindowProc = previous;
        _ = DwmExtendFrameIntoClientArea(windowHandle, new Margins { Top = 1 });
        _ = SetWindowPos(windowHandle, 0, 0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
        return true;
    }

    internal static void Uninstall()
    {
        nint hwnd = _windowHandle;
        nint previous = _previousWindowProc;
        if (OperatingSystem.IsWindows() && hwnd != 0 && previous != 0)
        {
            SetWindowLongPtrW(hwnd, GwlpWndProc, previous);
            _ = DwmExtendFrameIntoClientArea(hwnd, default);
            _ = SetWindowPos(hwnd, 0, 0, 0, 0, 0,
                SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
        }

        _windowHandle = 0;
        _previousWindowProc = 0;
        _hitTestCallback = null;
        _hoverCallback = null;
        _hoveredButton = TabBarHitType.None;
        _pressedButton = 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static nint WindowProc(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        nint previous = _previousWindowProc;
        if (previous == 0)
            return DefWindowProcW(hwnd, message, wParam, lParam);

        if (message == WmNcCalcSize && lParam != 0)
        {
            Rect* clientRect = wParam != 0
                ? &((NccalcSizeParams*)lParam)->Rect0
                : (Rect*)lParam;
            int originalTop = clientRect->Top;
            nint result = CallWindowProcW(previous, hwnd, message, wParam, lParam);
            clientRect->Top = originalTop +
                (IsZoomed(hwnd) != 0 ? GetResizeFrameThickness(hwnd) : 0);
            return result;
        }

        if (message == WmNcHitTest)
        {
            nint originalHit = CallWindowProcW(previous, hwnd, message, wParam, lParam);
            int nativeHit = unchecked((int)originalHit);
            if (IsResizeHit(nativeHit))
                return originalHit;

            int screenX = unchecked((short)(long)lParam);
            int screenY = unchecked((short)((long)lParam >> 16));
            if (IsZoomed(hwnd) == 0 && IsInTopResizeBand(hwnd, screenX, screenY))
                return (nint)HtTop;

            if (TryGetClientPoint(hwnd, screenX, screenY, out Point clientPoint) && _hitTestCallback != null)
            {
                TabBarHitType hit = (TabBarHitType)_hitTestCallback(clientPoint.X, clientPoint.Y);
                int customHit = hit switch
                {
                    TabBarHitType.Caption => HtCaption,
                    TabBarHitType.Minimize => HtMinButton,
                    TabBarHitType.Maximize => HtMaxButton,
                    TabBarHitType.Close => HtClose,
                    TabBarHitType.None => nativeHit == HtCaption || IsCaptionButton(nativeHit) ? HtClient : nativeHit,
                    _ => HtClient
                };
                return (nint)customHit;
            }

            return nativeHit == HtCaption || IsCaptionButton(nativeHit) ? (nint)HtClient : originalHit;
        }

        if (message == WmNcMouseMove)
        {
            TabBarHitType hovered = HitTypeFromNativeCode(unchecked((int)wParam));
            UpdateHover(hovered);
            if (hovered != TabBarHitType.None)
            {
                var track = new TrackMouseEventData
                {
                    Size = (uint)sizeof(TrackMouseEventData),
                    Flags = TmeNonClient | TmeLeave,
                    Window = hwnd
                };
                _ = TrackMouseEvent(ref track);
            }
            return CallWindowProcW(previous, hwnd, message, wParam, lParam);
        }
        else if (message == WmNcMouseLeave)
        {
            UpdateHover(TabBarHitType.None);
            return CallWindowProcW(previous, hwnd, message, wParam, lParam);
        }

        if (message == WmNcLButtonDown)
        {
            int button = unchecked((int)wParam);
            if (IsCaptionButton(button))
            {
                _pressedButton = button;
                UpdateHover(HitTypeFromNativeCode(button));
                _ = SetCapture(hwnd);
                return 0;
            }
        }
        else if (message == WmNcLButtonUp && _pressedButton != 0)
        {
            int releasedButton = unchecked((int)wParam);
            int pressedButton = _pressedButton;
            _pressedButton = 0;
            _ = ReleaseCapture();
            if (releasedButton == pressedButton)
                ExecuteCaptionCommand(hwnd, pressedButton);
            return 0;
        }
        else if (message == WmMouseMove && _pressedButton != 0)
        {
            Point clientPoint = PointFromClientLParam(lParam);
            TabBarHitType hovered = _hitTestCallback == null
                ? TabBarHitType.None
                : (TabBarHitType)_hitTestCallback(clientPoint.X, clientPoint.Y);
            UpdateHover(hovered);
            return 0;
        }
        else if (message == WmLButtonUp && _pressedButton != 0)
        {
            int pressedButton = _pressedButton;
            Point clientPoint = PointFromClientLParam(lParam);
            TabBarHitType releasedHit = _hitTestCallback == null
                ? TabBarHitType.None
                : (TabBarHitType)_hitTestCallback(clientPoint.X, clientPoint.Y);
            int releasedButton = NativeCodeFromHitType(releasedHit);
            _pressedButton = 0;
            _ = ReleaseCapture();
            if (releasedButton == pressedButton)
                ExecuteCaptionCommand(hwnd, pressedButton);
            UpdateHover(releasedHit);
            return 0;
        }
        else if ((message == WmCaptureChanged || message == WmCancelMode) && _pressedButton != 0)
        {
            _pressedButton = 0;
            UpdateHover(TabBarHitType.None);
        }

        return CallWindowProcW(previous, hwnd, message, wParam, lParam);
    }

    private static bool IsInTopResizeBand(nint hwnd, int screenX, int screenY)
    {
        if (GetWindowRect(hwnd, out Rect windowRect) == 0)
            return false;

        int thickness = GetResizeFrameThickness(hwnd);
        return screenY >= windowRect.Top && screenY < windowRect.Top + thickness &&
            screenX >= windowRect.Left && screenX < windowRect.Right;
    }

    private static int GetResizeFrameThickness(nint hwnd)
    {
        uint dpi = GetDpiForWindow(hwnd);
        if (dpi == 0)
            dpi = 96;
        return GetSystemMetricsForDpi(SmCyFrame, dpi) + GetSystemMetricsForDpi(SmCxPaddedBorder, dpi);
    }

    private static bool TryGetClientPoint(nint hwnd, int screenX, int screenY, out Point point)
    {
        point = new Point { X = screenX, Y = screenY };
        return ScreenToClient(hwnd, ref point) != 0;
    }

    private static Point PointFromClientLParam(nint lParam) => new()
    {
        X = unchecked((short)(long)lParam),
        Y = unchecked((short)((long)lParam >> 16))
    };

    private static bool IsResizeHit(int hit) => hit is
        HtTop or HtTopLeft or HtTopRight or HtLeft or HtRight or HtBottom or HtBottomLeft or HtBottomRight;

    private static bool IsCaptionButton(int hit) => hit is HtMinButton or HtMaxButton or HtClose;

    private static TabBarHitType HitTypeFromNativeCode(int hit) => hit switch
    {
        HtMinButton => TabBarHitType.Minimize,
        HtMaxButton => TabBarHitType.Maximize,
        HtClose => TabBarHitType.Close,
        _ => TabBarHitType.None
    };

    private static int NativeCodeFromHitType(TabBarHitType hit) => hit switch
    {
        TabBarHitType.Minimize => HtMinButton,
        TabBarHitType.Maximize => HtMaxButton,
        TabBarHitType.Close => HtClose,
        _ => 0
    };

    private static void UpdateHover(TabBarHitType hit)
    {
        if (_hoveredButton == hit)
            return;
        _hoveredButton = hit;
        if (_hoverCallback != null)
            _hoverCallback((int)hit);
    }

    private static void ExecuteCaptionCommand(nint hwnd, int button)
    {
        switch (button)
        {
            case HtMinButton:
                _ = ShowWindow(hwnd, SwMinimize);
                break;
            case HtMaxButton:
                _ = ShowWindow(hwnd, IsZoomed(hwnd) != 0 ? SwRestore : SwMaximize);
                break;
            case HtClose:
                _ = PostMessageW(hwnd, WmClose, 0, 0);
                break;
        }
    }

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static partial nint SetWindowLongPtrW(nint hwnd, int index, nint newValue);

    [LibraryImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static partial nint CallWindowProcW(nint previous, nint hwnd, uint message, nuint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static partial nint DefWindowProcW(nint hwnd, uint message, nuint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowPos")]
    private static partial int SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);

    [LibraryImport("dwmapi.dll", EntryPoint = "DwmExtendFrameIntoClientArea")]
    private static partial int DwmExtendFrameIntoClientArea(nint hwnd, in Margins margins);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowRect")]
    private static partial int GetWindowRect(nint hwnd, out Rect rect);

    [LibraryImport("user32.dll", EntryPoint = "ScreenToClient")]
    private static partial int ScreenToClient(nint hwnd, ref Point point);

    [LibraryImport("user32.dll", EntryPoint = "GetDpiForWindow")]
    private static partial uint GetDpiForWindow(nint hwnd);

    [LibraryImport("user32.dll", EntryPoint = "GetSystemMetricsForDpi")]
    private static partial int GetSystemMetricsForDpi(int index, uint dpi);

    [LibraryImport("user32.dll", EntryPoint = "IsZoomed")]
    private static partial int IsZoomed(nint hwnd);

    [LibraryImport("user32.dll", EntryPoint = "ShowWindow")]
    private static partial int ShowWindow(nint hwnd, int command);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    private static partial int PostMessageW(nint hwnd, uint message, nuint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "SetCapture")]
    private static partial nint SetCapture(nint hwnd);

    [LibraryImport("user32.dll", EntryPoint = "ReleaseCapture")]
    private static partial int ReleaseCapture();

    [LibraryImport("user32.dll", EntryPoint = "TrackMouseEvent")]
    private static partial int TrackMouseEvent(ref TrackMouseEventData track);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NccalcSizeParams
    {
        public Rect Rect0;
        public Rect Rect1;
        public Rect Rect2;
        public nint WindowPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrackMouseEventData
    {
        public uint Size;
        public uint Flags;
        public nint Window;
        public uint HoverTime;
    }
}
