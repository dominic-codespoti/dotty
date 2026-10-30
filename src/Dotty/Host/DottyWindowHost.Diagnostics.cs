using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Dotty.Rendering.Gpu;
using Dotty.Runtime.Panes;
using Dotty.Silk.Rendering;
using Silk.NET.OpenGL;

namespace Dotty.Silk;

/// <summary>
/// GL diagnostics, frame timing and the deferred control commands (<c>WAIT_FOR_IDLE</c>, <c>SCREENSHOT:</c>) of
/// the desktop host (plan-11 WI-0a/0b/0c, WI-5d). Everything here runs on the UI thread.
/// </summary>
internal static partial class DottyWindowHost
{
    internal const string GlInfoEnvironmentVariable = "DOTTY_GL_INFO";
    internal const int WaitForIdleDefaultTimeoutMs = 5000;
    // DesktopControlServer abandons a request after 10 s; answer before that.
    internal const int WaitForIdleMaxTimeoutMs = 9000;

    private static string? _glVendor;
    private static string? _glRenderer;
    private static string? _glVersion;
    private static string? _glShadingLanguageVersion;
    private static int _glMajorVersion;
    private static int _glMinorVersion;
    private static int _glMaxTextureSize;
    private static GraphicsAdapterClassification _graphicsClassification;
    private static bool _glDebugEnabled;
    private static bool _glDebugCallbackInstalled;

    private static readonly FrameTimings _frameTimings = new(System.Diagnostics.Stopwatch.Frequency);
    private static readonly List<IdleWaiter> _idleWaiters = new();
    private static readonly List<ScreenshotRequest> _screenshotRequests = new();

    private readonly record struct IdleWaiter(TaskCompletionSource<string> Completion, long DeadlineMs);
    private readonly record struct ScreenshotRequest(TaskCompletionSource<string> Completion, string Path);

    /// <summary>Reads the driver strings and limits once, right after the context is created.</summary>
    private static void CaptureGraphicsInfo(GL gl)
    {
        _glVendor = gl.GetStringS(StringName.Vendor);
        _glRenderer = gl.GetStringS(StringName.Renderer);
        _glVersion = gl.GetStringS(StringName.Version);
        _glShadingLanguageVersion = gl.GetStringS(StringName.ShadingLanguageVersion);
        GraphicsCapabilities.TryParseOpenGlVersion(_glVersion, out _glMajorVersion, out _glMinorVersion);
        gl.GetInteger(GetPName.MaxTextureSize, out _glMaxTextureSize);
        _graphicsClassification = GraphicsCapabilities.Classify(_glVendor, _glRenderer, _glVersion);
    }

    /// <summary><c>DOTTY_GL_DEBUG=1</c>: KHR_debug callback where available; glGetError checks after every render.</summary>
    private static void ConfigureGlDebug(GL gl)
    {
        _glDebugEnabled = GlDebugOutput.IsRequested();
        if (!_glDebugEnabled)
            return;

        _glDebugCallbackInstalled = GlDebugOutput.TryInstall(gl, _glMajorVersion, _glMinorVersion, static name => _glfwApi!.GetProcAddress(name));
        Console.Error.WriteLine(_glDebugCallbackInstalled
            ? "[gl-debug] KHR_debug callback installed; glGetError is checked after every render."
            : "[gl-debug] KHR_debug is not available; only glGetError is checked after every render.");
    }

    private static void LogGraphicsInfoIfRequested()
    {
        if (string.Equals(Environment.GetEnvironmentVariable(GlInfoEnvironmentVariable), "1", StringComparison.Ordinal))
            Console.Error.WriteLine("DOTTY_GL_INFO " + BuildGraphicsInfoJson());
    }

    /// <summary>Startup failed: always name the driver when the context got far enough to report one.</summary>
    private static void LogGraphicsInfoOnInitFailure()
    {
        if (_glVersion != null)
            Console.Error.WriteLine(_graphicsClassification.Summary);
    }

    /// <summary>The <c>GL</c> control command and the <c>DOTTY_GL_INFO</c> line.</summary>
    private static string BuildGraphicsInfoJson()
    {
        var windowSize = _window.Size;
        var framebufferSize = _window.FramebufferSize;
        TryGetWindowContentScale(out float contentScaleX, out float contentScaleY);
        var json = new StringBuilder(512);
        json.Append("{\"vendor\":").Append(QuoteJson(_glVendor ?? string.Empty))
            .Append(",\"renderer\":").Append(QuoteJson(_glRenderer ?? string.Empty))
            .Append(",\"version\":").Append(QuoteJson(_glVersion ?? string.Empty))
            .Append(",\"shadingLanguageVersion\":").Append(QuoteJson(_glShadingLanguageVersion ?? string.Empty))
            .Append(",\"maxTextureSize\":").Append(_glMaxTextureSize.ToString(CultureInfo.InvariantCulture))
            .Append(",\"swapInterval\":").Append(_window.VSync ? '1' : '0')
            .Append(",\"windowWidth\":").Append(windowSize.X.ToString(CultureInfo.InvariantCulture))
            .Append(",\"windowHeight\":").Append(windowSize.Y.ToString(CultureInfo.InvariantCulture))
            .Append(",\"framebufferWidth\":").Append(framebufferSize.X.ToString(CultureInfo.InvariantCulture))
            .Append(",\"framebufferHeight\":").Append(framebufferSize.Y.ToString(CultureInfo.InvariantCulture))
            .Append(",\"contentScaleX\":").Append(contentScaleX.ToString("0.###", CultureInfo.InvariantCulture))
            .Append(",\"contentScaleY\":").Append(contentScaleY.ToString("0.###", CultureInfo.InvariantCulture))
            .Append(",\"glfwPlatform\":").Append(QuoteJson(_windowBackend))
            .Append(",\"software\":").Append(_graphicsClassification.IsSoftware ? "true" : "false")
            .Append(",\"family\":").Append(QuoteJson(_graphicsClassification.Family.ToString()))
            .Append(",\"summary\":").Append(QuoteJson(_graphicsClassification.Summary ?? string.Empty))
            .Append(",\"glDebug\":").Append(_glDebugEnabled ? "true" : "false")
            .Append(",\"khrDebugCallback\":").Append(_glDebugCallbackInstalled ? "true" : "false")
            .Append('}');
        return json.ToString();
    }

    /// <summary><c>glfwGetWindowContentScale</c> (not wrapped by Silk.NET 2.x); 0 when unavailable.</summary>
    private static unsafe void TryGetWindowContentScale(out float x, out float y)
    {
        x = 0f;
        y = 0f;
        nint window = _window.Native?.Glfw ?? 0;
        if (window == 0 || _glfwApi == null || !_glfwApi.Context.TryGetProcAddress("glfwGetWindowContentScale", out nint proc))
            return;

        float sx, sy;
        ((delegate* unmanaged[Cdecl]<nint, float*, float*, void>)proc)(window, &sx, &sy);
        x = sx;
        y = sy;
    }

    private static void CheckGlErrorsIfDebugging(string stage)
    {
        if (_glDebugEnabled)
            GlDebugOutput.CheckErrors(_gl, stage);
    }

    private static long FrameTimestamp() => System.Diagnostics.Stopwatch.GetTimestamp();

    /// <summary>Swaps and records the swap duration, present interval and input-to-swap latency.</summary>
    private static void SwapBuffersTimed()
    {
        long start = FrameTimestamp();
        _window.SwapBuffers();
        _frameTimings.RecordSwap(start, FrameTimestamp());
    }

    private static void AppendFrameTimingsJson(StringBuilder json) => _frameTimings.AppendJson(json);

    /// <summary>
    /// Takes ownership of control commands that answer later: <c>WAIT_FOR_IDLE[:timeoutMs]</c> and
    /// <c>SCREENSHOT:&lt;path&gt;</c>. Returns false for every other command.
    /// </summary>
    private static bool TryDeferControlCommand(string command, TaskCompletionSource<string> completion)
    {
        const string WaitForIdle = "WAIT_FOR_IDLE";
        const string Screenshot = "SCREENSHOT:";
        if (command.StartsWith(WaitForIdle, StringComparison.OrdinalIgnoreCase))
        {
            ReadOnlySpan<char> rest = command.AsSpan(WaitForIdle.Length).Trim();
            int timeoutMs = WaitForIdleDefaultTimeoutMs;
            if (!rest.IsEmpty)
            {
                if (rest[0] != ':' ||
                    !int.TryParse(rest[1..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out timeoutMs))
                {
                    completion.TrySetResult("ERROR usage: WAIT_FOR_IDLE[:timeoutMs]");
                    return true;
                }
                timeoutMs = Math.Clamp(timeoutMs, 0, WaitForIdleMaxTimeoutMs);
            }

            _idleWaiters.Add(new IdleWaiter(completion, GetClockMilliseconds() + timeoutMs));
            return true;
        }

        if (command.StartsWith(Screenshot, StringComparison.OrdinalIgnoreCase))
        {
            string path = command[Screenshot.Length..].Trim();
            if (path.Length == 0)
            {
                completion.TrySetResult("ERROR usage: SCREENSHOT:<path>");
                return true;
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(path);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                completion.TrySetResult($"ERROR invalid path: {exception.Message}");
                return true;
            }

            _screenshotRequests.Add(new ScreenshotRequest(completion, fullPath));
            // Capture happens in the next presented frame; make sure there is one.
            WindowPresentationGate.Invalidate(WindowFrameReason.Overlay);
            return true;
        }

        return false;
    }

    /// <summary>
    /// The pipeline is quiet: no pending frame reasons, no pending wake, every visible pane presented at its current
    /// buffer generation with no unparsed PTY output, no deferred image upload and no synchronized-update hold.
    /// </summary>
    private static bool IsPipelineIdle(int framebufferWidth, int framebufferHeight)
    {
        if (WindowPresentationGate.PendingReasons != WindowFrameReason.None ||
            HostWake.IsPending ||
            _syncHeld ||
            _renderer.ImageUploadsDeferred ||
            framebufferWidth != _committedFramebufferWidth ||
            framebufferHeight != _committedFramebufferHeight ||
            _atlas.ContentVersion != _committedAtlasVersion)
        {
            return false;
        }

        LeafPane[] leaves = _visibleLeaves;
        for (int i = 0; i < _visibleLeafCount; i++)
        {
            var session = leaves[i].Session;
            if (session.PendingOutputChunks != 0 ||
                !TryReadGeneration(session.Adapter.Buffer, out ulong generation) ||
                !_committedGenerations.TryGetValue(leaves[i], out ulong committed) ||
                committed != generation)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Called where a frame found nothing to do: answers every parked <c>WAIT_FOR_IDLE</c> if quiet.</summary>
    private static void CompleteIdleWaitersIfIdle(int framebufferWidth, int framebufferHeight)
    {
        if (_idleWaiters.Count == 0 || !IsPipelineIdle(framebufferWidth, framebufferHeight))
            return;

        for (int i = 0; i < _idleWaiters.Count; i++)
            _idleWaiters[i].Completion.TrySetResult("IDLE");
        _idleWaiters.Clear();
    }

    /// <summary>Answers <c>TIMEOUT</c> to waiters past their deadline; called once per frame.</summary>
    private static void ExpireIdleWaiters(long nowMs)
    {
        for (int i = _idleWaiters.Count - 1; i >= 0; i--)
        {
            if (nowMs < _idleWaiters[i].DeadlineMs)
                continue;
            _idleWaiters[i].Completion.TrySetResult("TIMEOUT");
            _idleWaiters.RemoveAt(i);
        }
    }

    /// <summary>Milliseconds until the nearest <c>WAIT_FOR_IDLE</c> deadline; 0 when none is parked.</summary>
    private static long IdleWaiterRemainingMs(long nowMs)
    {
        if (_idleWaiters.Count == 0)
            return 0;

        long nearest = long.MaxValue;
        for (int i = 0; i < _idleWaiters.Count; i++)
            nearest = Math.Min(nearest, _idleWaiters[i].DeadlineMs);
        return Math.Max(1L, nearest - nowMs);
    }

    /// <summary>
    /// Reads the freshly rendered back buffer for parked <c>SCREENSHOT:</c> requests. Must run after the frame's
    /// last draw and before <c>SwapBuffers</c> (reading the back buffer after a swap is undefined, notably on
    /// Wayland). PNG encoding and file writes run on the thread pool.
    /// </summary>
    private static void CaptureScreenshotsBeforeSwap(int framebufferWidth, int framebufferHeight)
    {
        if (_screenshotRequests.Count == 0)
            return;

        var requests = _screenshotRequests.ToArray();
        _screenshotRequests.Clear();
        byte[] pixels;
        try
        {
            pixels = new byte[checked(framebufferWidth * framebufferHeight * 4)];
            _renderer.ReadBackBuffer(framebufferWidth, framebufferHeight, pixels);
        }
        catch (Exception exception)
        {
            for (int i = 0; i < requests.Length; i++)
                requests[i].Completion.TrySetResult($"ERROR {exception.Message}");
            return;
        }

        bool opaque = !_window.TransparentFramebuffer;
        _ = Task.Run(() =>
        {
            byte[] png;
            try
            {
                png = FramebufferScreenshot.EncodePng(pixels, framebufferWidth, framebufferHeight, opaque);
            }
            catch (Exception exception)
            {
                for (int i = 0; i < requests.Length; i++)
                    requests[i].Completion.TrySetResult($"ERROR {exception.Message}");
                return;
            }

            for (int i = 0; i < requests.Length; i++)
            {
                try
                {
                    File.WriteAllBytes(requests[i].Path, png);
                    requests[i].Completion.TrySetResult(
                        string.Create(CultureInfo.InvariantCulture, $"OK {framebufferWidth}x{framebufferHeight} {requests[i].Path}"));
                }
                catch (Exception exception)
                {
                    requests[i].Completion.TrySetResult($"ERROR {exception.Message}");
                }
            }
        });
    }

    /// <summary>Host shutdown: parked requests can never be answered normally.</summary>
    private static void FailParkedControlRequests()
    {
        for (int i = 0; i < _idleWaiters.Count; i++)
            _idleWaiters[i].Completion.TrySetResult("ERROR host is closed");
        _idleWaiters.Clear();
        FailScreenshotRequests("host is closed");
    }

    /// <summary>Answers every parked <c>SCREENSHOT:</c> request with <c>ERROR</c> <paramref name="error"/>.</summary>
    private static void FailScreenshotRequests(string error)
    {
        for (int i = 0; i < _screenshotRequests.Count; i++)
            _screenshotRequests[i].Completion.TrySetResult($"ERROR {error}");
        _screenshotRequests.Clear();
    }
}
