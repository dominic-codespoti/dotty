using System.Numerics;
using Dotty.Silk.Input;
using System.Text;
using Silk.NET.Input;
using Dotty.Silk;
using System;
using Dotty.Runtime.Config;
using Dotty.Runtime.Input;
using Dotty.Runtime.Scripting;
using Dotty.Runtime.Tabs;
using Xunit;

namespace Dotty.App.Tests;

[Collection("Allocation-sensitive tests")]
public sealed class InputAllocationTests
{
    [Fact]
    public void TryGetAction_BoundAndUnboundChordsAllocateNothingAfterWarmup()
    {
        var bindings = new KeybindingManager();
        bindings.Bind("ctrl+shift+k", Dotty.Abstractions.Config.TerminalAction.Copy);
        for (int i = 0; i < 100; i++)
        {
            bindings.TryGetAction(true, true, false, false, "K", out _);
            bindings.TryGetAction(false, false, false, false, "Z", out _);
        }

        AllocationAssert.NoAllocations(() =>
        {
            for (int i = 0; i < 100; i++)
            {
                bindings.TryGetAction(true, true, false, false, "K", out _);
                bindings.TryGetAction(false, false, false, false, "Z", out _);
            }
        }, measuredIterationsPerWindow: 1);
    }

    [Fact]
    public void LuaTryExecute_UnboundChordAllocatesNothingAfterWarmup()
    {
        using var tabs = new TerminalTabManager(SilentPty.Create);
        using var host = new LuaScriptHost(new FakeLuaHostServices(tabs), tabs);
        host.Evaluate(new DottyUserConfig(), null);

        for (int i = 0; i < 100; i++)
            host.Keybinds.TryExecute(false, false, false, false, "Z");

        AllocationAssert.NoAllocations(
            () => host.Keybinds.TryExecute(false, false, false, false, "Z"),
            measuredIterationsPerWindow: 100);
    }



    [Fact]
    public void KeyAndMouseEncodingIntoSpansAllocatesNothingAfterWarmup()
    {
        var encoder = new TerminalInputEncoder();
        var bytes = new byte[64];
        int length = SilkKeyMapper.Encode(Key.Up, false, true, false, false, bytes);
        Assert.Equal("\x1b[1;2A", System.Text.Encoding.ASCII.GetString(bytes.AsSpan(0, length)));
        length = encoder.Encode(TerminalKey.Delete, TerminalKeyModifiers.Control, bytes);
        Assert.Equal("\x1b[3;5~", System.Text.Encoding.ASCII.GetString(bytes.AsSpan(0, length)));
        length = encoder.EncodeMouseEvent(
            Dotty.Terminal.Adapter.TerminalAdapter.MouseMode.Normal,
            Dotty.Terminal.Adapter.TerminalAdapter.MouseEncoding.SGR,
            0, 3, 4, true, false, TerminalKeyModifiers.None, bytes);
        Assert.Equal("\x1b[<0;5;4M", System.Text.Encoding.ASCII.GetString(bytes.AsSpan(0, length)));
        for (int i = 0; i < 100; i++)
        {
            SilkKeyMapper.Encode(Key.Up, false, true, false, false, bytes);
            encoder.Encode(TerminalKey.Delete, TerminalKeyModifiers.Control, bytes);
            encoder.EncodeMouseEvent(
                Dotty.Terminal.Adapter.TerminalAdapter.MouseMode.Normal,
                Dotty.Terminal.Adapter.TerminalAdapter.MouseEncoding.SGR,
                0, 3, 4, true, false, TerminalKeyModifiers.None, bytes);
        }

        AllocationAssert.NoAllocations(() =>
        {
            for (int i = 0; i < 100; i++)
            {
                SilkKeyMapper.Encode(Key.Up, false, true, false, false, bytes);
                encoder.Encode(TerminalKey.Delete, TerminalKeyModifiers.Control, bytes);
                encoder.EncodeMouseEvent(
                    Dotty.Terminal.Adapter.TerminalAdapter.MouseMode.Normal,
                    Dotty.Terminal.Adapter.TerminalAdapter.MouseEncoding.SGR,
                    0, 3, 4, true, false, TerminalKeyModifiers.None, bytes);
            }
        }, measuredIterationsPerWindow: 1);
    }

    [Fact]
    public void DispatcherKeyEventsAndTextAllocateNothingAfterWarmup()
    {
        using var host = new TerminalKeyboardDispatcherTests.FakeHost
        {
            ActiveTab = new TerminalTab(rows: 24, columns: 80)
        };
        using var dispatcher = new TerminalKeyboardDispatcher(host);
        for (int i = 0; i < 300; i++)
        {
            dispatcher.HandleKeyEvent(Key.A, 0, 'a', TerminalKeyEventType.Press, ReadOnlySpan<char>.Empty);
            dispatcher.HandleKeyEvent(Key.A, 0, 'a', TerminalKeyEventType.Press, "a".AsSpan());
        }
        host.InputBytes.Clear();

        AllocationAssert.NoAllocations(() =>
        {
            for (int i = 0; i < 100; i++)
            {
                dispatcher.HandleKeyEvent(Key.A, 0, 'a', TerminalKeyEventType.Press, ReadOnlySpan<char>.Empty);
                dispatcher.HandleKeyEvent(Key.A, 0, 'a', TerminalKeyEventType.Press, "a".AsSpan());
            }
        }, measuredIterationsPerWindow: 1);
    }

    [Fact]
    public void MouseMoveOverTerminalAndTabBarAllocatesNothingAfterWarmup()
    {
        using var host = new TerminalMouseControllerTests.FakeTerminalMouseHost();
        var tab = host.TabManager.CreateTab(cols: 80, rows: 24);
        host.TabManager.SelectTab(tab);
        tab.PaneTree.Layout(800, 480, 10, 20);
        tab.ActivePane.Session.Parser.Feed(Encoding.UTF8.GetBytes("https://example.test"));
        host.Ctrl = true;
        var controller = new TerminalMouseController(host);
        for (int i = 0; i < 100; i++)
        {
            controller.HandleMouseMove(null!, new Vector2(30, 5));
            controller.HandleMouseMove(null!, new Vector2(30, 30));
            Assert.Equal(global::Silk.NET.Input.StandardCursor.Hand, host.CurrentCursor);
        }

        AllocationAssert.NoAllocations(() =>
        {
            for (int i = 0; i < 100; i++)
            {
                controller.HandleMouseMove(null!, new Vector2(30, 5));
                controller.HandleMouseMove(null!, new Vector2(30, 30));
            }
        }, measuredIterationsPerWindow: 1);
    }
}