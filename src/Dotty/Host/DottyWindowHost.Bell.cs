using System.Threading;
using Dotty.Rendering.Gpu;
using Dotty.Runtime.Config;
using Dotty.Runtime.Panes;
using Dotty.Runtime.Tabs;

namespace Dotty.Silk;

// Terminal bell: window attention and the visual flash (config `bell.*`). Bells are raised on a session's PTY
// consumer thread while it holds the buffer lock (TerminalSession.ConsumePtyOutput -> Parser.Feed -> OnBell ->
// TerminalTabManager.BellRung), so that thread only counts them and wakes the UI thread; every decision and every
// GLFW/GL call happens in TickBell/DrawBellFlash on the UI thread.
internal static partial class DottyWindowHost
{
    private static int _pendingBellCount;
    private static long _lastBellEffectMs = BellPolicy.Never;
    private static long _bellFlashStartMs = BellPolicy.Never;
    private static long _bellFlashNextFrameMs = BellPolicy.Never;
    private static float _bellFlashAlpha;
    private static bool _glfwAttentionProcResolved;
    private static nint _glfwRequestWindowAttentionProc;
    private static SilkOverlayRenderer? _bellOverlay;

    private static void AttachBellNotifications(TerminalTabManager tabManager)
    {
        Volatile.Write(ref _pendingBellCount, 0);
        _lastBellEffectMs = BellPolicy.Never;
        _bellFlashStartMs = BellPolicy.Never;
        _bellFlashNextFrameMs = BellPolicy.Never;
        _bellFlashAlpha = 0f;
        tabManager.BellRung += OnTabBellRung;
    }

    private static void DetachBellNotifications(TerminalTabManager tabManager)
    {
        tabManager.BellRung -= OnTabBellRung;
        Volatile.Write(ref _pendingBellCount, 0);
    }

    private static void DisposeBellOverlay()
    {
        _bellOverlay?.Dispose();
        _bellOverlay = null;
    }

    // PTY consumer thread, buffer lock held: count and wake only.
    private static void OnTabBellRung(TerminalTab tab, LeafPane pane)
    {
        Interlocked.Increment(ref _pendingBellCount);
        HostWake.Request();
    }

    /// <summary>UI thread, once per render pass: applies pending bells and paces the running flash.</summary>
    private static void TickBell(long nowMs)
    {
        if (Interlocked.Exchange(ref _pendingBellCount, 0) != 0)
        {
            // A background tab's tab-bar bell marker may have changed. Repaint as Content so a BEL flood is
            // coalesced like streaming output instead of forcing an uncapped frame per drained batch.
            WindowPresentationGate.Invalidate(WindowFrameReason.Content);
            if (BellPolicy.TryAccept(ref _lastBellEffectMs, nowMs))
            {
                BellUserConfig bell = UserConfigService.Current.Bell;
                BellEffects effects = BellPolicy.Decide(bell.Attention, bell.Visual, windowFocused: _lastWindowFocus != false);
                if ((effects & BellEffects.Attention) != 0)
                    RequestWindowAttention();
                if ((effects & BellEffects.Flash) != 0)
                {
                    _bellFlashStartMs = nowMs;
                    _bellFlashNextFrameMs = nowMs;
                }
            }
        }

        if (_bellFlashStartMs == BellPolicy.Never)
            return;

        _bellFlashAlpha = BellPolicy.FlashAlpha(nowMs - _bellFlashStartMs);
        if (_bellFlashAlpha <= 0f)
        {
            // One more frame without the overlay clears it.
            _bellFlashStartMs = BellPolicy.Never;
            _bellFlashNextFrameMs = BellPolicy.Never;
            WindowPresentationGate.Invalidate(WindowFrameReason.Overlay);
        }
        else if (nowMs >= _bellFlashNextFrameMs)
        {
            _bellFlashNextFrameMs = BellPolicy.NextFlashFrameMs(_bellFlashStartMs, nowMs);
            WindowPresentationGate.Invalidate(WindowFrameReason.Overlay);
        }
    }

    /// <summary>Wait deadline for the idle loop: 0 without a running flash, otherwise the time to its next frame.</summary>
    private static long BellFlashRemainingMs(long nowMs) =>
        _bellFlashNextFrameMs == BellPolicy.Never ? 0 : System.Math.Max(1L, _bellFlashNextFrameMs - nowMs);

    /// <summary>Draws the flash over the rendered frame, after <c>Render</c> and before the swap.</summary>
    private static void DrawBellFlash(int framebufferWidth, int framebufferHeight)
    {
        float alpha = _bellFlashAlpha;
        if (alpha <= 0f || _bellFlashStartMs == BellPolicy.Never)
            return;

        _bellOverlay ??= new SilkOverlayRenderer(_gl, _renderer);
        float r = _themeForeground.R / 255f;
        float g = _themeForeground.G / 255f;
        float b = _themeForeground.B / 255f;
        // One pixel past every edge so the shader's anti-aliased border falls outside the window.
        var quad = new ChromeQuadInstance
        {
            X = -1f,
            Y = -1f,
            W = framebufferWidth + 2f,
            H = framebufferHeight + 2f,
            TopR = r,
            TopG = g,
            TopB = b,
            TopA = alpha,
            BottomR = r,
            BottomG = g,
            BottomB = b,
            BottomA = alpha,
        };
        _bellOverlay.Draw(quad, framebufferWidth, framebufferHeight);
    }

    private static unsafe void RequestWindowAttention()
    {
        nint handle = _window.Native?.Glfw ?? 0;
        if (handle == 0 || _glfwApi is null)
            return;

        if (!_glfwAttentionProcResolved)
        {
            _glfwAttentionProcResolved = true;
            _glfwRequestWindowAttentionProc = _glfwApi.Context.GetProcAddress("glfwRequestWindowAttention");
        }

        if (_glfwRequestWindowAttentionProc != 0)
            ((delegate* unmanaged[Cdecl]<nint, void>)_glfwRequestWindowAttentionProc)(handle);
    }
}
