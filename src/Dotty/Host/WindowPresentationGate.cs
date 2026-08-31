using Dotty.Terminal.Adapter;

namespace Dotty.Silk;

public static class WindowPresentationGate
{
    public static bool ShouldPresent(TerminalAdapter? adapter) =>
        adapter is null || !adapter.SynchronizedUpdateActive;
}
