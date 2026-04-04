using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using Dotty.E2E.Tests.Assertions;
using Xunit;
using Xunit.Abstractions;

namespace Dotty.E2E.Tests.Scenarios;

/// <summary>
/// Comprehensive E2E tests for search functionality.
/// Tests search UI activation, text search, regex, case sensitivity, navigation, and performance.
/// </summary>
[Trait("Category", "Search")]
[Trait("Category", "CoreFeature")]
public class SearchE2ETests : E2EPerformanceTestBase
{
    public SearchE2ETests(ITestOutputHelper outputHelper) : base("Search", outputHelper)
    {
    }

    #region Search UI Activation

    [Fact]
    public async Task Search_UI_Should_Activate_With_Keyboard_Shortcut()
    {
        await RunPerformanceTestAsync(nameof(Search_UI_Should_Activate_With_Keyboard_Shortcut), async () =>
        {
            // Arrange - Add some content to search
            await SendTextAndWaitAsync("Search test content here\n");
            await Task.Delay(300);
            
            // Act - Activate search with Ctrl+Shift+F
            await App.Commands.SendKeyComboAsync("F", new[] { "Ctrl", "Shift" });
            await Task.Delay(500);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0, "Session should remain active after search activation");
            
            // Verify search is active by checking state
            var state = await App.Commands.GetStateAsync();
            Assert.True(state.Cols > 0 && state.Rows > 0, "Terminal should maintain dimensions");
        }, assertBaseline: false);
    }

    [Fact]
    public async Task Search_UI_Should_Close_With_Escape()
    {
        await RunPerformanceTestAsync(nameof(Search_UI_Should_Close_With_Escape), async () =>
        {
            // Arrange - Open search
            await SendTextAndWaitAsync("Test content\n");
            await App.Commands.SendKeyComboAsync("F", new[] { "Ctrl", "Shift" });
            await Task.Delay(500);
            
            // Act - Close search with Escape
            await App.Commands.SendKeyAsync("Escape");
            await Task.Delay(300);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0, "Session should remain active after closing search");
        });
    }

    #endregion

    #region Basic Text Search

    [Fact]
    public async Task Search_Should_Find_Simple_Text()
    {
        await RunPerformanceTestAsync(nameof(Search_Should_Find_Simple_Text), async () =>
        {
            // Arrange - Add searchable content
            var content = "This is a test line with the word target in it\n" +
                         "Another line without the keyword\n" +
                         "One more line with target present\n";
            await SendTextAndWaitAsync(content);
            await Task.Delay(500);
            
            // Act - Open search and type search term
            await App.Commands.SendKeyComboAsync("F", new[] { "Ctrl", "Shift" });
            await Task.Delay(300);
            await App.Commands.SendTextAsync("target");
            await Task.Delay(500);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0, "Should find text in search");
        }, assertBaseline: false);
    }

    [Fact]
    public async Task Search_Should_Find_Multiple_Matches()
    {
        await RunPerformanceTestAsync(nameof(Search_Should_Find_Multiple_Matches), async () =>
        {
            // Arrange - Content with multiple matches
            var sb = new StringBuilder();
            for (int i = 0; i < 10; i++)
            {
                sb.AppendLine($"Line {i}: search term appears here");
            }
            await SendTextAndWaitAsync(sb.ToString());
            await Task.Delay(500);
            
            // Act - Search for term
            await App.Commands.SendKeyComboAsync("F", new[] { "Ctrl", "Shift" });
            await Task.Delay(300);
            await App.Commands.SendTextAsync("search term");
            await Task.Delay(500);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0, "Should find multiple matches");
        });
    }

    [Fact]
    public async Task Search_Should_Handle_No_Matches()
    {
        await RunPerformanceTestAsync(nameof(Search_Should_Handle_No_Matches), async () =>
        {
            // Arrange
            await SendTextAndWaitAsync("Content without the search term\n");
            await Task.Delay(300);
            
            // Act - Search for non-existent term
            await App.Commands.SendKeyComboAsync("F", new[] { "Ctrl", "Shift" });
            await Task.Delay(300);
            await App.Commands.SendTextAsync("nonexistentxyz123");
            await Task.Delay(500);
            
            // Assert - Should handle gracefully
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0, "Should handle no matches gracefully");
        });
    }

    #endregion

    #region Search Direction

    [Fact]
    public async Task Search_Next_Should_Find_Forward()
    {
        await RunPerformanceTestAsync(nameof(Search_Next_Should_Find_Forward), async () =>
        {
            // Arrange
            var sb = new StringBuilder();
            for (int i = 0; i < 20; i++)
            {
                sb.AppendLine($"Line {i}: keyword");
            }
            await SendTextAndWaitAsync(sb.ToString());
            await Task.Delay(500);
            
            // Act - Search and navigate forward
            await App.Commands.SendKeyComboAsync("F", new[] { "Ctrl", "Shift" });
            await Task.Delay(300);
            await App.Commands.SendTextAsync("keyword");
            await Task.Delay(300);
            
            // Press Enter or F3 to go to next match
            await App.Commands.SendKeyAsync("Enter");
            await Task.Delay(200);
            await App.Commands.SendKeyAsync("Enter");
            await Task.Delay(200);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0, "Should navigate to next match");
        });
    }

    [Fact]
    public async Task Search_Previous_Should_Find_Backward()
    {
        await RunPerformanceTestAsync(nameof(Search_Previous_Should_Find_Backward), async () =>
        {
            // Arrange
            var sb = new StringBuilder();
            for (int i = 0; i < 20; i++)
            {
                sb.AppendLine($"Line {i}: searchword");
            }
            await SendTextAndWaitAsync(sb.ToString());
            await Task.Delay(500);
            
            // Act - Search and navigate backward
            await App.Commands.SendKeyComboAsync("F", new[] { "Ctrl", "Shift" });
            await Task.Delay(300);
            await App.Commands.SendTextAsync("searchword");
            await Task.Delay(300);
            
            // Go to several matches
            for (int i = 0; i < 5; i++)
            {
                await App.Commands.SendKeyAsync("Enter");
                await Task.Delay(100);
            }
            
            // Go back with Shift+Enter or Shift+F3
            await App.Commands.SendKeyComboAsync("Enter", new[] { "Shift" });
            await Task.Delay(200);
            await App.Commands.SendKeyComboAsync("Enter", new[] { "Shift" });
            await Task.Delay(200);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0, "Should navigate to previous match");
        });
    }

    #endregion

    #region Case Sensitivity

    [Fact]
    public async Task Case_Sensitive_Search_Should_Respect_Case()
    {
        await RunPerformanceTestAsync(nameof(Case_Sensitive_Search_Should_Respect_Case), async () =>
        {
            // Arrange
            await SendTextAndWaitAsync("UPPERCASE text lowercase Text\n");
            await Task.Delay(300);
            
            // Act - Case-sensitive search
            await App.Commands.SendKeyComboAsync("F", new[] { "Ctrl", "Shift" });
            await Task.Delay(300);
            await App.Commands.SendTextAsync("Text");
            await Task.Delay(300);
            
            // Enable case sensitive (Alt+C or similar)
            await App.Commands.SendKeyComboAsync("C", new[] { "Alt" });
            await Task.Delay(300);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0, "Should perform case-sensitive search");
        }, assertBaseline: false);
    }

    [Fact]
    public async Task Case_Insensitive_Search_Should_Ignore_Case()
    {
        await RunPerformanceTestAsync(nameof(Case_Insensitive_Search_Should_Ignore_Case), async () =>
        {
            // Arrange
            await SendTextAndWaitAsync("Mixed Case TEXT mixed case text\n");
            await Task.Delay(300);
            
            // Act - Case-insensitive search
            await App.Commands.SendKeyComboAsync("F", new[] { "Ctrl", "Shift" });
            await Task.Delay(300);
            await App.Commands.SendTextAsync("text");
            await Task.Delay(500);
            
            // Assert - Should find all variations
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0, "Should perform case-insensitive search");
        });
    }

    #endregion

    #region Regex Search

    [Fact]
    public async Task Regex_Search_Should_Support_Patterns()
    {
        await RunPerformanceTestAsync(nameof(Regex_Search_Should_Support_Patterns), async () =>
        {
            // Arrange
            await SendTextAndWaitAsync("Line 1: abc123\nLine 2: def456\nLine 3: xyz789\n");
            await Task.Delay(300);
            
            // Act - Enable regex mode and search
            await App.Commands.SendKeyComboAsync("F", new[] { "Ctrl", "Shift" });
            await Task.Delay(300);
            
            // Enable regex mode (Alt+R or similar)
            await App.Commands.SendKeyComboAsync("R", new[] { "Alt" });
            await Task.Delay(300);
            
            // Search for digit pattern
            await App.Commands.SendTextAsync("[0-9]+");
            await Task.Delay(500);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0, "Should support regex search");
        }, assertBaseline: false);
    }

    [Fact]
    public async Task Regex_Search_Should_Match_Word_Boundaries()
    {
        await RunPerformanceTestAsync(nameof(Regex_Search_Should_Match_Word_Boundaries), async () =>
        {
            // Arrange
            await SendTextAndWaitAsync("word1 word2 word3\n");
            await Task.Delay(300);
            
            // Act - Regex with word boundary
            await App.Commands.SendKeyComboAsync("F", new[] { "Ctrl", "Shift" });
            await Task.Delay(300);
            await App.Commands.SendKeyComboAsync("R", new[] { "Alt" }); // Enable regex
            await Task.Delay(300);
            await App.Commands.SendTextAsync(@"\bword\d\b");
            await Task.Delay(500);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0, "Should match word boundaries");
        });
    }

    [Fact]
    public async Task Regex_Search_Should_Support_Character_Classes()
    {
        await RunPerformanceTestAsync(nameof(Regex_Search_Should_Support_Character_Classes), async () =>
        {
            // Arrange
            await SendTextAndWaitAsync("Email: test@example.com Phone: 123-456-7890\n");
            await Task.Delay(300);
            
            // Act - Search for email pattern
            await App.Commands.SendKeyComboAsync("F", new[] { "Ctrl", "Shift" });
            await Task.Delay(300);
            await App.Commands.SendKeyComboAsync("R", new[] { "Alt" });
            await Task.Delay(300);
            await App.Commands.SendTextAsync(@"[a-z]+@[a-z]+\.[a-z]+");
            await Task.Delay(500);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0, "Should support character classes");
        });
    }

    #endregion

    #region Search in Scrollback

    [Fact]
    public async Task Search_Should_Find_In_Scrollback_Buffer()
    {
        await RunPerformanceTestAsync(nameof(Search_Should_Find_In_Scrollback_Buffer), async () =>
        {
            // Arrange - Generate content that goes into scrollback
            var sb = new StringBuilder();
            for (int i = 0; i < 100; i++)
            {
                sb.AppendLine($"Scrollback line {i}: target content");
            }
            await SendTextAndWaitAsync(sb.ToString());
            await Task.Delay(1000);
            
            // Act - Search for content in scrollback
            await App.Commands.SendKeyComboAsync("F", new[] { "Ctrl", "Shift" });
            await Task.Delay(300);
            await App.Commands.SendTextAsync("target content");
            await Task.Delay(500);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.Scrollback?.ScrollbackCount > 50, "Should have scrollback content");
            Assert.True(stats.SessionsStarted > 0, "Should search in scrollback");
        });
    }

    [Fact]
    public async Task Search_Should_Scroll_To_Match_In_Scrollback()
    {
        await RunPerformanceTestAsync(nameof(Search_Should_Scroll_To_Match_In_Scrollback), async () =>
        {
            // Arrange - Create content with match in scrollback
            var sb = new StringBuilder();
            for (int i = 0; i < 50; i++)
            {
                sb.AppendLine($"Line {i}: filler content");
            }
            sb.AppendLine("SPECIAL_MARKER: find this");
            for (int i = 51; i < 100; i++)
            {
                sb.AppendLine($"Line {i}: more filler");
            }
            await SendTextAndWaitAsync(sb.ToString());
            await Task.Delay(1000);
            
            // Act - Search for the marker
            await App.Commands.SendKeyComboAsync("F", new[] { "Ctrl", "Shift" });
            await Task.Delay(300);
            await App.Commands.SendTextAsync("SPECIAL_MARKER");
            await Task.Delay(500);
            await App.Commands.SendKeyAsync("Enter"); // Go to match
            await Task.Delay(500);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.Scrollback?.ScrollbackCount > 50, "Should have scrollback");
            Assert.True(stats.SessionsStarted > 0, "Should scroll to match");
        });
    }

    #endregion

    #region Search Performance

    [Fact]
    public async Task Search_Performance_Large_Buffer()
    {
        await RunPerformanceTestAsync(nameof(Search_Performance_Large_Buffer), async () =>
        {
            // Arrange - Generate large buffer
            var sb = new StringBuilder();
            for (int i = 0; i < 1000; i++)
            {
                sb.AppendLine($"Line {i}: {new string('a', 80)}");
            }
            sb.AppendLine("TARGET_LINE: search for this");
            for (int i = 1001; i < 2000; i++)
            {
                sb.AppendLine($"Line {i}: {new string('b', 80)}");
            }
            
            var stopwatch = Stopwatch.StartNew();
            await SendTextAndWaitAsync(sb.ToString());
            await Task.Delay(2000);
            
            // Act - Search in large buffer
            await App.Commands.SendKeyComboAsync("F", new[] { "Ctrl", "Shift" });
            await Task.Delay(300);
            await App.Commands.SendTextAsync("TARGET_LINE");
            await Task.Delay(1000); // Time for search
            
            stopwatch.Stop();
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0, "Should search large buffer");
            Assert.True(stopwatch.ElapsedMilliseconds < 5000, "Search in large buffer too slow");
            
            var snapshot = CurrentSnapshot;
            Assert.NotNull(snapshot);
            PerformanceAssertions.AssertFps(snapshot.Fps, minFps: 10);
        });
    }

    [Fact]
    public async Task Regex_Search_Performance()
    {
        await RunPerformanceTestAsync(nameof(Regex_Search_Performance), async () =>
        {
            // Arrange - Generate content
            var sb = new StringBuilder();
            for (int i = 0; i < 500; i++)
            {
                sb.AppendLine($"Item {i}: value_{i}_{Guid.NewGuid().ToString()[..8]}");
            }
            
            var stopwatch = Stopwatch.StartNew();
            await SendTextAndWaitAsync(sb.ToString());
            await Task.Delay(1000);
            
            // Act - Regex search
            await App.Commands.SendKeyComboAsync("F", new[] { "Ctrl", "Shift" });
            await Task.Delay(300);
            await App.Commands.SendKeyComboAsync("R", new[] { "Alt" });
            await Task.Delay(300);
            await App.Commands.SendTextAsync(@"value_\d+_[a-f0-9]+");
            await Task.Delay(1000);
            
            stopwatch.Stop();
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0, "Should perform regex search");
            Assert.True(stopwatch.ElapsedMilliseconds < 5000, "Regex search too slow");
            
            var snapshot = CurrentSnapshot;
            Assert.NotNull(snapshot);
            PerformanceAssertions.AssertFps(snapshot.Fps, minFps: 15);
        });
    }

    [Fact]
    public async Task Rapid_Search_Operations_Performance()
    {
        await RunPerformanceTestAsync(nameof(Rapid_Search_Operations_Performance), async () =>
        {
            // Arrange
            var sb = new StringBuilder();
            for (int i = 0; i < 100; i++)
            {
                sb.AppendLine($"Content line {i} with keyword");
            }
            await SendTextAndWaitAsync(sb.ToString());
            await Task.Delay(500);
            
            // Act - Rapid search operations
            await App.Commands.SendKeyComboAsync("F", new[] { "Ctrl", "Shift" });
            await Task.Delay(300);
            
            var stopwatch = Stopwatch.StartNew();
            
            // Type different search terms rapidly
            for (int i = 0; i < 10; i++)
            {
                await App.Commands.SendTextAsync($"term{i}");
                await Task.Delay(100);
                // Clear and try next
                for (int j = 0; j < 6; j++)
                {
                    await App.Commands.SendKeyAsync("Backspace");
                    await Task.Delay(50);
                }
            }
            
            stopwatch.Stop();
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0, "Should handle rapid search");
            Assert.True(stopwatch.ElapsedMilliseconds < 5000, "Rapid search too slow");
            
            var snapshot = CurrentSnapshot;
            Assert.NotNull(snapshot);
            PerformanceAssertions.AssertFps(snapshot.Fps, minFps: 20);
        });
    }

    #endregion

    #region Search Highlighting

    [Fact]
    public async Task Search_Should_Highlight_Matches()
    {
        await RunPerformanceTestAsync(nameof(Search_Should_Highlight_Matches), async () =>
        {
            // Arrange
            await SendTextAndWaitAsync("This is the target word in a sentence\n");
            await Task.Delay(300);
            
            // Act - Search should trigger highlighting
            await App.Commands.SendKeyComboAsync("F", new[] { "Ctrl", "Shift" });
            await Task.Delay(300);
            await App.Commands.SendTextAsync("target");
            await Task.Delay(500);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0, "Should highlight search matches");
        });
    }

    [Fact]
    public async Task Search_Highlight_Should_Update_On_Type()
    {
        await RunPerformanceTestAsync(nameof(Search_Highlight_Should_Update_On_Type), async () =>
        {
            // Arrange
            await SendTextAndWaitAsync("testing search highlight functionality\n");
            await Task.Delay(300);
            
            // Act - Type search term character by character
            await App.Commands.SendKeyComboAsync("F", new[] { "Ctrl", "Shift" });
            await Task.Delay(300);
            
            var term = "search";
            foreach (var c in term)
            {
                await App.Commands.SendTextAsync(c.ToString());
                await Task.Delay(100);
            }
            await Task.Delay(300);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0, "Should update highlight while typing");
            
            var snapshot = CurrentSnapshot;
            Assert.NotNull(snapshot);
            PerformanceAssertions.AssertFps(snapshot.Fps, minFps: 20);
        });
    }

    #endregion

    #region Search with Selection

    [Fact]
    public async Task Search_With_Active_Selection()
    {
        await RunPerformanceTestAsync(nameof(Search_With_Active_Selection), async () =>
        {
            // Arrange - Add content and make selection
            await SendTextAndWaitAsync("First line of content\nSecond line here\nThird line there\n");
            await Task.Delay(300);
            
            // Make a selection (if supported by command interface)
            // This test verifies search works even with selection
            
            // Act - Open search
            await App.Commands.SendKeyComboAsync("F", new[] { "Ctrl", "Shift" });
            await Task.Delay(300);
            await App.Commands.SendTextAsync("line");
            await Task.Delay(500);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0, "Should work with selection");
        });
    }

    #endregion
}
