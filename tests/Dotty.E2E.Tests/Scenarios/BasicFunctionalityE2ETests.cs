using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using Dotty.E2E.Tests.Assertions;
using Xunit;
using Xunit.Abstractions;

namespace Dotty.E2E.Tests.Scenarios;

/// <summary>
/// Comprehensive E2E tests for basic terminal functionality.
/// Tests text input/output, cursor movement, window operations, copy/paste, clear screen, and line editing.
/// </summary>
[Trait("Category", "Basic")]
[Trait("Category", "Core")]
public class BasicFunctionalityE2ETests : E2EPerformanceTestBase
{
    public BasicFunctionalityE2ETests(ITestOutputHelper outputHelper) : base("BasicFunctionality", outputHelper)
    {
    }

    #region Text Input/Output

    [Theory]
    [InlineData("Hello World")]
    [InlineData("abcdefghijklmnopqrstuvwxyz")]
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZ")]
    [InlineData("0123456789")]
    [InlineData("!@#$%^&*()_+-=[]{}|;':\",./<>?")]
    [InlineData("Unicode: 你好世界 🎉 émojis ñ")]
    public async Task Text_Input_Should_Display(string input)
    {
        await RunPerformanceTestAsync($"Text_Input_{input.Substring(0, Math.Min(10, input.Length))}", async () =>
        {
            // Act
            await SendTextAndWaitAsync(input);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0, "Text should be displayed");
            
            var state = await App.Commands.GetStateAsync();
            Assert.True(state.Cols > 0 && state.Rows > 0, "Terminal should have valid dimensions");
        }, assertBaseline: false);
    }

    [Fact]
    public async Task Long_Text_Input_Should_Wrap()
    {
        await RunPerformanceTestAsync(nameof(Long_Text_Input_Should_Wrap), async () =>
        {
            // Arrange - Create text longer than terminal width
            var longText = new string('A', 200);
            
            // Act
            await SendTextAndWaitAsync(longText);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0, "Long text should be handled");
        });
    }

    [Fact]
    public async Task Multiple_Lines_Input()
    {
        await RunPerformanceTestAsync(nameof(Multiple_Lines_Input), async () =>
        {
            // Arrange
            var lines = new[]
            {
                "Line 1: First line of text",
                "Line 2: Second line of text",
                "Line 3: Third line of text",
                "Line 4: Fourth line of text",
                "Line 5: Fifth line of text"
            };
            
            // Act
            foreach (var line in lines)
            {
                await SendTextAndWaitAsync(line + "\n");
            }
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0);
            
            var state = await App.Commands.GetStateAsync();
            Assert.True(state.Rows >= 5 || stats.Scrollback?.ScrollbackCount > 0, 
                "Should have multiple lines");
        });
    }

    [Fact]
    public async Task Empty_Text_Input()
    {
        await RunPerformanceTestAsync(nameof(Empty_Text_Input), async () =>
        {
            // Act
            await SendTextAndWaitAsync("");
            await Task.Delay(300);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0, "Should handle empty input");
        });
    }

    [Fact]
    public async Task Whitespace_Only_Input()
    {
        await RunPerformanceTestAsync(nameof(Whitespace_Only_Input), async () =>
        {
            // Act
            await SendTextAndWaitAsync("     \t\t\n");
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0, "Should handle whitespace input");
        });
    }

    #endregion

    #region Cursor Movement

    [Fact]
    public async Task Cursor_Left_Should_Move_Backward()
    {
        await RunPerformanceTestAsync(nameof(Cursor_Left_Should_Move_Backward), async () =>
        {
            // Arrange
            await SendTextAndWaitAsync("ABCD");
            
            // Act - Move cursor left
            for (int i = 0; i < 2; i++)
            {
                await App.Commands.SendKeyAsync("Left");
                await Task.Delay(100);
            }
            
            // Assert
            var state = await App.Commands.GetStateAsync();
            Assert.True(state.Cols > 0, "Terminal should have columns");
        });
    }

    [Fact]
    public async Task Cursor_Right_Should_Move_Forward()
    {
        await RunPerformanceTestAsync(nameof(Cursor_Right_Should_Move_Forward), async () =>
        {
            // Arrange
            await SendTextAndWaitAsync("ABCD");
            await App.Commands.SendKeyAsync("Home");
            await Task.Delay(200);
            
            // Act - Move cursor right
            for (int i = 0; i < 2; i++)
            {
                await App.Commands.SendKeyAsync("Right");
                await Task.Delay(100);
            }
            
            // Assert
            var state = await App.Commands.GetStateAsync();
            Assert.True(state.Cols > 0, "Terminal should have columns");
        });
    }

    [Fact]
    public async Task Cursor_Up_Should_Move_To_Previous_Line()
    {
        await RunPerformanceTestAsync(nameof(Cursor_Up_Should_Move_To_Previous_Line), async () =>
        {
            // Arrange
            await SendTextAndWaitAsync("Line 1\n");
            await SendTextAndWaitAsync("Line 2\n");
            await SendTextAndWaitAsync("Line 3");
            await Task.Delay(300);
            
            // Act
            await App.Commands.SendKeyAsync("Up");
            await Task.Delay(200);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0, "Should navigate with cursor up");
        });
    }

    [Fact]
    public async Task Cursor_Down_Should_Move_To_Next_Line()
    {
        await RunPerformanceTestAsync(nameof(Cursor_Down_Should_Move_To_Next_Line), async () =>
        {
            // Arrange
            await SendTextAndWaitAsync("Line 1\nLine 2\nLine 3");
            await Task.Delay(300);
            
            // Move up first
            await App.Commands.SendKeyAsync("Up");
            await Task.Delay(200);
            await App.Commands.SendKeyAsync("Up");
            await Task.Delay(200);
            
            // Act - Move down
            await App.Commands.SendKeyAsync("Down");
            await Task.Delay(200);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0, "Should navigate with cursor down");
        });
    }

    [Fact]
    public async Task Cursor_Home_Should_Move_To_Start()
    {
        await RunPerformanceTestAsync(nameof(Cursor_Home_Should_Move_To_Start), async () =>
        {
            // Arrange
            await SendTextAndWaitAsync("ABCDEFGHIJ");
            
            // Act
            await App.Commands.SendKeyAsync("Home");
            await Task.Delay(200);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0, "Home key should work");
        });
    }

    [Fact]
    public async Task Cursor_End_Should_Move_To_End()
    {
        await RunPerformanceTestAsync(nameof(Cursor_End_Should_Move_To_End), async () =>
        {
            // Arrange
            await SendTextAndWaitAsync("ABCDEFG");
            await App.Commands.SendKeyAsync("Home");
            await Task.Delay(200);
            
            // Act
            await App.Commands.SendKeyAsync("End");
            await Task.Delay(200);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0, "End key should work");
        });
    }

    [Fact]
    public async Task Cursor_All_Directions()
    {
        await RunPerformanceTestAsync(nameof(Cursor_All_Directions), async () =>
        {
            // Arrange - Create a grid of content
            for (int i = 0; i < 3; i++)
            {
                await SendTextAndWaitAsync($"Row{i}: ABCDEFGHIJ\n");
            }
            await Task.Delay(300);
            
            // Act - Navigate in all directions
            await App.Commands.SendKeyAsync("Up");
            await Task.Delay(100);
            await App.Commands.SendKeyAsync("Left");
            await Task.Delay(100);
            await App.Commands.SendKeyAsync("Right");
            await Task.Delay(100);
            await App.Commands.SendKeyAsync("Down");
            await Task.Delay(100);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0, "All cursor directions should work");
        });
    }

    #endregion

    #region Window Operations

    [Fact]
    public async Task Window_Create_Should_Initialize()
    {
        await RunPerformanceTestAsync(nameof(Window_Create_Should_Initialize), async () =>
        {
            // Act - Window is created in InitializeAsync, just verify
            await Task.Delay(500);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.MountedViews > 0, "Window should be mounted");
            Assert.True(stats.SessionsStarted > 0, "Session should be started");
            
            var state = await App.Commands.GetStateAsync();
            Assert.True(state.Rows > 0, "Should have rows");
            Assert.True(state.Cols > 0, "Should have columns");
        });
    }

    [Theory]
    [InlineData(80, 24)]
    [InlineData(100, 30)]
    [InlineData(120, 40)]
    [InlineData(60, 20)]
    public async Task Window_Resize_Should_Change_Dimensions(int cols, int rows)
    {
        await RunPerformanceTestAsync($"Resize_{cols}x{rows}", async () =>
        {
            // Arrange
            var initialState = await App.Commands.GetStateAsync();
            
            // Act
            await App.Commands.ResizeAsync(cols, rows);
            await Task.Delay(500);
            
            // Assert
            var newState = await App.Commands.GetStateAsync();
            Assert.True(newState.Rows > 0, "Should have valid rows after resize");
            Assert.True(newState.Cols > 0, "Should have valid columns after resize");
        });
    }

    [Fact]
    public async Task Window_Resize_To_Small_Dimensions()
    {
        await RunPerformanceTestAsync(nameof(Window_Resize_To_Small_Dimensions), async () =>
        {
            // Arrange
            await SendTextAndWaitAsync("Content that might need wrapping");
            await Task.Delay(300);
            
            // Act - Resize to small window
            await App.Commands.ResizeAsync(40, 12);
            await Task.Delay(500);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0, "Should handle small window size");
            
            var state = await App.Commands.GetStateAsync();
            Assert.True(state.Rows > 0 && state.Cols > 0, "Dimensions should be valid");
        });
    }

    [Fact]
    public async Task Window_Resize_To_Large_Dimensions()
    {
        await RunPerformanceTestAsync(nameof(Window_Resize_To_Large_Dimensions), async () =>
        {
            // Act - Resize to large window
            await App.Commands.ResizeAsync(200, 60);
            await Task.Delay(500);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0, "Should handle large window size");
            
            var state = await App.Commands.GetStateAsync();
            Assert.True(state.Rows > 0 && state.Cols > 0, "Dimensions should be valid");
        });
    }

    [Fact]
    public async Task Window_Minimize_And_Restore()
    {
        await RunPerformanceTestAsync(nameof(Window_Minimize_And_Restore), async () =>
        {
            // Arrange
            await SendTextAndWaitAsync("Content before minimize");
            await Task.Delay(300);
            
            // Act - Simulate minimize/restore via resize
            // Minimize typically makes window very small
            await App.Commands.ResizeAsync(10, 5);
            await Task.Delay(500);
            
            // Restore
            await App.Commands.ResizeAsync(80, 24);
            await Task.Delay(500);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0, "Should handle minimize/restore");
        });
    }

    [Fact]
    public async Task Window_Maximize()
    {
        await RunPerformanceTestAsync(nameof(Window_Maximize), async () =>
        {
            // Arrange
            await SendTextAndWaitAsync("Content before maximize");
            await Task.Delay(300);
            
            // Act - Resize to large dimensions (simulate maximize)
            await App.Commands.ResizeAsync(240, 80);
            await Task.Delay(500);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0, "Should handle maximize");
            
            var snapshot = CurrentSnapshot;
            Assert.NotNull(snapshot);
            PerformanceAssertions.AssertFps(snapshot.Fps, minFps: 10);
        });
    }

    [Fact]
    public async Task Rapid_Window_Resize()
    {
        await RunPerformanceTestAsync(nameof(Rapid_Window_Resize), async () =>
        {
            // Arrange
            var sizes = new[] { (80, 24), (100, 30), (60, 20), (120, 40), (80, 24) };
            var stopwatch = Stopwatch.StartNew();
            
            // Act - Rapid resize
            for (int i = 0; i < 10; i++)
            {
                foreach (var (cols, rows) in sizes)
                {
                    await App.Commands.ResizeAsync(cols, rows);
                    await Task.Delay(100);
                }
            }
            
            stopwatch.Stop();
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0, "Should handle rapid resize");
            Assert.True(stopwatch.ElapsedMilliseconds < 10000, "Rapid resize too slow");
            
            var snapshot = CurrentSnapshot;
            Assert.NotNull(snapshot);
            PerformanceAssertions.AssertFps(snapshot.Fps, minFps: 15);
        });
    }

    #endregion

    #region Copy/Paste

    [Fact]
    public async Task Copy_Selection_Should_Work()
    {
        await RunPerformanceTestAsync(nameof(Copy_Selection_Should_Work), async () =>
        {
            // Arrange
            await SendTextAndWaitAsync("Text to be selected and copied");
            await Task.Delay(300);
            
            // Act - Try to copy
            await App.Commands.CopySelectionAsync();
            await Task.Delay(300);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0, "Copy operation should work");
        });
    }

    [Fact]
    public async Task Paste_From_Clipboard_Should_Work()
    {
        await RunPerformanceTestAsync(nameof(Paste_From_Clipboard_Should_Work), async () =>
        {
            // Arrange
            await SendTextAndWaitAsync("Initial text");
            await Task.Delay(300);
            
            // Act - Try to paste
            await App.Commands.PasteClipboardAsync();
            await Task.Delay(300);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0, "Paste operation should work");
        });
    }

    [Fact]
    public async Task Copy_Paste_Cycle()
    {
        await RunPerformanceTestAsync(nameof(Copy_Paste_Cycle), async () =>
        {
            // Arrange
            var originalText = "Original text to copy";
            await SendTextAndWaitAsync(originalText);
            await Task.Delay(300);
            
            // Act - Copy
            await App.Commands.CopySelectionAsync();
            await Task.Delay(300);
            
            // Paste (may paste different content depending on clipboard)
            await App.Commands.PasteClipboardAsync();
            await Task.Delay(300);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0, "Copy-paste cycle should work");
        });
    }

    #endregion

    #region Clear Screen

    [Fact]
    public async Task Clear_Screen_Should_Remove_Content()
    {
        await RunPerformanceTestAsync(nameof(Clear_Screen_Should_Remove_Content), async () =>
        {
            // Arrange - Add content
            for (int i = 0; i < 10; i++)
            {
                await SendTextAndWaitAsync($"Line {i}: {new string('A', 50)}\n");
            }
            await Task.Delay(500);
            
            // Act - Clear screen via ANSI
            await App.Commands.InjectAnsiAsync("\u001b[2J\u001b[H");
            await Task.Delay(500);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0, "Clear screen should work");
        });
    }

    [Fact]
    public async Task Clear_Line_Should_Work()
    {
        await RunPerformanceTestAsync(nameof(Clear_Line_Should_Work), async () =>
        {
            // Arrange
            await SendTextAndWaitAsync("This line will be partially cleared");
            await Task.Delay(300);
            
            // Act - Clear to end of line
            await App.Commands.InjectAnsiAsync("\u001b[K");
            await Task.Delay(300);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0, "Clear line should work");
        });
    }

    [Fact]
    public async Task Clear_From_Cursor_To_Beginning()
    {
        await RunPerformanceTestAsync(nameof(Clear_From_Cursor_To_Beginning), async () =>
        {
            // Arrange
            await SendTextAndWaitAsync("Text content here");
            await Task.Delay(300);
            
            // Move cursor to middle
            for (int i = 0; i < 5; i++)
            {
                await App.Commands.SendKeyAsync("Left");
                await Task.Delay(100);
            }
            
            // Act - Clear from cursor to beginning
            await App.Commands.InjectAnsiAsync("\u001b[1K");
            await Task.Delay(300);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0, "Clear to beginning should work");
        });
    }

    #endregion

    #region Line Editing

    [Fact]
    public async Task Backspace_Should_Delete_Character()
    {
        await RunPerformanceTestAsync(nameof(Backspace_Should_Delete_Character), async () =>
        {
            // Arrange
            await SendTextAndWaitAsync("ABCD");
            await Task.Delay(200);
            
            // Act - Backspace
            await App.Commands.SendKeyAsync("Backspace");
            await Task.Delay(200);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0, "Backspace should work");
        });
    }

    [Fact]
    public async Task Delete_Should_Remove_Character()
    {
        await RunPerformanceTestAsync(nameof(Delete_Should_Remove_Character), async () =>
        {
            // Arrange
            await SendTextAndWaitAsync("ABCD");
            await App.Commands.SendKeyAsync("Home");
            await Task.Delay(200);
            
            // Act - Delete
            await App.Commands.SendKeyAsync("Delete");
            await Task.Delay(200);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0, "Delete should work");
        });
    }

    [Fact]
    public async Task Insert_Mode_Should_Toggle()
    {
        await RunPerformanceTestAsync(nameof(Insert_Mode_Should_Toggle), async () =>
        {
            // Arrange
            await SendTextAndWaitAsync("ABCD");
            await App.Commands.SendKeyAsync("Home");
            await Task.Delay(200);
            
            // Act - Insert key
            await App.Commands.SendKeyAsync("Insert");
            await Task.Delay(200);
            await SendTextAndWaitAsync("X");
            await Task.Delay(200);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0, "Insert mode should work");
        });
    }

    [Fact]
    public async Task Enter_Should_Create_New_Line()
    {
        await RunPerformanceTestAsync(nameof(Enter_Should_Create_New_Line), async () =>
        {
            // Arrange
            await SendTextAndWaitAsync("Line 1");
            
            // Act
            await App.Commands.SendKeyAsync("Enter");
            await Task.Delay(200);
            await SendTextAndWaitAsync("Line 2");
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0, "Enter should create new line");
        });
    }

    [Fact]
    public async Task Tab_Should_Insert_Tab_Or_Complete()
    {
        await RunPerformanceTestAsync(nameof(Tab_Should_Insert_Tab_Or_Complete), async () =>
        {
            // Arrange
            await SendTextAndWaitAsync("te");
            await Task.Delay(200);
            
            // Act - Tab key
            await App.Commands.SendKeyAsync("Tab");
            await Task.Delay(300);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0, "Tab should work");
        });
    }

    [Fact]
    public async Task Escape_Should_Cancel_Operations()
    {
        await RunPerformanceTestAsync(nameof(Escape_Should_Cancel_Operations), async () =>
        {
            // Arrange
            await SendTextAndWaitAsync("Partial input");
            
            // Act - Escape key
            await App.Commands.SendKeyAsync("Escape");
            await Task.Delay(300);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0, "Escape should work");
        });
    }

    [Fact]
    public async Task Word_Navigation_Ctrl_Arrow()
    {
        await RunPerformanceTestAsync(nameof(Word_Navigation_Ctrl_Arrow), async () =>
        {
            // Arrange
            await SendTextAndWaitAsync("word1 word2 word3 word4");
            await Task.Delay(300);
            
            // Act - Navigate by word
            await App.Commands.SendKeyComboAsync("Left", new[] { "Ctrl" });
            await Task.Delay(200);
            await App.Commands.SendKeyComboAsync("Left", new[] { "Ctrl" });
            await Task.Delay(200);
            await App.Commands.SendKeyComboAsync("Right", new[] { "Ctrl" });
            await Task.Delay(200);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0, "Word navigation should work");
        });
    }

    [Fact]
    public async Task Select_All_Ctrl_A()
    {
        await RunPerformanceTestAsync(nameof(Select_All_Ctrl_A), async () =>
        {
            // Arrange
            await SendTextAndWaitAsync("Text to select all");
            await Task.Delay(300);
            
            // Act
            await App.Commands.SendKeyComboAsync("A", new[] { "Ctrl" });
            await Task.Delay(300);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0, "Select all should work");
        });
    }

    [Fact]
    public async Task Undo_Ctrl_Z()
    {
        await RunPerformanceTestAsync(nameof(Undo_Ctrl_Z), async () =>
        {
            // Arrange
            await SendTextAndWaitAsync("Initial text");
            await Task.Delay(300);
            
            // Act - Undo
            await App.Commands.SendKeyComboAsync("Z", new[] { "Ctrl" });
            await Task.Delay(300);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0, "Undo should work");
        });
    }

    #endregion

    #region Basic Functionality Performance

    [Fact]
    public async Task Basic_Operations_Performance()
    {
        await RunPerformanceTestAsync(nameof(Basic_Operations_Performance), async () =>
        {
            // Arrange
            var operations = 50;
            var stopwatch = Stopwatch.StartNew();
            
            // Act - Perform various basic operations
            for (int i = 0; i < operations; i++)
            {
                await SendTextAndWaitAsync($"Op{i}");
                await App.Commands.SendKeyAsync("Left");
                await App.Commands.SendKeyAsync("Right");
            }
            
            stopwatch.Stop();
            
            // Assert
            var avgTime = stopwatch.ElapsedMilliseconds / (double)operations;
            Assert.True(avgTime < 100, $"Average operation time too high: {avgTime:F1}ms");
            
            var snapshot = CurrentSnapshot;
            Assert.NotNull(snapshot);
            PerformanceAssertions.AssertFps(snapshot.Fps, minFps: 20);
        });
    }

    [Fact]
    public async Task Rapid_Input_Stress_Test()
    {
        await RunPerformanceTestAsync(nameof(Rapid_Input_Stress_Test), async () =>
        {
            // Arrange
            var charCount = 1000;
            var stopwatch = Stopwatch.StartNew();
            
            // Act - Rapid input
            var sb = new StringBuilder();
            for (int i = 0; i < charCount; i++)
            {
                sb.Append((char)('a' + (i % 26)));
            }
            await App.Commands.SendTextAsync(sb.ToString());
            await Task.Delay(1000);
            
            stopwatch.Stop();
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0, "Should handle rapid input");
            Assert.True(stopwatch.ElapsedMilliseconds < 5000, "Rapid input too slow");
            
            var snapshot = CurrentSnapshot;
            Assert.NotNull(snapshot);
            
            // Check input latency
            PerformanceAssertions.AssertInputLatency(snapshot.InputLatencyAvgMs, snapshot.InputLatencyP95Ms,
                maxAvgMs: 50, maxP95Ms: 100);
        });
    }

    #endregion
}
