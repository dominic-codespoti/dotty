using System;
namespace Dotty.Terminal.Adapter;

/// <summary>DEC private mode 4 (DECSCLM) state and bounded presentation timing.</summary>
public partial class TerminalAdapter
{
    private const long SmoothScrollDurationPerLineMs = 160;
    private const long SmoothScrollMaximumDurationMs = 640;
    private bool _smoothScrollEnabled;
    private bool _smoothScrollActive;
    private long _smoothScrollStartedAtMs;
    private long _smoothScrollDurationMs;
    private int _smoothScrollDirection;
    private int _smoothScrollLines;
    private int _smoothScrollTop;
    private int _smoothScrollBottom;
    private RenderSnapshot? _pendingSmoothScrollSnapshot;
    private RenderSnapshot? _smoothScrollOutgoingSnapshot;
    public object SmoothScrollSyncRoot { get; } = new();
    /// <summary>Whether DECSET 4 currently requests smooth presentation of terminal scrolls.</summary>
    public bool SmoothScrollEnabled => _smoothScrollEnabled;
    public bool SmoothScrollActive => _smoothScrollActive;

    /// <summary>Monotonic animation epoch, exposed internally so deterministic compositor tests can advance exact frames.</summary>
    internal long SmoothScrollStartedAtMs => _smoothScrollStartedAtMs;

    /// <summary>True while the most recently coalesced scroll transition is still being presented.</summary>
    public RenderSnapshot? SmoothScrollOutgoingSnapshot => _smoothScrollOutgoingSnapshot;

    /// <summary>Normalized transition progress in [0,1]; consumers use it to move outgoing/incoming rows.</summary>
    public float SmoothScrollProgress { get; private set; } = 1f;
    public int SmoothScrollDirection => _smoothScrollDirection;
    public int SmoothScrollTop => _smoothScrollTop;
    public int SmoothScrollBottom => _smoothScrollBottom;
    public int SmoothScrollLines => _smoothScrollLines;

    /// <summary>Raised only when DECSET/DECRST 4 changes effective mode state.</summary>
    public event Action<bool>? SmoothScrollModeChanged;

    /// <summary>Set by DECSET/DECRST 4. Disabling immediately terminates any in-flight transition.</summary>
    public void OnSetSmoothScroll(bool enabled)
    {
        lock (SmoothScrollSyncRoot)
        {
            if (_smoothScrollEnabled == enabled)
                return;

            _smoothScrollEnabled = enabled;
            _buffer.SmoothScrollRequested = enabled ? OnBufferSmoothScrollRequested : null;
            if (!enabled)
            {
                _pendingSmoothScrollSnapshot?.Dispose();
                _pendingSmoothScrollSnapshot = null;
                _smoothScrollOutgoingSnapshot?.Dispose();
                _smoothScrollOutgoingSnapshot = null;
                StopSmoothScroll();
            }
        }
        SmoothScrollModeChanged?.Invoke(enabled);
        RequestRender();
    }

    /// <summary>
    /// Starts or coalesces a visible scroll transition. A burst never queues stale frames: it restarts one bounded
    /// transition from the latest terminal state. <paramref name="nowMs"/> is the host's monotonic millisecond clock.
    /// Called only for scrolls which actually moved rows.
    /// </summary>
    private void BeginSmoothScroll(int direction, int lines, long nowMs)
    {
        if (!_smoothScrollEnabled || direction == 0 || lines <= 0)
            return;

        lock (SmoothScrollSyncRoot)
        {
            if (!_smoothScrollEnabled)
                return;
            _smoothScrollDirection = direction < 0 ? -1 : 1;
            _smoothScrollLines = lines;
            _smoothScrollStartedAtMs = nowMs;
            _smoothScrollDurationMs = Math.Min(SmoothScrollMaximumDurationMs,
                Math.Max(SmoothScrollDurationPerLineMs, (long)lines * SmoothScrollDurationPerLineMs));
            SmoothScrollProgress = 0f;
            _smoothScrollActive = true;
        }
    }

    /// <summary>Advances the bounded transition using the host monotonic clock; returns true while another frame is needed.</summary>
    public bool AdvanceSmoothScrollFrame(long nowMs)
    {
        if (!_smoothScrollActive)
            return false;

        lock (SmoothScrollSyncRoot)
        {
            if (!_smoothScrollActive)
                return false;

            long elapsed = Math.Max(0, nowMs - _smoothScrollStartedAtMs);
            if (elapsed >= _smoothScrollDurationMs)
            {
                SmoothScrollProgress = 1f;
                _smoothScrollActive = false;
                return true;
            }

            float t = (float)elapsed / _smoothScrollDurationMs;
            float remaining = 1f - t;
            SmoothScrollProgress = 1f - remaining * remaining * remaining;
            return true;
        }
    }

    private void OnBufferSmoothScrollRequested(int direction, int lines, int top, int bottom, bool before)
    {
        lock (SmoothScrollSyncRoot)
        {
            if (before)
            {
                _smoothScrollTop = top;
                _smoothScrollBottom = bottom;
                _pendingSmoothScrollSnapshot?.Dispose();
                _pendingSmoothScrollSnapshot = _buffer.CaptureRenderSnapshotVisible();
                return;
            }
            _smoothScrollTop = top;
            _smoothScrollBottom = bottom;
            if (lines <= 0 || _pendingSmoothScrollSnapshot is null)
            {
                _pendingSmoothScrollSnapshot?.Dispose();
                _pendingSmoothScrollSnapshot = null;
                return;
            }

            _smoothScrollOutgoingSnapshot?.Dispose();
            _smoothScrollOutgoingSnapshot = _pendingSmoothScrollSnapshot;
            _pendingSmoothScrollSnapshot = null;
            BeginSmoothScroll(direction, lines, System.Diagnostics.Stopwatch.GetTimestamp() * 1000
                / System.Diagnostics.Stopwatch.Frequency);
        }
    }


    public void CompleteSmoothScrollComposition()
    {
        _smoothScrollOutgoingSnapshot?.Dispose();
        _smoothScrollOutgoingSnapshot = null;
        if (!_smoothScrollActive)
        {
            _smoothScrollStartedAtMs = 0;
            _smoothScrollDurationMs = 0;
            _smoothScrollDirection = 0;
            _smoothScrollLines = 0;
        }
    }
    private void StopSmoothScroll()
    {
        _smoothScrollActive = false;
        _smoothScrollStartedAtMs = 0;
        _smoothScrollDurationMs = 0;
        SmoothScrollProgress = 1f;
        _smoothScrollDirection = 0;
        _smoothScrollLines = 0;
    }
    /// <summary>Cancels and releases an in-flight visual transition without changing DEC mode 4.</summary>
    public void ResetSmoothScrollAnimation()
    {
        lock (SmoothScrollSyncRoot)
        {
            _pendingSmoothScrollSnapshot?.Dispose();
            _pendingSmoothScrollSnapshot = null;
            _smoothScrollOutgoingSnapshot?.Dispose();
            _smoothScrollOutgoingSnapshot = null;
            StopSmoothScroll();
        }
    }
}
