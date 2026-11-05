using System;
using Dotty.Runtime.Config;
using Dotty.Runtime.Input;
using Dotty.Terminal.Adapter;
using Silk.NET.Input;

namespace Dotty.Silk;

/// <summary>Pointer shape resolution (OSC 22, <c>mouse.hideWhileTyping</c>) and file drops.</summary>
internal static partial class DottyWindowHost
{
    private static PointerShape _hostPointerShape = PointerShape.Text;
    private static PointerShape _appliedPointerShape = (PointerShape)(-1);
    private static bool _pointerHiddenWhileTyping;

    /// <summary>The mouse controller's choice for the pointer's context; resolved against OSC 22 and typing.</summary>
    private static void SetHostPointer(StandardCursor cursor)
    {
        _hostPointerShape = FromStandardCursor(cursor);
        _pointerHiddenWhileTyping = false;
        ApplyPointerShape();
    }

    private static void HidePointerWhileTyping()
    {
        if (_pointerHiddenWhileTyping || !UserConfigService.Current.Mouse.HideWhileTyping)
            return;
        _pointerHiddenWhileTyping = true;
        ApplyPointerShape();
    }

    private static void ApplyPointerShape()
    {
        if (_mouse is null)
            return;
        var app = _tabManager?.ActiveTab?.ActivePane.Session.Adapter.RequestedPointerShape ?? PointerShape.Default;
        var shape = PointerShapePolicy.Resolve(_hostPointerShape, app, _pointerHiddenWhileTyping);
        if (shape == _appliedPointerShape)
            return;
        _appliedPointerShape = shape;
        if (shape == PointerShape.Hidden)
        {
            _mouse.Cursor.CursorMode = CursorMode.Hidden;
            return;
        }
        _mouse.Cursor.CursorMode = CursorMode.Normal;
        _mouse.Cursor.StandardCursor = ToStandardCursor(shape);
    }

    private static PointerShape FromStandardCursor(StandardCursor cursor) => cursor switch
    {
        StandardCursor.IBeam => PointerShape.Text,
        StandardCursor.Hand => PointerShape.Pointer,
        StandardCursor.HResize => PointerShape.EwResize,
        StandardCursor.VResize => PointerShape.NsResize,
        StandardCursor.Crosshair => PointerShape.Crosshair,
        StandardCursor.Arrow => PointerShape.Arrow,
        _ => PointerShape.Default,
    };

    private static StandardCursor ToStandardCursor(PointerShape shape) => shape switch
    {
        PointerShape.Arrow => StandardCursor.Arrow,
        PointerShape.Text => StandardCursor.IBeam,
        PointerShape.Pointer => StandardCursor.Hand,
        PointerShape.Crosshair => StandardCursor.Crosshair,
        PointerShape.EwResize => StandardCursor.HResize,
        PointerShape.NsResize => StandardCursor.VResize,
        PointerShape.NwseResize => StandardCursor.NwseResize,
        PointerShape.NeswResize => StandardCursor.NeswResize,
        PointerShape.Move => StandardCursor.ResizeAll,
        PointerShape.NotAllowed => StandardCursor.NotAllowed,
        PointerShape.Wait => StandardCursor.Wait,
        PointerShape.Progress => StandardCursor.WaitArrow,
        _ => StandardCursor.Default,
    };

    /// <summary><c>drop.mode</c> paste-path: shell-escape the dropped paths and paste them into the active pane.</summary>
    private static void OnFilesDropped(string[] paths)
    {
        if (_closed || paths is null || paths.Length == 0)
            return;
        var drop = UserConfigService.Current.Drop;
        if (string.Equals(drop.Mode?.Trim(), "ignore", StringComparison.OrdinalIgnoreCase))
            return;
        var pane = _tabManager?.ActiveTab?.ActivePane;
        if (pane is null || _pasteRouter is null)
            return;
        var style = DropPathEscaper.ResolveStyle(drop.Quote, pane.Session.LaunchShell, OperatingSystem.IsWindows());
        string? text = DropPathEscaper.Build(paths, style, out _);
        if (text is null)
            return;
        RecordInteraction();
        _pasteRouter.PasteText(pane, text);
        WindowPresentationGate.Invalidate(WindowFrameReason.Input | WindowFrameReason.Overlay);
    }
}
