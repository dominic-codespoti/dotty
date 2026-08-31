using System.Collections.Generic;
using Dotty.Silk;
using Xunit;

namespace Dotty.App.Tests;

public sealed class WindowFocusRouterTests
{
    [Fact]
    public void DuplicateFocusStatesAreIgnored()
    {
        bool? last = null;
        var reports = new List<bool>();

        WindowFocusRouter.Route(ref last, focused: true, closed: false, reports.Add);
        WindowFocusRouter.Route(ref last, focused: true, closed: false, reports.Add);
        WindowFocusRouter.Route(ref last, focused: false, closed: false, reports.Add);
        WindowFocusRouter.Route(ref last, focused: false, closed: false, reports.Add);

        Assert.Equal(new[] { true, false }, reports);
    }

    [Fact]
    public void NullActiveTabDoesNotReportAndTransitionIsRemembered()
    {
        bool? last = null;
        var reports = new List<bool>();

        WindowFocusRouter.Route(ref last, focused: true, closed: false, report: null);
        WindowFocusRouter.Route(ref last, focused: true, closed: false, reports.Add);

        Assert.Empty(reports);
        Assert.True(last);
    }

    [Fact]
    public void ActiveTabAtTransitionReceivesOnlyThatTransition()
    {
        bool? last = null;
        string? activeTab = "first";
        var reports = new List<string>();

        WindowFocusRouter.Route(ref last, true, false, focused => reports.Add($"{activeTab}:{focused}"));
        activeTab = "second";
        WindowFocusRouter.Route(ref last, false, false, focused => reports.Add($"{activeTab}:{focused}"));

        Assert.Equal(new[] { "first:True", "second:False" }, reports);
    }

    [Fact]
    public void CloseBeforeCallbackSuppressesReportButStoresState()
    {
        bool? last = null;
        var reports = new List<bool>();

        WindowFocusRouter.Route(ref last, true, closed: true, reports.Add);
        WindowFocusRouter.Route(ref last, true, closed: false, reports.Add);

        Assert.Empty(reports);
        Assert.True(last);
    }
}
