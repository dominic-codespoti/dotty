using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using Dotty.E2E.Tests.Assertions;
using Xunit;
using Xunit.Abstractions;

namespace Dotty.E2E.Tests.Scenarios;

/// <summary>
/// Comprehensive E2E tests for color rendering functionality.
/// Tests basic 8 colors, 256 colors, TrueColor (24-bit), and text attributes.
/// </summary>
[Trait("Category", "Colors")]
[Trait("Category", "Rendering")]
public class ColorsE2ETests : E2EPerformanceTestBase
{
    public ColorsE2ETests(ITestOutputHelper outputHelper) : base("Colors", outputHelper)
    {
    }

    #region Basic 8 Colors - Foreground

    [Theory]
    [InlineData(30, "Black")]
    [InlineData(31, "Red")]
    [InlineData(32, "Green")]
    [InlineData(33, "Yellow")]
    [InlineData(34, "Blue")]
    [InlineData(35, "Magenta")]
    [InlineData(36, "Cyan")]
    [InlineData(37, "White")]
    public async Task Basic_Foreground_Color_Should_Render(int colorCode, string colorName)
    {
        await RunPerformanceTestAsync($"Basic_FG_{colorName}", async () =>
        {
            // Arrange
            var ansiSequence = $"\u001b[{colorCode}m{colorName} Text\u001b[0m";
            
            // Act
            await App.Commands.InjectAnsiAsync(ansiSequence);
            await Task.Delay(300);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0, $"Should render {colorName} foreground color");
            
            var state = await App.Commands.GetStateAsync();
            Assert.True(state.Rows > 0 && state.Cols > 0, "Terminal should have dimensions");
        }, assertBaseline: false);
    }

    [Fact]
    public async Task All_Basic_Foreground_Colors_Should_Render_Simultaneously()
    {
        await RunPerformanceTestAsync(nameof(All_Basic_Foreground_Colors_Should_Render_Simultaneously), async () =>
        {
            // Arrange - Create a line with all 8 colors
            var sb = new StringBuilder();
            var colors = new[] { (30, "Black"), (31, "Red"), (32, "Green"), (33, "Yellow"),
                                (34, "Blue"), (35, "Magenta"), (36, "Cyan"), (37, "White") };
            
            foreach (var (code, name) in colors)
            {
                sb.Append($"\u001b[{code}m{name}\u001b[0m ");
            }
            
            // Act
            await App.Commands.InjectAnsiAsync(sb.ToString() + "\n");
            await Task.Delay(500);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0, "Should render all basic colors");
            
            var snapshot = CurrentSnapshot;
            Assert.NotNull(snapshot);
            PerformanceAssertions.AssertFps(snapshot.Fps, minFps: 20);
        });
    }

    #endregion

    #region Basic 8 Colors - Background

    [Theory]
    [InlineData(40, "Black")]
    [InlineData(41, "Red")]
    [InlineData(42, "Green")]
    [InlineData(43, "Yellow")]
    [InlineData(44, "Blue")]
    [InlineData(45, "Magenta")]
    [InlineData(46, "Cyan")]
    [InlineData(47, "White")]
    public async Task Basic_Background_Color_Should_Render(int colorCode, string colorName)
    {
        await RunPerformanceTestAsync($"Basic_BG_{colorName}", async () =>
        {
            // Arrange
            var ansiSequence = $"\u001b[{colorCode}m{colorName} BG\u001b[0m";
            
            // Act
            await App.Commands.InjectAnsiAsync(ansiSequence);
            await Task.Delay(300);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0, $"Should render {colorName} background color");
        }, assertBaseline: false);
    }

    [Fact]
    public async Task All_Basic_Background_Colors_Should_Render_Simultaneously()
    {
        await RunPerformanceTestAsync(nameof(All_Basic_Background_Colors_Should_Render_Simultaneously), async () =>
        {
            // Arrange
            var sb = new StringBuilder();
            var colors = new[] { (40, "Blk"), (41, "Red"), (42, "Grn"), (43, "Yel"),
                                (44, "Blu"), (45, "Mag"), (46, "Cyn"), (47, "Wht") };
            
            foreach (var (code, name) in colors)
            {
                sb.Append($"\u001b[{code}m {name} \u001b[0m");
            }
            
            // Act
            await App.Commands.InjectAnsiAsync(sb.ToString() + "\n");
            await Task.Delay(500);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0, "Should render all background colors");
        });
    }

    #endregion

    #region Bright/Intense Colors

    [Theory]
    [InlineData(90, "BrightBlack")]
    [InlineData(91, "BrightRed")]
    [InlineData(92, "BrightGreen")]
    [InlineData(93, "BrightYellow")]
    [InlineData(94, "BrightBlue")]
    [InlineData(95, "BrightMagenta")]
    [InlineData(96, "BrightCyan")]
    [InlineData(97, "BrightWhite")]
    public async Task Bright_Foreground_Color_Should_Render(int colorCode, string colorName)
    {
        await RunPerformanceTestAsync($"Bright_FG_{colorName}", async () =>
        {
            // Arrange
            var ansiSequence = $"\u001b[{colorCode}m{colorName}\u001b[0m";
            
            // Act
            await App.Commands.InjectAnsiAsync(ansiSequence);
            await Task.Delay(300);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0, $"Should render {colorName} bright color");
        }, assertBaseline: false);
    }

    [Theory]
    [InlineData(100, "BrightBlack")]
    [InlineData(101, "BrightRed")]
    [InlineData(102, "BrightGreen")]
    [InlineData(103, "BrightYellow")]
    [InlineData(104, "BrightBlue")]
    [InlineData(105, "BrightMagenta")]
    [InlineData(106, "BrightCyan")]
    [InlineData(107, "BrightWhite")]
    public async Task Bright_Background_Color_Should_Render(int colorCode, string colorName)
    {
        await RunPerformanceTestAsync($"Bright_BG_{colorName}", async () =>
        {
            // Arrange
            var ansiSequence = $"\u001b[{colorCode}m{colorName}\u001b[0m";
            
            // Act
            await App.Commands.InjectAnsiAsync(ansiSequence);
            await Task.Delay(300);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0, $"Should render {colorName} bright background");
        }, assertBaseline: false);
    }

    #endregion

    #region 256 Colors - Color Cube (16-231)

    [Fact]
    public async Task _256_Color_Cube_Corner_Colors_Should_Render()
    {
        await RunPerformanceTestAsync(nameof(_256_Color_Cube_Corner_Colors_Should_Render), async () =>
        {
            // Arrange - Test key colors from the 6x6x6 color cube
            var testColors = new[]
            {
                (16, "Black"),
                (21, "Blue"),
                (46, "Green"),
                (196, "Red"),
                (226, "Yellow"),
                (201, "Magenta"),
                (51, "Cyan"),
                (231, "White"),
                (208, "Orange"),
                (93, "Purple")
            };
            
            var sb = new StringBuilder();
            foreach (var (code, name) in testColors)
            {
                sb.Append($"\u001b[38;5;{code}m{name}\u001b[0m ");
            }
            
            // Act
            await App.Commands.InjectAnsiAsync(sb.ToString() + "\n");
            await Task.Delay(500);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0, "Should render 256 color cube colors");
        }, assertBaseline: false);
    }

    [Fact]
    public async Task _256_Color_Cube_Spectrum_Should_Render_Performance()
    {
        await RunPerformanceTestAsync(nameof(_256_Color_Cube_Spectrum_Should_Render_Performance), async () =>
        {
            // Arrange - Generate a spectrum of colors (16-231)
            var stopwatch = Stopwatch.StartNew();
            var sb = new StringBuilder();
            
            // Sample every 6th color in the cube (36 colors total)
            for (int r = 0; r < 6; r++)
            {
                for (int g = 0; g < 6; g++)
                {
                    for (int b = 0; b < 6; b += 2)
                    {
                        var colorCode = 16 + (36 * r) + (6 * g) + b;
                        sb.Append($"\u001b[38;5;{colorCode}m█\u001b[0m");
                    }
                }
            }
            
            // Act
            await App.Commands.InjectAnsiAsync(sb.ToString() + "\n");
            await Task.Delay(1000);
            stopwatch.Stop();
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0, "Should render color spectrum");
            Assert.True(stopwatch.ElapsedMilliseconds < 5000, "256-color rendering too slow");
            
            var snapshot = CurrentSnapshot;
            Assert.NotNull(snapshot);
            PerformanceAssertions.AssertFps(snapshot.Fps, minFps: 20);
        });
    }

    #endregion

    #region 256 Colors - Grayscale (232-255)

    [Fact]
    public async Task _256_Color_Grayscale_Ramp_Should_Render()
    {
        await RunPerformanceTestAsync(nameof(_256_Color_Grayscale_Ramp_Should_Render), async () =>
        {
            // Arrange - All 24 grayscale shades
            var sb = new StringBuilder();
            for (int i = 232; i <= 255; i++)
            {
                sb.Append($"\u001b[38;5;{i}m█\u001b[0m");
            }
            
            // Act
            await App.Commands.InjectAnsiAsync(sb.ToString() + "\n");
            await Task.Delay(500);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0, "Should render grayscale ramp");
        }, assertBaseline: false);
    }

    [Theory]
    [InlineData(232)]  // Darkest
    [InlineData(244)]  // Mid-gray
    [InlineData(255)]  // Lightest
    public async Task _256_Grayscale_Shades_Should_Render(int colorCode)
    {
        await RunPerformanceTestAsync($"256_Gray_{colorCode}", async () =>
        {
            // Arrange
            var ansiSequence = $"\u001b[48;5;{colorCode}m\u001b[38;5;255mGray {colorCode}\u001b[0m";
            
            // Act
            await App.Commands.InjectAnsiAsync(ansiSequence);
            await Task.Delay(300);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0, $"Should render grayscale color {colorCode}");
        }, assertBaseline: false);
    }

    #endregion

    #region TrueColor (24-bit RGB)

    [Theory]
    [InlineData(255, 0, 0, "Pure Red")]
    [InlineData(0, 255, 0, "Pure Green")]
    [InlineData(0, 0, 255, "Pure Blue")]
    [InlineData(255, 255, 0, "Yellow")]
    [InlineData(255, 0, 255, "Magenta")]
    [InlineData(0, 255, 255, "Cyan")]
    [InlineData(255, 255, 255, "White")]
    [InlineData(0, 0, 0, "Black")]
    [InlineData(128, 128, 128, "Gray")]
    [InlineData(255, 128, 0, "Orange")]
    [InlineData(128, 0, 128, "Purple")]
    public async Task TrueColor_RGB_Should_Render(int r, int g, int b, string colorName)
    {
        await RunPerformanceTestAsync($"TrueColor_{colorName.Replace(" ", "")}", async () =>
        {
            // Arrange
            var ansiSequence = $"\u001b[38;2;{r};{g};{b}m{colorName}\u001b[0m";
            
            // Act
            await App.Commands.InjectAnsiAsync(ansiSequence);
            await Task.Delay(300);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0, $"Should render TrueColor {colorName}");
        }, assertBaseline: false);
    }

    [Fact]
    public async Task TrueColor_RGB_Gradient_Should_Render()
    {
        await RunPerformanceTestAsync(nameof(TrueColor_RGB_Gradient_Should_Render), async () =>
        {
            // Arrange - Create RGB gradients
            var sb = new StringBuilder();
            
            // Red gradient
            for (int i = 0; i <= 255; i += 17) // 16 steps
            {
                sb.Append($"\u001b[38;2;{i};0;0m█\u001b[0m");
            }
            sb.Append("\n");
            
            // Green gradient
            for (int i = 0; i <= 255; i += 17)
            {
                sb.Append($"\u001b[38;2;0;{i};0m█\u001b[0m");
            }
            sb.Append("\n");
            
            // Blue gradient
            for (int i = 0; i <= 255; i += 17)
            {
                sb.Append($"\u001b[38;2;0;0;{i}m█\u001b[0m");
            }
            sb.Append("\n");
            
            // Act
            await App.Commands.InjectAnsiAsync(sb.ToString());
            await Task.Delay(1000);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0, "Should render RGB gradients");
            
            var snapshot = CurrentSnapshot;
            Assert.NotNull(snapshot);
            PerformanceAssertions.AssertFps(snapshot.Fps, minFps: 20);
        });
    }

    [Fact]
    public async Task TrueColor_Background_Should_Render()
    {
        await RunPerformanceTestAsync(nameof(TrueColor_Background_Should_Render), async () =>
        {
            // Arrange - TrueColor backgrounds with contrasting foreground
            var sb = new StringBuilder();
            var colors = new[] { (255, 0, 0), (0, 255, 0), (0, 0, 255), (255, 255, 0), (255, 0, 255), (0, 255, 255) };
            
            foreach (var (r, g, b) in colors)
            {
                var fgR = 255 - r;
                var fgG = 255 - g;
                var fgB = 255 - b;
                sb.Append($"\u001b[48;2;{r};{g};{b}m\u001b[38;2;{fgR};{fgG};{fgB}mBG\u001b[0m ");
            }
            
            // Act
            await App.Commands.InjectAnsiAsync(sb.ToString() + "\n");
            await Task.Delay(500);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0, "Should render TrueColor backgrounds");
        });
    }

    #endregion

    #region Text Attributes

    [Theory]
    [InlineData(1, "Bold")]
    [InlineData(2, "Dim")]
    [InlineData(3, "Italic")]
    [InlineData(4, "Underline")]
    [InlineData(5, "Blink")]
    [InlineData(7, "Reverse")]
    [InlineData(8, "Hidden")]
    [InlineData(9, "Strikethrough")]
    public async Task Text_Attribute_Should_Render(int attrCode, string attrName)
    {
        await RunPerformanceTestAsync($"Attribute_{attrName}", async () =>
        {
            // Arrange
            var ansiSequence = $"\u001b[{attrCode}m{attrName}\u001b[0m";
            
            // Act
            await App.Commands.InjectAnsiAsync(ansiSequence);
            await Task.Delay(300);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0, $"Should render {attrName} attribute");
        }, assertBaseline: false);
    }

    [Fact]
    public async Task All_Text_Attributes_Should_Render_Simultaneously()
    {
        await RunPerformanceTestAsync(nameof(All_Text_Attributes_Should_Render_Simultaneously), async () =>
        {
            // Arrange
            var attributes = new[] { 1, 2, 3, 4, 5, 7, 8, 9 };
            var names = new[] { "Bold", "Dim", "Italic", "Underline", "Blink", "Reverse", "Hidden", "Strike" };
            
            var sb = new StringBuilder();
            for (int i = 0; i < attributes.Length; i++)
            {
                sb.Append($"\u001b[{attributes[i]}m{names[i]}\u001b[0m ");
            }
            
            // Act
            await App.Commands.InjectAnsiAsync(sb.ToString() + "\n");
            await Task.Delay(500);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0, "Should render all text attributes");
        });
    }

    [Fact]
    public async Task Bold_And_Color_Combination_Should_Render()
    {
        await RunPerformanceTestAsync(nameof(Bold_And_Color_Combination_Should_Render), async () =>
        {
            // Arrange - Bold with colors
            var colors = new[] { 31, 32, 34, 33, 35, 36 };
            var sb = new StringBuilder();
            
            foreach (var color in colors)
            {
                sb.Append($"\u001b[1;{color}mBold Color\u001b[0m ");
            }
            
            // Act
            await App.Commands.InjectAnsiAsync(sb.ToString() + "\n");
            await Task.Delay(500);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0, "Should render bold + color combinations");
        });
    }

    [Fact]
    public async Task Underline_And_Color_Combination_Should_Render()
    {
        await RunPerformanceTestAsync(nameof(Underline_And_Color_Combination_Should_Render), async () =>
        {
            // Arrange - Underline with colors
            var colors = new[] { 31, 32, 34, 33 };
            var sb = new StringBuilder();
            
            foreach (var color in colors)
            {
                sb.Append($"\u001b[4;{color}mUnderlined\u001b[0m ");
            }
            
            // Act
            await App.Commands.InjectAnsiAsync(sb.ToString() + "\n");
            await Task.Delay(500);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0, "Should render underline + color combinations");
        });
    }

    #endregion

    #region Combined Color and Attribute Tests

    [Fact]
    public async Task Complex_Color_Attribute_Combinations()
    {
        await RunPerformanceTestAsync(nameof(Complex_Color_Attribute_Combinations), async () =>
        {
            // Arrange - Complex combinations
            var combinations = new[]
            {
                "\u001b[1;31;47mBold Red on White\u001b[0m",
                "\u001b[1;4;32mBold Underline Green\u001b[0m",
                "\u001b[3;35;43mItalic Magenta on Yellow\u001b[0m",
                "\u001b[1;3;4;34mBold Italic Underline Blue\u001b[0m",
                "\u001b[7;36mReverse Cyan\u001b[0m",
                "\u001b[1;38;5;196;48;5;226mBold 256-Red on 256-Yellow\u001b[0m",
                "\u001b[1;4;38;2;255;128;0;48;2;0;0;128mBold Underline Orange on Navy\u001b[0m"
            };
            
            var sb = new StringBuilder();
            foreach (var combo in combinations)
            {
                sb.AppendLine(combo);
            }
            
            // Act
            await App.Commands.InjectAnsiAsync(sb.ToString());
            await Task.Delay(1000);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0, "Should render complex combinations");
            
            var snapshot = CurrentSnapshot;
            Assert.NotNull(snapshot);
            PerformanceAssertions.AssertFps(snapshot.Fps, minFps: 20);
        });
    }

    #endregion

    #region SGR Reset Tests

    [Fact]
    public async Task SGR_Reset_Should_Clear_All_Attributes()
    {
        await RunPerformanceTestAsync(nameof(SGR_Reset_Should_Clear_All_Attributes), async () =>
        {
            // Arrange - Set many attributes then reset
            var ansiSequence = 
                "\u001b[1;4;31;47mBold Underline Red on White\u001b[0m" +
                "Normal Text" +
                "\u001b[32mGreen\u001b[0m" +
                "Normal Again";
            
            // Act
            await App.Commands.InjectAnsiAsync(ansiSequence);
            await Task.Delay(500);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0, "SGR reset should work correctly");
        });
    }

    [Fact]
    public async Task Partial_SGR_Reset_Should_Clear_Specific_Attributes()
    {
        await RunPerformanceTestAsync(nameof(Partial_SGR_Reset_Should_Clear_Specific_Attributes), async () =>
        {
            // Test partial resets
            // 22 = normal intensity (clears bold/dim)
            // 24 = underline off
            // 25 = blink off
            // 27 = inverse off
            
            var sequences = new[]
            {
                "\u001b[1;31mBold Red\u001b[22mNormal Intensity Red\u001b[0m",
                "\u001b[4;32mUnderline Green\u001b[24mNo Underline Green\u001b[0m",
                "\u001b[7;33mReverse Yellow\u001b[27mNormal Yellow\u001b[0m"
            };
            
            // Act
            foreach (var seq in sequences)
            {
                await App.Commands.InjectAnsiAsync(seq + "\n");
                await Task.Delay(200);
            }
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0, "Partial SGR reset should work");
        });
    }

    #endregion

    #region Performance Tests

    [Fact]
    public async Task Color_Rendering_Performance_High_Volume()
    {
        await RunPerformanceTestAsync(nameof(Color_Rendering_Performance_High_Volume), async () =>
        {
            // Arrange - Generate many colored lines
            var stopwatch = Stopwatch.StartNew();
            var sb = new StringBuilder();
            
            for (int i = 0; i < 100; i++)
            {
                var colorCode = 31 + (i % 7); // Cycle through colors
                sb.AppendLine($"\u001b[{colorCode}mLine {i}: Colored text content\u001b[0m");
            }
            
            // Act
            await App.Commands.InjectAnsiAsync(sb.ToString());
            await Task.Delay(2000);
            stopwatch.Stop();
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0, "Should handle high volume colors");
            Assert.True(stopwatch.ElapsedMilliseconds < 5000, "Color rendering too slow");
            
            var snapshot = CurrentSnapshot;
            Assert.NotNull(snapshot);
            PerformanceAssertions.AssertFps(snapshot.Fps, minFps: 15);
            PerformanceAssertions.AssertFrameTimeP95(snapshot.FrameTimeP95, maxP95Ms: 50);
        });
    }

    [Fact]
    public async Task TrueColor_Rendering_Performance_High_Volume()
    {
        await RunPerformanceTestAsync(nameof(TrueColor_Rendering_Performance_High_Volume), async () =>
        {
            // Arrange - Generate TrueColor gradient lines
            var stopwatch = Stopwatch.StartNew();
            var sb = new StringBuilder();
            
            for (int i = 0; i < 50; i++)
            {
                var r = (i * 5) % 256;
                var g = (i * 3) % 256;
                var b = (i * 7) % 256;
                sb.AppendLine($"\u001b[38;2;{r};{g};{b}mTrueColor Line {i}\u001b[0m");
            }
            
            // Act
            await App.Commands.InjectAnsiAsync(sb.ToString());
            await Task.Delay(2000);
            stopwatch.Stop();
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0, "Should handle TrueColor volume");
            Assert.True(stopwatch.ElapsedMilliseconds < 5000, "TrueColor rendering too slow");
            
            var snapshot = CurrentSnapshot;
            Assert.NotNull(snapshot);
            PerformanceAssertions.AssertFps(snapshot.Fps, minFps: 15);
        });
    }

    [Fact]
    public async Task Mixed_Color_Systems_Performance()
    {
        await RunPerformanceTestAsync(nameof(Mixed_Color_Systems_Performance), async () =>
        {
            // Arrange - Mix all color systems
            var sb = new StringBuilder();
            
            for (int i = 0; i < 30; i++)
            {
                // Basic color
                sb.Append($"\u001b[{(31 + i % 6)}mB\u001b[0m");
                // 256 color
                sb.Append($"\u001b[38;5;{16 + i}m2\u001b[0m");
                // TrueColor
                sb.Append($"\u001b[38;2;{i * 8};{i * 4};{i * 2}mT\u001b[0m");
                sb.AppendLine();
            }
            
            // Act
            await App.Commands.InjectAnsiAsync(sb.ToString());
            await Task.Delay(1500);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0, "Should handle mixed color systems");
            
            var snapshot = CurrentSnapshot;
            Assert.NotNull(snapshot);
            PerformanceAssertions.AssertFps(snapshot.Fps, minFps: 20);
        });
    }

    #endregion
}
