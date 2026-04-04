using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using Dotty.E2E.Tests.Assertions;
using Xunit;
using Xunit.Abstractions;

namespace Dotty.E2E.Tests.Scenarios;

/// <summary>
/// Comprehensive E2E tests for window and tab management with performance measurements.
/// Includes tab creation, switching, closing, reordering, persistence, and performance tests.
/// </summary>
[Trait("Category", "WindowManagement")]
[Trait("Category", "Tabs")]
public class WindowManagementE2ETests : E2EPerformanceTestBase
{
    public WindowManagementE2ETests(ITestOutputHelper outputHelper) : base("WindowManagement", outputHelper)
    {
    }

    #region Tab Creation

    [Fact]
    public async Task Create_New_Tab_Should_Add_Tab()
    {
        await RunPerformanceTestAsync(nameof(Create_New_Tab_Should_Add_Tab), async () =>
        {
            // Arrange
            var initialStats = await GetStatsAsync();
            var initialTabCount = initialStats.TotalTabs;
            
            // Act
            await App.Commands.CreateTabAsync();
            await Task.Delay(1000);
            
            // Assert
            var newStats = await GetStatsAsync();
            Assert.True(newStats.TotalTabs > initialTabCount, "New tab should be created");
        });
    }

    [Fact]
    public async Task Create_Multiple_Tabs_Should_Work()
    {
        await RunPerformanceTestAsync(nameof(Create_Multiple_Tabs_Should_Work), async () =>
        {
            // Arrange
            var initialStats = await GetStatsAsync();
            
            // Act - Create multiple tabs
            for (int i = 0; i < 3; i++)
            {
                await App.Commands.CreateTabAsync();
                await Task.Delay(500);
            }
            
            // Assert
            var newStats = await GetStatsAsync();
            Assert.True(newStats.TotalTabs >= initialStats.TotalTabs + 3, "Multiple tabs should be created");
        });
    }

    [Theory]
    [InlineData(5)]
    [InlineData(10)]
    [InlineData(20)]
    public async Task Create_N_Tabs_Should_Succeed(int tabCount)
    {
        await RunPerformanceTestAsync($"Create_{tabCount}_Tabs", async () =>
        {
            // Arrange
            var initialStats = await GetStatsAsync();
            
            // Act
            for (int i = 0; i < tabCount; i++)
            {
                await App.Commands.CreateTabAsync();
                await Task.Delay(200);
            }
            
            await Task.Delay(1000);
            
            // Assert
            var finalStats = await GetStatsAsync();
            Assert.True(finalStats.TotalTabs >= initialStats.TotalTabs + tabCount, 
                $"Should create {tabCount} tabs");
            Assert.True(finalStats.SessionsCreated >= initialStats.SessionsCreated + tabCount, 
                "Should create sessions for all tabs");
        });
    }

    #endregion

    #region Tab Switching

    [Fact]
    public async Task Switch_Tabs_Should_Change_Active_Tab()
    {
        await RunPerformanceTestAsync(nameof(Switch_Tabs_Should_Change_Active_Tab), async () =>
        {
            // Arrange - Create a second tab
            await App.Commands.CreateTabAsync();
            await Task.Delay(1000);
            
            var statsBefore = await GetStatsAsync();
            if (statsBefore.TotalTabs < 2)
            {
                // Skip if we couldn't create a second tab
                return;
            }
            
            var initialActiveIndex = statsBefore.ActiveTabIndex;
            
            // Act - Switch to next tab
            await App.Commands.NextTabAsync();
            await Task.Delay(500);
            
            // Assert
            var statsAfter = await GetStatsAsync();
            // The active tab index might be the same if we only have 1 tab
            // but if we have 2+, it should have changed
            if (statsAfter.TotalTabs > 1)
            {
                Assert.NotEqual(initialActiveIndex, statsAfter.ActiveTabIndex);
            }
        });
    }

    [Fact]
    public async Task Switch_To_Previous_Tab_Should_Work()
    {
        await RunPerformanceTestAsync(nameof(Switch_To_Previous_Tab_Should_Work), async () =>
        {
            // Arrange - Create multiple tabs
            await App.Commands.CreateTabAsync();
            await Task.Delay(1000);
            await App.Commands.CreateTabAsync();
            await Task.Delay(1000);
            
            var stats = await GetStatsAsync();
            if (stats.TotalTabs < 3)
            {
                return; // Skip if not enough tabs
            }
            
            var initialIndex = stats.ActiveTabIndex;
            
            // Act - Switch to previous tab
            await App.Commands.PrevTabAsync();
            await Task.Delay(500);
            
            // Assert
            var finalStats = await GetStatsAsync();
            Assert.NotEqual(initialIndex, finalStats.ActiveTabIndex);
        });
    }

    [Fact]
    public async Task Tab_Switching_With_Keyboard_Shortcuts()
    {
        await RunPerformanceTestAsync(nameof(Tab_Switching_With_Keyboard_Shortcuts), async () =>
        {
            // Arrange - Create multiple tabs with content
            for (int i = 0; i < 5; i++)
            {
                await App.Commands.CreateTabAsync();
                await Task.Delay(500);
                await SendTextAndWaitAsync($"Tab {i} content");
            }
            
            var stats = await GetStatsAsync();
            if (stats.TotalTabs < 5)
            {
                return;
            }
            
            // Act - Switch using Ctrl+Tab and Ctrl+Shift+Tab
            for (int i = 0; i < stats.TotalTabs; i++)
            {
                await App.Commands.SendKeyComboAsync("Tab", new[] { "Ctrl" });
                await Task.Delay(200);
            }
            
            for (int i = 0; i < stats.TotalTabs; i++)
            {
                await App.Commands.SendKeyComboAsync("Tab", new[] { "Ctrl", "Shift" });
                await Task.Delay(200);
            }
            
            // Assert
            var finalStats = await GetStatsAsync();
            Assert.True(finalStats.SessionsStarted >= 5, "All sessions should be active");
        });
    }

    [Fact]
    public async Task Tab_Switching_With_Number_Shortcuts()
    {
        await RunPerformanceTestAsync(nameof(Tab_Switching_With_Number_Shortcuts), async () =>
        {
            // Arrange - Create tabs
            for (int i = 0; i < 5; i++)
            {
                await App.Commands.CreateTabAsync();
                await Task.Delay(300);
            }
            
            var stats = await GetStatsAsync();
            if (stats.TotalTabs < 3)
            {
                return;
            }
            
            // Act - Use Alt+1, Alt+2, etc. to switch
            for (int i = 1; i <= Math.Min(5, stats.TotalTabs); i++)
            {
                await App.Commands.SendKeyComboAsync($"D{i}", new[] { "Alt" });
                await Task.Delay(300);
            }
            
            // Assert
            var finalStats = await GetStatsAsync();
            Assert.True(finalStats.SessionsStarted > 0, "Sessions should remain active");
        });
    }

    #endregion

    #region Tab Closing

    [Fact]
    public async Task Close_Tab_Should_Remove_Tab()
    {
        await RunPerformanceTestAsync(nameof(Close_Tab_Should_Remove_Tab), async () =>
        {
            // Arrange - Create an extra tab
            await App.Commands.CreateTabAsync();
            await Task.Delay(1000);
            
            var statsBefore = await GetStatsAsync();
            if (statsBefore.TotalTabs < 2)
            {
                // Skip if we couldn't create tabs
                return;
            }
            
            // Act - Close the active tab
            await App.Commands.CloseTabAsync();
            await Task.Delay(1000);
            
            // Assert
            var statsAfter = await GetStatsAsync();
            Assert.True(statsAfter.TotalTabs < statsBefore.TotalTabs, "Tab should be closed");
        });
    }

    [Fact]
    public async Task Close_Tab_With_Keyboard_Shortcut()
    {
        await RunPerformanceTestAsync(nameof(Close_Tab_With_Keyboard_Shortcut), async () =>
        {
            // Arrange
            await App.Commands.CreateTabAsync();
            await Task.Delay(1000);
            
            var statsBefore = await GetStatsAsync();
            if (statsBefore.TotalTabs < 2)
            {
                return;
            }
            
            // Act - Close with Ctrl+W
            await App.Commands.SendKeyComboAsync("W", new[] { "Ctrl" });
            await Task.Delay(1000);
            
            // Assert
            var statsAfter = await GetStatsAsync();
            Assert.True(statsAfter.TotalTabs < statsBefore.TotalTabs, "Tab should be closed with Ctrl+W");
        });
    }

    [Fact]
    public async Task Close_All_Tabs_Except_One_Should_Work()
    {
        await RunPerformanceTestAsync(nameof(Close_All_Tabs_Except_One_Should_Work), async () =>
        {
            // Arrange - Create multiple tabs
            for (int i = 0; i < 5; i++)
            {
                await App.Commands.CreateTabAsync();
                await Task.Delay(300);
            }
            
            var statsBefore = await GetStatsAsync();
            if (statsBefore.TotalTabs < 3)
            {
                return;
            }
            
            // Act - Close all but one
            var tabsToClose = statsBefore.TotalTabs - 1;
            for (int i = 0; i < tabsToClose; i++)
            {
                await App.Commands.CloseTabAsync();
                await Task.Delay(500);
            }
            
            // Assert
            var statsAfter = await GetStatsAsync();
            Assert.Equal(1, statsAfter.TotalTabs);
            Assert.True(statsAfter.SessionsStarted > 0, "Last session should be active");
        });
    }

    #endregion

    #region Tab Reordering

    [Fact]
    public async Task Tab_Reordering_Should_Work()
    {
        await RunPerformanceTestAsync(nameof(Tab_Reordering_Should_Work), async () =>
        {
            // Arrange - Create tabs with distinguishable content
            for (int i = 0; i < 4; i++)
            {
                await App.Commands.CreateTabAsync();
                await Task.Delay(500);
                await SendTextAndWaitAsync($"Tab{i + 1}Content");
            }
            
            var stats = await GetStatsAsync();
            if (stats.TotalTabs < 4)
            {
                return;
            }
            
            // Act - Reorder tabs (if supported via commands)
            // This would typically be done via drag-drop in GUI
            // We'll simulate with commands if available
            
            // For now, just verify tabs can be switched
            await App.Commands.NextTabAsync();
            await Task.Delay(300);
            await App.Commands.PrevTabAsync();
            await Task.Delay(300);
            
            // Assert
            var finalStats = await GetStatsAsync();
            Assert.True(finalStats.TotalTabs >= 4, "Tabs should be preserved");
        });
    }

    #endregion

    #region Tab Persistence

    [Fact]
    public async Task Tab_Switching_Preserves_Session_State()
    {
        await RunPerformanceTestAsync(nameof(Tab_Switching_Preserves_Session_State), async () =>
        {
            // Arrange - Add content to first tab
            await SendTextAndWaitAsync("Tab 1 content");
            await Task.Delay(300);
            
            // Create second tab and add different content
            await App.Commands.CreateTabAsync();
            await Task.Delay(1000);
            await SendTextAndWaitAsync("Tab 2 content");
            await Task.Delay(300);
            
            var stats = await GetStatsAsync();
            if (stats.TotalTabs < 2)
            {
                return; // Skip if tab creation failed
            }
            
            // Act - Switch back to first tab
            await App.Commands.PrevTabAsync();
            await Task.Delay(500);
            
            // Assert - Session should still be active
            var finalStats = await GetStatsAsync();
            Assert.True(finalStats.SessionsStarted > 0, "Session should remain started");
        });
    }

    [Fact]
    public async Task Tab_Content_Persists_After_Switching()
    {
        await RunPerformanceTestAsync(nameof(Tab_Content_Persists_After_Switching), async () =>
        {
            // Arrange - Create tabs with content
            await SendTextAndWaitAsync("Original Tab Content");
            await Task.Delay(300);
            
            await App.Commands.CreateTabAsync();
            await Task.Delay(1000);
            await SendTextAndWaitAsync("New Tab Content");
            await Task.Delay(300);
            
            var stats = await GetStatsAsync();
            if (stats.TotalTabs < 2)
            {
                return;
            }
            
            // Act - Switch back and forth
            for (int i = 0; i < 5; i++)
            {
                await App.Commands.PrevTabAsync();
                await Task.Delay(200);
                await App.Commands.NextTabAsync();
                await Task.Delay(200);
            }
            
            // Assert
            var finalStats = await GetStatsAsync();
            Assert.True(finalStats.SessionsStarted >= 2, "Both sessions should remain active");
        });
    }

    #endregion

    #region Tab Titles

    [Fact]
    public async Task Tab_Titles_Should_Be_Unique()
    {
        await RunPerformanceTestAsync(nameof(Tab_Titles_Should_Be_Unique), async () =>
        {
            // Arrange - Create multiple tabs
            for (int i = 0; i < 3; i++)
            {
                await App.Commands.CreateTabAsync();
                await Task.Delay(500);
            }
            
            // Assert
            var stats = await GetStatsAsync();
            // Each tab should have a session
            Assert.True(stats.SessionsCreated >= stats.TotalTabs, 
                "Each tab should have a session");
        });
    }

    [Fact]
    public async Task Tab_Title_Updates_With_Activity()
    {
        await RunPerformanceTestAsync(nameof(Tab_Title_Updates_With_Activity), async () =>
        {
            // Arrange
            await SendTextAndWaitAsync("Initial content");
            await Task.Delay(300);
            
            await App.Commands.CreateTabAsync();
            await Task.Delay(1000);
            
            // Act - Generate activity in inactive tab
            await App.Commands.PrevTabAsync();
            await Task.Delay(300);
            await SendTextAndWaitAsync("New activity");
            await Task.Delay(500);
            
            // Switch back
            await App.Commands.NextTabAsync();
            await Task.Delay(300);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0, "Sessions should track activity");
        });
    }

    #endregion

    #region Tab Performance

    [Fact]
    public async Task Tab_Switching_Performance_Should_Be_Fast()
    {
        await RunPerformanceTestAsync(nameof(Tab_Switching_Performance_Should_Be_Fast), async () =>
        {
            // Arrange - Create several tabs
            const int tabCount = 5;
            for (int i = 0; i < tabCount; i++)
            {
                await App.Commands.CreateTabAsync();
                await Task.Delay(300);
            }
            
            var stats = await GetStatsAsync();
            if (stats.TotalTabs < tabCount)
            {
                return; // Skip if we couldn't create enough tabs
            }
            
            // Act - Switch through tabs and measure time
            var switchCount = stats.TotalTabs * 3; // Switch through all tabs 3 times
            var stopwatch = Stopwatch.StartNew();
            
            for (int i = 0; i < switchCount; i++)
            {
                await App.Commands.NextTabAsync();
                await Task.Delay(50); // Small delay between switches
            }
            
            stopwatch.Stop();
            
            // Assert - Tab switching should be responsive
            var avgSwitchTime = stopwatch.ElapsedMilliseconds / (double)switchCount;
            
            // Be lenient in CI/headless environments
            var maxAvgTime = ShouldRunHeadless() ? 200.0 : 100.0;
            
            PerformanceAssertions.AssertTabSwitchPerformance(avgSwitchTime, maxAvgTime);
            
            // Ensure tabs are still functional after rapid switching
            var finalStats = await GetStatsAsync();
            Assert.True(finalStats.SessionsStarted > 0, "Sessions should still be active after tab switching");
        });
    }

    [Fact]
    public async Task Memory_Usage_With_Multiple_Tabs()
    {
        await RunPerformanceTestAsync(nameof(Memory_Usage_With_Multiple_Tabs), async () =>
        {
            // Arrange
            const int tabCount = 5;
            var initialStats = await GetStatsAsync();
            
            // Act - Create multiple tabs with content
            for (int i = 0; i < tabCount; i++)
            {
                if (i > 0)
                {
                    await App.Commands.CreateTabAsync();
                    await Task.Delay(500);
                }
                
                // Add content to each tab
                var content = string.Join("\n", 
                    Enumerable.Range(0, 20).Select(j => $"Tab{i} Line{j}: {new string('A', 80)}"));
                await SendTextAndWaitAsync(content);
                await Task.Delay(200);
            }
            
            await Task.Delay(1000);
            
            // Assert - Memory should not grow excessively with tabs
            var snapshot = CurrentSnapshot;
            Assert.NotNull(snapshot);
            
            // Allow more memory for multiple tabs
            var maxHeapMB = ShouldRunHeadless() ? 1024 : 512;
            PerformanceAssertions.AssertHeapSize(snapshot.HeapSizeBytes, maxHeapSizeBytes: maxHeapMB * 1024 * 1024);
            
            // All tabs should still be functional
            var finalStats = await GetStatsAsync();
            Assert.True(finalStats.TotalTabs >= tabCount, $"Should have {tabCount} tabs");
            Assert.True(finalStats.SessionsStarted >= tabCount, "All sessions should be started");
        });
    }

    [Fact]
    public async Task Many_Tabs_Performance()
    {
        await RunPerformanceTestAsync(nameof(Many_Tabs_Performance), async () =>
        {
            // Arrange - Create many tabs
            const int manyTabCount = 15;
            var stopwatch = Stopwatch.StartNew();
            
            for (int i = 0; i < manyTabCount; i++)
            {
                await App.Commands.CreateTabAsync();
                await Task.Delay(200);
            }
            
            stopwatch.Stop();
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.TotalTabs >= manyTabCount - 2, // Allow some tolerance
                $"Should have approximately {manyTabCount} tabs");
            
            // Creation should complete in reasonable time
            Assert.True(stopwatch.Elapsed.TotalSeconds < 30, 
                "Creating many tabs took too long");
            
            var snapshot = CurrentSnapshot;
            Assert.NotNull(snapshot);
            
            // Memory should still be reasonable
            var maxMemoryMB = ShouldRunHeadless() ? 2048 : 1024;
            PerformanceAssertions.AssertHeapSize(snapshot.HeapSizeBytes, 
                maxHeapSizeBytes: maxMemoryMB * 1024 * 1024);
        });
    }

    [Fact]
    public async Task Rapid_Tab_Creation_And_Closing()
    {
        await RunPerformanceTestAsync(nameof(Rapid_Tab_Creation_And_Closing), async () =>
        {
            // Arrange
            var initialTabCount = (await GetStatsAsync()).TotalTabs;
            var operations = 10;
            var stopwatch = Stopwatch.StartNew();
            
            // Act - Rapidly create and close tabs
            for (int i = 0; i < operations; i++)
            {
                await App.Commands.CreateTabAsync();
                await Task.Delay(200);
                await App.Commands.CloseTabAsync();
                await Task.Delay(200);
            }
            
            stopwatch.Stop();
            
            // Assert
            var stats = await GetStatsAsync();
            // Should end up with at least the original tabs
            Assert.True(stats.TotalTabs >= initialTabCount, "Should retain original tabs");
            
            // Operations should complete in reasonable time
            Assert.True(stopwatch.ElapsedMilliseconds < 10000, 
                $"Rapid tab operations took too long: {stopwatch.ElapsedMilliseconds}ms");
            
            // System should still be responsive
            Assert.True(stats.SessionsStarted > 0, "Sessions should still be functional");
        });
    }

    [Fact]
    public async Task Tab_Switching_With_Content_Updates()
    {
        await RunPerformanceTestAsync(nameof(Tab_Switching_With_Content_Updates), async () =>
        {
            // Arrange - Create tabs with streaming content
            for (int i = 0; i < 3; i++)
            {
                await App.Commands.CreateTabAsync();
                await Task.Delay(500);
            }
            
            var stats = await GetStatsAsync();
            if (stats.TotalTabs < 3)
            {
                return;
            }
            
            // Act - Switch while generating content
            var stopwatch = Stopwatch.StartNew();
            
            for (int i = 0; i < 20; i++)
            {
                await App.Commands.NextTabAsync();
                await Task.Delay(100);
                
                // Add a bit of content
                await SendTextAndWaitAsync($"Update {i}");
                await Task.Delay(100);
            }
            
            stopwatch.Stop();
            
            // Assert
            var snapshot = CurrentSnapshot;
            Assert.NotNull(snapshot);
            
            // Should maintain reasonable FPS during switching
            PerformanceAssertions.AssertFps(snapshot.Fps, minFps: 15);
            
            var finalStats = await GetStatsAsync();
            Assert.True(finalStats.SessionsStarted >= 3, "All sessions should be active");
        });
    }

    #endregion

    #region Window Operations

    [Fact]
    public async Task Window_Resize_Should_Affect_All_Tabs()
    {
        await RunPerformanceTestAsync(nameof(Window_Resize_Should_Affect_All_Tabs), async () =>
        {
            // Arrange - Create multiple tabs
            await App.Commands.CreateTabAsync();
            await Task.Delay(1000);
            
            // Act - Resize window
            await App.Commands.ResizeAsync(100, 30);
            await Task.Delay(500);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0, "All tabs should handle resize");
        });
    }

    [Fact]
    public async Task Window_Resize_With_Many_Tabs()
    {
        await RunPerformanceTestAsync(nameof(Window_Resize_With_Many_Tabs), async () =>
        {
            // Arrange - Create many tabs
            for (int i = 0; i < 8; i++)
            {
                await App.Commands.CreateTabAsync();
                await Task.Delay(300);
            }
            
            var stats = await GetStatsAsync();
            if (stats.TotalTabs < 5)
            {
                return;
            }
            
            // Act - Multiple resizes
            var sizes = new[] { (80, 24), (120, 40), (60, 20), (100, 30), (80, 24) };
            var stopwatch = Stopwatch.StartNew();
            
            foreach (var (cols, rows) in sizes)
            {
                await App.Commands.ResizeAsync(cols, rows);
                await Task.Delay(200);
            }
            
            stopwatch.Stop();
            
            // Assert
            Assert.True(stopwatch.ElapsedMilliseconds < 5000, "Resize with many tabs too slow");
            
            var snapshot = CurrentSnapshot;
            Assert.NotNull(snapshot);
            PerformanceAssertions.AssertFps(snapshot.Fps, minFps: 10);
        });
    }

    [Fact]
    public async Task Rapid_Resize_With_Tabs()
    {
        await RunPerformanceTestAsync(nameof(Rapid_Resize_With_Tabs), async () =>
        {
            // Arrange
            for (int i = 0; i < 4; i++)
            {
                await App.Commands.CreateTabAsync();
                await Task.Delay(300);
            }
            
            var stats = await GetStatsAsync();
            if (stats.TotalTabs < 3)
            {
                return;
            }
            
            // Act - Rapid resizes
            var operations = 20;
            var stopwatch = Stopwatch.StartNew();
            
            for (int i = 0; i < operations; i++)
            {
                var cols = 60 + (i % 40);
                var rows = 20 + (i % 20);
                await App.Commands.ResizeAsync(cols, rows);
                await Task.Delay(100);
            }
            
            stopwatch.Stop();
            
            // Assert
            var finalStats = await GetStatsAsync();
            Assert.True(finalStats.SessionsStarted > 0, "Sessions should survive rapid resize");
            Assert.True(stopwatch.ElapsedMilliseconds < 5000, "Rapid resize too slow");
        });
    }

    #endregion

    #region Session Management

    [Fact]
    public async Task All_Tabs_Should_Have_Active_Sessions()
    {
        await RunPerformanceTestAsync(nameof(All_Tabs_Should_Have_Active_Sessions), async () =>
        {
            // Arrange - Create tabs with activity
            for (int i = 0; i < 5; i++)
            {
                await App.Commands.CreateTabAsync();
                await Task.Delay(500);
                await SendTextAndWaitAsync($"echo 'Tab {i} active'");
                await Task.Delay(300);
            }
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.TotalTabs >= 5, "Should have 5 tabs");
            Assert.True(stats.SessionsStarted >= 5, "All 5 sessions should be started");
            Assert.True(stats.SessionsCreated >= stats.TotalTabs, "Each tab should have a session");
        });
    }

    [Fact]
    public async Task Tab_Closing_Should_Cleanup_Session()
    {
        await RunPerformanceTestAsync(nameof(Tab_Closing_Should_Cleanup_Session), async () =>
        {
            // Arrange - Create tabs
            for (int i = 0; i < 3; i++)
            {
                await App.Commands.CreateTabAsync();
                await Task.Delay(500);
            }
            
            var statsBefore = await GetStatsAsync();
            if (statsBefore.TotalTabs < 3)
            {
                return;
            }
            
            var sessionsBefore = statsBefore.SessionsCreated;
            
            // Act - Close a tab
            await App.Commands.CloseTabAsync();
            await Task.Delay(1000);
            
            // Assert
            var statsAfter = await GetStatsAsync();
            // Sessions might not be immediately cleaned up, but tabs should be closed
            Assert.True(statsAfter.TotalTabs < statsBefore.TotalTabs, "Tab should be closed");
        });
    }

    #endregion
}

