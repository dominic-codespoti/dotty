using System;
using System.Collections.Generic;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Dotty.App.Controls;

namespace Dotty.App.Controls.Canvas.Rendering;

/// <summary>
/// One semantic automation surface for the terminal: a meaningful name, the
/// visible viewport text queryable on demand, and no children — never an
/// automation peer per cell. All work is lazy (AT-driven), so accessibility
/// adds no per-frame cost while unused.
/// </summary>
internal sealed class TerminalCanvasAutomationPeer : ControlAutomationPeer
{
    private readonly TerminalCanvas _owner;

    internal TerminalCanvasAutomationPeer(TerminalCanvas owner)
        : base(owner)
    {
        _owner = owner;
    }

    protected override string? GetNameCore() => "Terminal";

    protected override string? GetAutomationIdCore() => "PART_TerminalCanvas";

    protected override string GetClassNameCore() => "TerminalCanvas";

    protected override AutomationControlType GetAutomationControlTypeCore() =>
        AutomationControlType.Custom;

    protected override IReadOnlyList<AutomationPeer> GetOrCreateChildrenCore() =>
        Array.Empty<AutomationPeer>();

    protected override string? GetHelpTextCore() => _owner.GetVisibleTextForAccessibility();
}
