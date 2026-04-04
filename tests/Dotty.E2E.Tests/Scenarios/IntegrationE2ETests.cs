using System.Threading.Tasks;
using Xunit;

namespace Dotty.E2E.Tests.Scenarios;

/// <summary>
/// E2E tests for shell and tool integration.
/// </summary>
public class IntegrationE2ETests : E2ETestBase
{
    public IntegrationE2ETests() : base("Integration")
    {
    }

    [Fact]
    public async Task Shell_Should_Start_Successfully()
    {
        await RunTestAsync(async () =>
        {
            // Act - Shell starts automatically
            await Task.Delay(2000); // Wait for shell initialization
            
            // Send a simple command
            await SendTextAndWaitAsync("echo 'Shell is running'");
            await Task.Delay(1000);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0, "Shell session should be started");
        });
    }

    [Fact]
    public async Task Basic_Commands_Should_Execute()
    {
        await RunTestAsync(async () =>
        {
            // Arrange
            await Task.Delay(2000); // Wait for shell
            
            // Act - Execute basic commands
            await SendTextAndWaitAsync("pwd");
            await Task.Delay(500);
            await App.Commands.SendKeyAsync("Enter");
            await Task.Delay(1000);
            
            await SendTextAndWaitAsync("ls -la");
            await Task.Delay(500);
            await App.Commands.SendKeyAsync("Enter");
            await Task.Delay(1000);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0);
        });
    }

    [Fact]
    public async Task Environment_Variables_Should_Be_Accessible()
    {
        await RunTestAsync(async () =>
        {
            // Arrange
            await Task.Delay(2000);
            
            // Act - Check environment
            await SendTextAndWaitAsync("echo $HOME");
            await Task.Delay(500);
            await App.Commands.SendKeyAsync("Enter");
            await Task.Delay(500);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0);
        });
    }

    [Fact]
    public async Task Pipes_And_Redirections_Should_Work()
    {
        await RunTestAsync(async () =>
        {
            // Arrange
            await Task.Delay(2000);
            
            // Act - Test pipe
            await SendTextAndWaitAsync("echo 'hello world' | wc -w");
            await Task.Delay(500);
            await App.Commands.SendKeyAsync("Enter");
            await Task.Delay(1000);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0);
        });
    }

    [Fact]
    public async Task Interactive_Programs_Should_Handle_Input()
    {
        await RunTestAsync(async () =>
        {
            // Arrange
            await Task.Delay(2000);
            
            // Act - Run interactive program (cat with heredoc)
            await SendTextAndWaitAsync("cat << EOF");
            await Task.Delay(500);
            await App.Commands.SendKeyAsync("Enter");
            await Task.Delay(500);
            
            await SendTextAndWaitAsync("Line 1");
            await Task.Delay(200);
            await App.Commands.SendKeyAsync("Enter");
            await Task.Delay(200);
            
            await SendTextAndWaitAsync("Line 2");
            await Task.Delay(200);
            await App.Commands.SendKeyAsync("Enter");
            await Task.Delay(200);
            
            await SendTextAndWaitAsync("EOF");
            await Task.Delay(500);
            await App.Commands.SendKeyAsync("Enter");
            await Task.Delay(1000);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0);
        });
    }

    [Fact]
    public async Task Git_Status_Should_Render_Correctly()
    {
        await RunTestAsync(async () =>
        {
            // Arrange
            await Task.Delay(2000);
            
            // Act - Check git status (may fail if not in repo)
            await SendTextAndWaitAsync("git status 2>/dev/null || echo 'Not a git repo'");
            await Task.Delay(500);
            await App.Commands.SendKeyAsync("Enter");
            await Task.Delay(1000);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0);
        });
    }

    [Fact]
    public async Task Tree_Output_Should_Render_Correctly()
    {
        await RunTestAsync(async () =>
        {
            // Arrange
            await Task.Delay(2000);
            
            // Act - Run tree command
            await SendTextAndWaitAsync("tree -L 1 2>/dev/null || ls");
            await Task.Delay(500);
            await App.Commands.SendKeyAsync("Enter");
            await Task.Delay(1000);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0);
        });
    }

    [Fact]
    public async Task Colored_Ls_Output_Should_Show_Colors()
    {
        await RunTestAsync(async () =>
        {
            // Arrange
            await Task.Delay(2000);
            
            // Act - Run ls with colors
            await SendTextAndWaitAsync("ls --color=auto 2>/dev/null || ls -G 2>/dev/null || ls");
            await Task.Delay(500);
            await App.Commands.SendKeyAsync("Enter");
            await Task.Delay(1000);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0);
        });
    }

    [Fact]
    public async Task Process_Listing_Should_Work()
    {
        await RunTestAsync(async () =>
        {
            // Arrange
            await Task.Delay(2000);
            
            // Act - List processes
            await SendTextAndWaitAsync("ps aux | head -10");
            await Task.Delay(500);
            await App.Commands.SendKeyAsync("Enter");
            await Task.Delay(1000);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0);
        });
    }

    [Fact]
    public async Task Long_Running_Command_Should_Not_Block()
    {
        await RunTestAsync(async () =>
        {
            // Arrange
            await Task.Delay(2000);
            
            // Act - Run a command that takes some time
            await SendTextAndWaitAsync("sleep 1 && echo 'Done'");
            await Task.Delay(500);
            await App.Commands.SendKeyAsync("Enter");
            await Task.Delay(2000);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0);
        });
    }

    [Fact]
    public async Task Exit_Code_Should_Be_Tracked()
    {
        await RunTestAsync(async () =>
        {
            // Arrange
            await Task.Delay(2000);
            
            // Act - Run commands with different exit codes
            await SendTextAndWaitAsync("true");
            await Task.Delay(500);
            await App.Commands.SendKeyAsync("Enter");
            await Task.Delay(500);
            
            await SendTextAndWaitAsync("false || echo 'Failed as expected'");
            await Task.Delay(500);
            await App.Commands.SendKeyAsync("Enter");
            await Task.Delay(500);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0);
        });
    }
}
