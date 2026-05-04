using Dotty.App.ViewModels;
using Xunit;

namespace Dotty.App.Tests;

public class TabViewModelTitleTests
{
    [Fact]
    public void Title_DefaultsToTerminal()
    {
        var tab = new TabViewModel();

        Assert.Equal("Terminal", tab.Title);
    }

    [Fact]
    public void SessionTitle_UpdatesVisibleTitle_WhenNoOverrideExists()
    {
        var tab = new TabViewModel();

        tab.SetSessionTitle("build logs");

        Assert.Equal("build logs", tab.Title);
    }

    [Fact]
    public void UserTitleOverride_TakesPrecedenceOverSessionTitle()
    {
        var tab = new TabViewModel();
        tab.SetSessionTitle("shell title");

        tab.Title = "Pinned";
        tab.SetSessionTitle("updated shell title");

        Assert.Equal("Pinned", tab.Title);
    }

    [Fact]
    public void EmptyUserTitleOverride_FallsBackToSessionTitle()
    {
        var tab = new TabViewModel();
        tab.SetSessionTitle("shell title");
        tab.Title = "Pinned";

        tab.Title = "   ";

        Assert.Equal("shell title", tab.Title);
    }
}
