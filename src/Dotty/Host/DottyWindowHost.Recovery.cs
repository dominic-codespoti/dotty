using System;
using Dotty.Rendering.Gpu;
using Dotty.Runtime.ContextMenu;
using Dotty.Runtime.Diagnostics;
using Dotty.Runtime.Prompt;
using Dotty.Silk.Rendering;
using Silk.NET.GLFW;
using Dotty.Runtime.Localization;

namespace Dotty.Silk;

/// <summary>
/// Render-failure and context-loss recovery (plan R4, plan-11 WI-5a/5b): an exception is retried once, then the GL
/// objects are rebuilt once, then the user is asked through a host prompt. A driver reset loses the context for
/// good, so rendering stops with a notice instead. A render failure never closes the window or ends the shells.
/// UI thread.
/// </summary>
internal static partial class DottyWindowHost
{
    private static RenderRecoveryPolicy _renderRecovery = new();
    private static GlContextResetProbe? _contextResetProbe;
    private static bool _verifyContextAfterRebuild;
    private static bool _renderFailurePromptShown;
    private static ContextMenuModel? _renderFailurePrompt;
    private static bool _contextLossReported;

    /// <summary>The context is lost for good (<see cref="RenderRecoveryState.ContextLost"/>): nothing is drawn until Dotty restarts.</summary>
    private static bool RenderingStopped => _renderRecovery.State == RenderRecoveryState.ContextLost;

    /// <summary>
    /// Asks GLFW for a robust context that reports driver resets (<c>GLFW_CONTEXT_ROBUSTNESS</c> =
    /// <c>GLFW_LOSE_CONTEXT_ON_RESET</c>). Must run after GLFW is initialised (<c>Window.Create</c>) and before the
    /// window is created (<c>IWindow.Initialize</c>); Silk.NET never resets window hints, so it reaches the context.
    /// Where the platform lacks the robustness extension GLFW creates an ordinary context.
    /// </summary>
    private static void RequestRobustContext() =>
        _glfwApi!.WindowHint(WindowHintRobustness.ContextRobustness, Robustness.LoseContextOnReset);

    private static void ConfigureRenderRecovery()
    {
        _renderRecovery = new RenderRecoveryPolicy();
        _verifyContextAfterRebuild = false;
        _renderFailurePromptShown = false;
        _renderFailurePrompt = null;
        _contextLossReported = false;
        _contextResetProbe = GlContextResetProbe.TryCreate(
            _gl, _glMajorVersion, _glMinorVersion, static name => _glfwApi!.GetProcAddress(name));
        if (_graphicsClassification.IsSoftware)
        {
            Console.Error.WriteLine(
                $"Dotty is drawing with a software OpenGL renderer ({_graphicsClassification.Summary}). " +
                "Content frames are limited to half the refresh rate. Install or enable the GPU driver for hardware " +
                "rendering; on Linux, unset LIBGL_ALWAYS_SOFTWARE or GALLIUM_DRIVER if they force software rendering.");
        }
    }

    /// <summary>Polls the driver's reset status before a frame renders; true when a reset was handled instead.</summary>
    private static bool HandleContextResetIfReported()
    {
        uint status = _contextResetProbe?.Poll() ?? 0;
        if (status == 0)
            return false;

        HandleContextLost(status);
        return true;
    }

    /// <summary>A presented frame: lets the policy settle, and checks that the context survived a rebuild.</summary>
    private static void OnFramePresented()
    {
        _renderRecovery.OnFramePresented(GetClockMilliseconds());
        if (!_verifyContextAfterRebuild)
            return;

        _verifyContextAfterRebuild = false;
        // A context lost on reset stays lost until it is recreated, which needs a new window; every command then
        // fails with GL_CONTEXT_LOST and the rebuilt objects are as dead as the old ones.
        if (GlContextResetProbe.ContextStillLost(_gl))
            HandleContextLost(GlContextResetProbe.GlContextLost);
    }

    private static void HandleContextLost(uint status) =>
        ApplyRenderRecovery(
            _renderRecovery.OnContextLost(GetClockMilliseconds()),
            UiStrings.RenderFailureOpenGl(GlContextResetProbe.DescribeStatus(status)));

    /// <summary>The frame threw. Replaces the former "print and close the window" path.</summary>
    private static void HandleRenderFailure(Exception exception)
    {
        RenderRecoveryAction action = _renderRecovery.OnRenderFailure(GetClockMilliseconds());
        Console.Error.WriteLine($"[render] frame failed ({exception.GetType().Name}: {exception.Message}); recovery: {action}.");
        if (action == RenderRecoveryAction.Fatal)
            Console.Error.WriteLine(exception);
        ApplyRenderRecovery(action, exception.Message);
    }

    private static void ApplyRenderRecovery(RenderRecoveryAction action, string reason)
    {
        switch (action)
        {
            case RenderRecoveryAction.Retry:
                // The failed frame requeued its reasons; make sure a complete frame follows.
                WindowPresentationGate.Invalidate(WindowFrameReason.Initial);
                break;
            case RenderRecoveryAction.Rebuild:
                if (TryRebuildRenderer(out string? error))
                {
                    _verifyContextAfterRebuild = true;
                }
                else
                {
                    Console.Error.WriteLine($"[render] rebuilding the renderer failed: {error}");
                    // Counts as the next failure; after a rebuild that is fatal, so this never recurses twice.
                    ApplyRenderRecovery(_renderRecovery.OnRenderFailure(GetClockMilliseconds()), error!);
                }
                break;
            case RenderRecoveryAction.StopRendering:
                StopRenderingAfterContextLoss(reason);
                break;
            default:
                EnterFatalRenderState(reason);
                break;
        }
    }

    /// <summary>
    /// Creates a new renderer on the same context and atlas: new programs, buffers, atlas textures (the new texture
    /// manager re-uploads the whole atlas) and image textures. The old renderer is disposed only once the new one
    /// exists, so a failed construction leaves the old one in place rather than a disposed instance. Every committed
    /// frame stamp is dropped so the next frame draws everything.
    /// </summary>
    private static bool TryRebuildRenderer(out string? error)
    {
        SilkTerminalRenderer rebuilt;
        try
        {
            rebuilt = new SilkTerminalRenderer(_gl, _atlas);
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }

        SilkTerminalRenderer previous = _renderer;
        _renderer = rebuilt;
        try
        {
            // The overlay binds through the old renderer's binding cache; it is recreated lazily against the new one.
            DisposeBellOverlay();
            previous?.Dispose();
        }
        catch (Exception exception)
        {
            // Deleting objects of a broken context may fail; the new renderer does not depend on them.
            Console.Error.WriteLine($"[render] disposing the failed renderer: {exception.Message}");
        }

        _committedFramebufferWidth = -1;
        _committedFramebufferHeight = -1;
        _committedAtlasVersion = int.MinValue;
        _committedGenerations.Clear();
        WindowPresentationGate.Invalidate(WindowFrameReason.Initial | WindowFrameReason.Atlas);
        error = null;
        return true;
    }

    /// <summary>
    /// Recovery is exhausted. The first time, show the prompt (one more frame is attempted to draw it); afterwards
    /// drop the failed frame's reasons and wait for new work, so a frame that keeps failing cannot spin a core.
    /// The window stays open and the shells keep running until the user decides.
    /// </summary>
    private static void EnterFatalRenderState(string reason)
    {
        if (!_renderFailurePromptShown)
        {
            _renderFailurePromptShown = true;
            Console.Error.WriteLine(
                "[render] Dotty could not recover its OpenGL renderer. The shells keep running; choose \"Try again\" " +
                "or \"Quit Dotty\" in the window, or close it.");
            ShowRenderFailurePrompt(reason);
            return;
        }

        WaitWithoutRendering();
    }

    /// <summary>
    /// The context is lost for good (a driver reset under <c>GLFW_LOSE_CONTEXT_ON_RESET</c>): rebuilding on it or
    /// drawing a prompt through it cannot work, and recreating the context (a new window) is not implemented. Once,
    /// say so on stderr, in the crash log and in the window title (no GL needed). Then wait for new work without
    /// drawing: the window stays open and the shells keep running until the user restarts Dotty.
    /// </summary>
    private static void StopRenderingAfterContextLoss(string reason)
    {
        if (!_contextLossReported)
        {
            _contextLossReported = true;
            _verifyContextAfterRebuild = false;
            // A failure prompt shown before the loss can no longer be drawn; it must not keep taking input.
            if (_renderFailurePrompt is { } prompt && _mouseHost is { } host && ReferenceEquals(host.ActiveContextMenu, prompt))
                host.ActiveContextMenu = null;
            string? logPath = CrashReporter.Report(
                "GPU reset (rendering stopped)",
                $"{reason}. The OpenGL context is lost; rendering stopped, the window and the shells were kept. " +
                $"Renderer: {_graphicsClassification.Summary}. Render failures: {_renderRecovery.FailureCount}.");
            Console.Error.WriteLine(
                $"[render] {reason}: the GPU driver reset and the OpenGL context is lost. Rendering stopped; the " +
                "shells keep running. Restart Dotty to draw again." +
                (logPath is null ? string.Empty : $" Crash log: {logPath}"));
            AssignWindowTitle(UiStrings.ContextLostWindowTitle);
        }

        WaitWithoutRendering();
    }

    /// <summary>Drops the pending frame reasons and blocks until new work, so a window that cannot draw never spins.</summary>
    private static void WaitWithoutRendering()
    {
        WindowPresentationGate.Consume();
        HostWake.Reset();
        WaitForWork(includeBlink: false);
    }

    private static void ShowRenderFailurePrompt(string reason)
    {
        if (_mouseHost == null)
            return;

        var prompt = HostPrompt.Build(
            UiStrings.RenderFailureTitle,
            new[] { reason, UiStrings.RenderFailureShellsRunning, UiStrings.RenderFailureDetails },
            new (string Label, Action Run)[]
            {
                (UiStrings.TryAgain, RetryRenderingFromPrompt),
                (UiStrings.QuitDotty, static () => _window.Close()),
            },
            initialFocus: 0,
            onDismissed: null);
        var size = _window.FramebufferSize;
        ClipboardReadPromptBuilder.Center(prompt, size.X, size.Y, _cellWidthPx, _cellHeightPx);
        _renderFailurePrompt = prompt;
        _mouseHost.ActiveContextMenu = prompt;
        WindowPresentationGate.Invalidate(WindowFrameReason.Overlay);
    }

    private static void RetryRenderingFromPrompt()
    {
        if (RenderingStopped)
            return;

        _renderRecovery.Reset();
        _renderFailurePromptShown = false;
        ApplyRenderRecovery(RenderRecoveryAction.Rebuild, "rebuild requested");
    }

    /// <summary><c>STATS</c>: <c>,"renderRecoveryState":"...","renderFailures":n</c> (<c>ContextLost</c> once rendering stopped).</summary>
    private static void AppendRenderRecoveryJson(System.Text.StringBuilder json)
    {
        json.Append(",\"renderRecoveryState\":").Append(QuoteJson(_renderRecovery.State.ToString()))
            .Append(",\"renderFailures\":").Append(_renderRecovery.FailureCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}
