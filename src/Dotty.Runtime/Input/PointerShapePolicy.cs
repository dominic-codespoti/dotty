using Dotty.Terminal.Adapter;

namespace Dotty.Runtime.Input;

/// <summary>
/// Pure pointer-shape priority: hidden-while-typing beats everything; host chrome and interaction shapes (resize,
/// links, scrollbar, tab bar, menus) beat the application; an OSC 22 shape replaces only the plain text beam over
/// pane content.
/// </summary>
public static class PointerShapePolicy
{
    /// <param name="hostShape">The shape the host wants for the pointer's context.</param>
    /// <param name="applicationShape">The active pane's OSC 22 request.</param>
    /// <param name="hiddenWhileTyping">Pointer hidden by <c>mouse.hideWhileTyping</c> since the last pointer motion.</param>
    public static PointerShape Resolve(PointerShape hostShape, PointerShape applicationShape, bool hiddenWhileTyping)
    {
        if (hiddenWhileTyping)
            return PointerShape.Hidden;
        if (hostShape == PointerShape.Text && applicationShape is not (PointerShape.Default or PointerShape.Hidden))
            return applicationShape;
        return hostShape;
    }
}
