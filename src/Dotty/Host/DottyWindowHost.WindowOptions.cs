using System;
using System.IO;
using System.Reflection;
using System.Text;
using Dotty.Runtime.Config;
using Dotty.Silk.Config;
using Dotty.Silk.Rendering;
using Silk.NET.Core;
using Silk.NET.Windowing;
using SkiaSharp;

namespace Dotty.Silk;

// Window options and desktop identity (`window.*`): creation hints (title, alpha framebuffer, floating,
// X11 class / Wayland app_id), the initial grid, the first-show state, the window icon and the
// background-only opacity.
internal static partial class DottyWindowHost
{
    private const int GlfwX11ClassName = 0x00024001;
    private const int GlfwX11InstanceName = 0x00024002;
    private const int GlfwWaylandAppId = 0x00026001;

    private static WindowStartOptions _startOptions;
    private static bool _startStateApplied;

    /// <summary>
    /// Resolves the creation-time options. Lua runs after the window exists, so only the JSON config decides
    /// the alpha framebuffer and the creation title; the rest is re-resolved by <see cref="ApplyLoadedWindowOptions"/>.
    /// </summary>
    private static WindowOptions WithStartOptions(WindowOptions options)
    {
        _startStateApplied = false;
        _startOptions = WindowStartOptions.Resolve(UserConfigService.Current.Window, SilkConfig.LoadActiveTheme().Opacity);
        return options with
        {
            Title = _startOptions.Title,
            TransparentFramebuffer = _startOptions.TransparentFramebuffer,
            TopMost = _startOptions.Floating,
        };
    }

    /// <summary>
    /// Sets the X11 class/instance and Wayland app_id window hints. Must run after GLFW is initialised
    /// (<c>Window.Create</c>) and before the window is created (<c>IWindow.Initialize</c>).
    /// </summary>
    private static unsafe void RequestDesktopIdentityHints()
    {
        nint proc = _glfwApi!.Context.GetProcAddress("glfwWindowHintString");
        if (proc == 0)
            return;
        var hintString = (delegate* unmanaged[Cdecl]<int, byte*, void>)proc;
        SetHintString(hintString, GlfwX11ClassName, WindowStartOptions.AppId);
        SetHintString(hintString, GlfwX11InstanceName, WindowStartOptions.X11InstanceName);
        SetHintString(hintString, GlfwWaylandAppId, WindowStartOptions.AppId);
    }

    private static unsafe void SetHintString(delegate* unmanaged[Cdecl]<int, byte*, void> hintString, int hint, string value)
    {
        int length = Encoding.UTF8.GetByteCount(value);
        Span<byte> utf8 = stackalloc byte[length + 1];
        Encoding.UTF8.GetBytes(value, utf8);
        utf8[length] = 0;
        fixed (byte* pointer = utf8)
            hintString(hint, pointer);
    }

    /// <summary>Applies options after the Lua config ran: initial grid, title, floating and the icon.</summary>
    private static void ApplyLoadedWindowOptions()
    {
        var config = UserConfigService.Current;
        _startOptions = WindowStartOptions.Resolve(config.Window, _activeTheme.Opacity) with
        {
            TransparentFramebuffer = _window.TransparentFramebuffer,
        };
        _cols = _startOptions.Columns;
        _rows = _startOptions.Rows;
        _window.Title = _startOptions.Title;
        _window.TopMost = _startOptions.Floating;
        if (WindowStartOptions.SupportsWindowIcon(_displayPlatform))
            SetWindowIcon();
    }

    /// <summary>Re-applies the live options (opacity, floating) after a config or theme change.</summary>
    private static void ApplyLiveWindowOptions(DottyUserConfig config)
    {
        byte alpha = _window.TransparentFramebuffer
            ? BackgroundOpacity.Resolve(config.Window.Opacity, _activeTheme.Opacity)
            : (byte)255;
        _renderer.BackgroundAlpha = alpha;
        _sceneComposer.BackgroundAlpha = alpha;
        if (_window.TopMost != config.Window.Floating)
            _window.TopMost = config.Window.Floating;
    }

    /// <summary>Applies <c>window.startMaximized</c>/<c>startFullscreen</c> once, right after the first show.</summary>
    private static void ApplyStartWindowState()
    {
        if (_startStateApplied)
            return;
        _startStateApplied = true;
        WindowState state = _startOptions.StartState switch
        {
            WindowStartState.Fullscreen => WindowState.Fullscreen,
            WindowStartState.Maximized => WindowState.Maximized,
            _ => WindowState.Normal,
        };
        if (state == WindowState.Normal)
            return;
        if (state == WindowState.Fullscreen)
            _previousNonFullscreenState = WindowState.Normal;
        _window.WindowState = state;
    }

    private static void SetWindowIcon()
    {
        Assembly assembly = typeof(DottyWindowHost).Assembly;
        var images = new RawImage[WindowStartOptions.IconSizes.Length];
        int count = 0;
        foreach (int size in WindowStartOptions.IconSizes)
        {
            using Stream? stream = assembly.GetManifestResourceStream(WindowStartOptions.IconResourceName(size));
            if (stream is null)
                continue;
            using var codec = SKCodec.Create(stream);
            if (codec is null)
                continue;
            var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            byte[] pixels = new byte[info.BytesSize];
            unsafe
            {
                fixed (byte* pointer = pixels)
                {
                    if (codec.GetPixels(info, (nint)pointer) != SKCodecResult.Success)
                        continue;
                }
            }
            images[count++] = new RawImage(info.Width, info.Height, pixels);
        }
        if (count > 0)
            _window.SetWindowIcon(images.AsSpan(0, count));
    }
}
