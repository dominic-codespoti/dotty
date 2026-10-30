using System;
using Dotty.Rendering.Gpu;
using Dotty.Runtime.Prompt;
using Dotty.Silk.Rendering;
using Silk.NET.GLFW;

namespace Dotty.Silk;

/// <summary>
/// Render-failure and context-loss recovery (plan R4, plan-11 WI-5a/5b): retry once, rebuild the GL objects once,
/// then ask the user through a host prompt. A render failure never closes the window or ends the shells. UI thread.
/// </summary>
internal static partial class DottyWindowHost
{
    private static RenderRecoveryPolicy _renderRecovery = new();
    private static GlContextResetProbe? _contextResetProbe;
    private static bool _verifyContextAfterRebuild;
    private static bool _renderFailurePromptShown;

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

        HandleContextReset(status);
        return true;
    }

    /// <summary>A presented frame: lets the policy settle, and checks that a reset-triggered rebuild took.</summary>
    private static void OnFramePresented()
    {
        _renderRecovery.OnFramePresented(GetClockMilliseconds());
        if (!_verifyContextAfterRebuild)
            return;

        _verifyContextAfterRebuild = false;
        // A context lost on reset stays lost until it is recreated, which needs a new window; every command then
        // fails with GL_CONTEXT_LOST and the rebuilt objects are as dead as the old ones.
        if (GlContextResetProbe.ContextStillLost(_gl))
            HandleContextReset(GlContextResetProbe.GlContextLost);
    }

    private static void HandleContextReset(uint status)
    {
        string reason = "OpenGL " + GlContextResetProbe.DescribeStatus(status);
        RenderRecoveryAction action = _renderRecovery.OnContextReset(GetClockMilliseconds());
        Console.Error.WriteLine($"[render] {reason}; recovery: {action}.");
        ApplyRenderRecovery(action, reason, contextReset: true);
    }

    /// <summary>The frame threw. Replaces the former "print and close the window" path.</summary>
    private static void HandleRenderFailure(Exception exception)
    {
        RenderRecoveryAction action = _renderRecovery.OnRenderFailure(GetClockMilliseconds());
        Console.Error.WriteLine($"[render] frame failed ({exception.GetType().Name}: {exception.Message}); recovery: {action}.");
        if (action == RenderRecoveryAction.Fatal)
            Console.Error.WriteLine(exception);
        ApplyRenderRecovery(action, exception.Message, contextReset: false);
    }

    private static void ApplyRenderRecovery(RenderRecoveryAction action, string reason, bool contextReset)
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
                    _verifyContextAfterRebuild = contextReset;
                }
                else
                {
                    Console.Error.WriteLine($"[render] rebuilding the renderer failed: {error}");
                    // Counts as the next failure; after a rebuild that is fatal, so this never recurses twice.
                    ApplyRenderRecovery(_renderRecovery.OnRenderFailure(GetClockMilliseconds()), error!, contextReset: false);
                }
                break;
            default:
                EnterFatalRenderState(reason);
                break;
        }
    }

    /// <summary>
    /// Disposes the renderer and creates a new one on the same context and atlas: new programs, buffers, atlas
    /// textures (the new texture manager re-uploads the whole atlas) and image textures. Every committed frame
    /// stamp is dropped so the next frame draws everything.
    /// </summary>
    private static bool TryRebuildRenderer(out string? error)
    {
        try
        {
            _renderer?.Dispose();
        }
        catch (Exception exception)
        {
            // Deleting objects of a broken or lost context may fail; the new renderer does not depend on them.
            Console.Error.WriteLine($"[render] disposing the failed renderer: {exception.Message}");
        }

        try
        {
            _renderer = new SilkTerminalRenderer(_gl, _atlas);
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
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

        WindowPresentationGate.Consume();
        HostWake.Reset();
        WaitForWork(includeBlink: false);
    }

    private static void ShowRenderFailurePrompt(string reason)
    {
        if (_mouseHost == null)
            return;

        var prompt = HostPrompt.Build(
            "Dotty could not draw this window",
            new[] { reason, "Your shells are still running.", "Details were written to stderr." },
            new (string Label, Action Run)[]
            {
                ("Try again", RetryRenderingFromPrompt),
                ("Quit Dotty", static () => _window.Close()),
            },
            initialFocus: 0,
            onDismissed: null);
        var size = _window.FramebufferSize;
        ClipboardReadPromptBuilder.Center(prompt, size.X, size.Y, _cellWidth * _scale, _cellHeight * _scale);
        _mouseHost.ActiveContextMenu = prompt;
        WindowPresentationGate.Invalidate(WindowFrameReason.Overlay);
    }

    private static void RetryRenderingFromPrompt()
    {
        _renderRecovery.Reset();
        _renderFailurePromptShown = false;
        ApplyRenderRecovery(RenderRecoveryAction.Rebuild, "rebuild requested", contextReset: false);
    }

    /// <summary><c>STATS</c>: <c>,"renderRecoveryState":"...","renderFailures":n</c>.</summary>
    private static void AppendRenderRecoveryJson(System.Text.StringBuilder json)
    {
        json.Append(",\"renderRecoveryState\":").Append(QuoteJson(_renderRecovery.State.ToString()))
            .Append(",\"renderFailures\":").Append(_renderRecovery.FailureCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}
