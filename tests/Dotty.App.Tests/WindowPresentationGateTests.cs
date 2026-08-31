using Dotty.Silk;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Parser;
using Xunit;

namespace Dotty.App.Tests;

public sealed class WindowPresentationGateTests
{
    [Fact]
    public void NullAdapterIsPresentable()
    {
        Assert.True(WindowPresentationGate.ShouldPresent(null));
    }

    [Fact]
    public void Mode2026SuppressesPresentationUntilDisabled()
    {
        var adapter = new TerminalAdapter(2, 8);
        var parser = new BasicAnsiParser { Handler = adapter };

        parser.Feed("\x1b[?2026h"u8);
        Assert.False(WindowPresentationGate.ShouldPresent(adapter));

        parser.Feed("\x1b[?2026l"u8);
        Assert.True(WindowPresentationGate.ShouldPresent(adapter));
    }
}
