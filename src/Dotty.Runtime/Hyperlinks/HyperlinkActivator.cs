using System;
using System.Threading;
using System.Threading.Tasks;

namespace Dotty.Runtime.Hyperlinks;

/// <summary>What the host should do after <see cref="HyperlinkActivator.Activate"/>.</summary>
public enum HyperlinkActivationKind
{
    /// <summary>A Lua <c>open_url</c> hook handled the link; nothing else to do.</summary>
    HandledByLua,

    /// <summary>The link is allowed without confirmation; the host passes the target to <see cref="HyperlinkActivator.OpenApprovedAsync"/>.</summary>
    Open,

    /// <summary>The user must confirm first (see <see cref="HyperlinkActivation.Risks"/>).</summary>
    NeedsConfirmation,

    /// <summary>The link must not be opened; <see cref="HyperlinkActivation.Message"/> says why.</summary>
    Blocked,
}

/// <summary>Result of <see cref="HyperlinkActivator.Activate"/>.</summary>
/// <param name="Kind">The required host action.</param>
/// <param name="Target">Canonical target for <see cref="HyperlinkActivationKind.Open"/> and <see cref="HyperlinkActivationKind.NeedsConfirmation"/>.</param>
/// <param name="Risks">Why confirmation is needed.</param>
/// <param name="Message">Block reason for <see cref="HyperlinkActivationKind.Blocked"/>.</param>
public readonly record struct HyperlinkActivation(
    HyperlinkActivationKind Kind,
    HyperlinkTarget? Target,
    HyperlinkRisk Risks,
    string? Message);

/// <summary>
/// Orchestrates link activation: structural validation, then the Lua <c>open_url</c> hook (which sees
/// only canonical, control-character-free URLs), then scheme policy and risk classification.
/// <see cref="Activate"/> performs no I/O and is safe on the UI thread; opening is a separate,
/// asynchronous step so the host can interpose a confirmation prompt.
/// </summary>
public sealed class HyperlinkActivator
{
    private readonly Func<HyperlinkOptions> _options;
    private readonly Func<string, HyperlinkSource, bool> _luaHook;
    private readonly IHyperlinkOpener _opener;
    private readonly string? _machineName;
    private readonly bool? _isWindows;

    /// <param name="options">Reads the current options on every activation (hot reload).</param>
    /// <param name="luaHook">Runs the <c>open_url</c> hook; true means handled.</param>
    /// <param name="opener">Launches approved targets.</param>
    /// <param name="machineName">Overrides the local machine name for <c>file:</c> host checks (tests).</param>
    /// <param name="isWindows">Overrides the OS for path rules (tests).</param>
    public HyperlinkActivator(
        Func<HyperlinkOptions> options,
        Func<string, HyperlinkSource, bool> luaHook,
        IHyperlinkOpener opener,
        string? machineName = null,
        bool? isWindows = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(luaHook);
        ArgumentNullException.ThrowIfNull(opener);
        _options = options;
        _luaHook = luaHook;
        _opener = opener;
        _machineName = machineName;
        _isWindows = isWindows;
    }

    /// <summary>Decides what to do with a clicked link.</summary>
    /// <param name="rawUrl">Unmodified URL text or OSC 8 target.</param>
    /// <param name="source">Detected text or explicit OSC 8.</param>
    /// <param name="displayText">Visible text of an explicit link; null for detected links.</param>
    public HyperlinkActivation Activate(string? rawUrl, HyperlinkSource source, string? displayText)
    {
        HyperlinkOptions options = _options();
        if (source == HyperlinkSource.Explicit && !options.Osc8)
            return Blocked("OSC 8 hyperlinks are disabled (hyperlinks.osc8)");

        var policy = new HyperlinkPolicy(options, _machineName, _isWindows);
        if (!policy.TryCanonicalize(rawUrl, out string? canonical, out string? reason))
            return Blocked(reason);

        if (RunHook(canonical, source))
            return new HyperlinkActivation(HyperlinkActivationKind.HandledByLua, null, HyperlinkRisk.None, null);

        HyperlinkDecision decision = policy.Evaluate(rawUrl, source, displayText);
        return decision.Verdict switch
        {
            HyperlinkVerdict.Open => new HyperlinkActivation(HyperlinkActivationKind.Open, decision.Target, decision.Risks, null),
            HyperlinkVerdict.Confirm => new HyperlinkActivation(HyperlinkActivationKind.NeedsConfirmation, decision.Target, decision.Risks, null),
            _ => Blocked(decision.BlockReason ?? "link is not allowed"),
        };
    }

    /// <summary>Opens a target previously approved by <see cref="Activate"/> (and, if required, by the user).</summary>
    public Task<HyperlinkOpenResult> OpenApprovedAsync(HyperlinkTarget target, CancellationToken ct = default) =>
        _opener.OpenAsync(target, ct);

    private bool RunHook(string canonical, HyperlinkSource source)
    {
        try
        {
            return _luaHook(canonical, source);
        }
        catch (Exception)
        {
            // A failing hook must never bypass the default pipeline.
            return false;
        }
    }

    private static HyperlinkActivation Blocked(string reason) =>
        new(HyperlinkActivationKind.Blocked, null, HyperlinkRisk.None, reason);
}
