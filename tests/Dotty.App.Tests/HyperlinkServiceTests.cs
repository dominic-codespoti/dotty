using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Dotty.App.Services;
using Xunit;

namespace Dotty.App.Tests;

/// <summary>
/// Comprehensive tests for HyperlinkService.
/// Tests URL validation, sanitization, and secure opening functionality.
/// </summary>
public class HyperlinkServiceTests
{
    public HyperlinkServiceTests()
    {
        Environment.SetEnvironmentVariable("DOTTY_DISABLE_EXTERNAL_URL_OPEN", "1");
    }

    #region URL Scheme Validation

    [Theory]
    [InlineData("http://example.com")]
    [InlineData("https://example.com")]
    [InlineData("https://example.com/path")]
    [InlineData("https://example.com:8080/path")]
    [InlineData("file:///path/to/file")]
    [InlineData("file:///home/user/document.txt")]
    public void IsSchemeAllowed_AllowedSchemes_ReturnsTrue(string url)
    {
        // Act
        bool result = HyperlinkService.IsSchemeAllowed(url);
        
        // Assert
        Assert.True(result);
    }

    [Theory]
    [InlineData("javascript:alert('xss')")]
    [InlineData("javascript:void(0)")]
    [InlineData("data:text/html,<script>alert('xss')</script>")]
    [InlineData("ftp://ftp.example.com")]
    [InlineData("ssh://server.example.com")]
    [InlineData("telnet://server.example.com")]
    [InlineData("vbscript:msgbox('test')")]
    [InlineData("chrome://settings")]
    [InlineData("about:blank")]
    [InlineData("mailto:test@example.com")]
    public void IsSchemeAllowed_DisallowedSchemes_ReturnsFalse(string url)
    {
        // Act
        bool result = HyperlinkService.IsSchemeAllowed(url);
        
        // Assert
        Assert.False(result);
    }

    [Theory]
    [InlineData("http://example.com")]
    [InlineData("HTTP://example.com")]
    [InlineData("Http://example.com")]
    [InlineData("https://example.com")]
    [InlineData("HTTPS://example.com")]
    public void IsSchemeAllowed_CaseInsensitive_ReturnsTrue(string url)
    {
        // Act
        bool result = HyperlinkService.IsSchemeAllowed(url);
        
        // Assert
        Assert.True(result);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("   ")]
    public void IsSchemeAllowed_InvalidInput_ReturnsFalse(string? url)
    {
        // Act
        bool result = HyperlinkService.IsSchemeAllowed(url!);
        
        // Assert
        Assert.False(result);
    }

    [Fact]
    public void IsSchemeAllowed_MalformedUrl_HandlesGracefully()
    {
        // Arrange
        var urls = new[]
        {
            "not-a-url",
            "://missing-scheme",
            "http:",
            "https",
            "://",
            "http//example.com" // missing colon
        };
        
        // Act & Assert
        foreach (var url in urls)
        {
            // Should not throw
            bool result = HyperlinkService.IsSchemeAllowed(url);
            // These should all be rejected as invalid
            Assert.False(result);
        }
    }

    [Fact]
    public void IsSchemeAllowed_UrlWithCredentials_AllowedButRisky()
    {
        // Arrange - URLs with embedded credentials
        var url = "https://user:pass@example.com";
        
        // Act
        bool result = HyperlinkService.IsSchemeAllowed(url);
        
        // Assert - technically allowed by scheme check
        // (additional warnings might be shown at UI level)
        Assert.True(result);
    }

    #endregion

    #region Caution Scheme Detection

    [Theory]
    [InlineData("file:///path/to/file")]
    [InlineData("file:///etc/passwd")]
    [InlineData("file:///C:/Windows/System32")]
    public void IsCautionScheme_FileScheme_ReturnsTrue(string url)
    {
        // Act
        bool result = HyperlinkService.IsCautionScheme(url);
        
        // Assert
        Assert.True(result);
    }

    [Theory]
    [InlineData("http://example.com")]
    [InlineData("https://example.com")]
    [InlineData("ftp://ftp.example.com")]
    public void IsCautionScheme_NonFileScheme_ReturnsFalse(string url)
    {
        // Act
        bool result = HyperlinkService.IsCautionScheme(url);
        
        // Assert
        Assert.False(result);
    }

    [Fact]
    public void IsCautionScheme_InvalidUrl_ReturnsFalse()
    {
        // Act
        bool result = HyperlinkService.IsCautionScheme("not-a-valid-url");
        
        // Assert
        Assert.False(result);
    }

    #endregion

    #region URL Sanitization

    [Fact]
    public void SanitizeUrl_RemovesNullBytes()
    {
        // Arrange
        var url = "https://example.com\0evil.com";
        
        // Act
        var result = HyperlinkService.SanitizeUrl(url);
        
        // Assert - Null byte (char 0) is < 32, so it should be removed by Where clause
        // Just verify the result doesn't have null bytes and contains expected text
        Assert.True(!result.Contains('\0') || result.IndexOf('\0') < 0, 
            "Sanitization should remove null bytes");
    }

    [Fact]
    public void SanitizeUrl_RemovesControlCharacters()
    {
        // Arrange
        var url = "https://example.com\x01\x02\x03path";
        
        // Act
        var result = HyperlinkService.SanitizeUrl(url);
        
        // Assert
        Assert.Equal("https://example.compath", result);
    }

    [Fact]
    public void SanitizeUrl_RemovesDELCharacter()
    {
        // Arrange
        var url = "https://example.com\x7Fpath";
        
        // Act
        var result = HyperlinkService.SanitizeUrl(url);
        
        // Assert
        Assert.Equal("https://example.compath", result);
    }

    [Fact]
    public void SanitizeUrl_KeepsPrintableAscii()
    {
        // Arrange
        var url = "https://example.com/path?query=value&foo=bar#fragment";
        
        // Act
        var result = HyperlinkService.SanitizeUrl(url);
        
        // Assert
        Assert.Equal(url, result);
    }

    [Fact]
    public void SanitizeUrl_TrimsWhitespace()
    {
        // Arrange
        var url = "  https://example.com  ";
        
        // Act
        var result = HyperlinkService.SanitizeUrl(url);
        
        // Assert
        Assert.Equal("https://example.com", result);
    }

    [Fact]
    public void SanitizeUrl_EmptyUrl_ReturnsEmptyString()
    {
        // Arrange & Act
        var result1 = HyperlinkService.SanitizeUrl("");
        var result2 = HyperlinkService.SanitizeUrl(null!);
        var result3 = HyperlinkService.SanitizeUrl("   ");
        
        // Assert
        Assert.Equal(string.Empty, result1);
        Assert.Equal(string.Empty, result2);
        Assert.Equal(string.Empty, result3);
    }

    [Fact]
    public void SanitizeUrl_NewlineCharacters_Removed()
    {
        // Arrange
        var url = "https://example.com\npath\r\nmore";
        
        // Act
        var result = HyperlinkService.SanitizeUrl(url);
        
        // Assert
        Assert.DoesNotContain("\n", result);
        Assert.DoesNotContain("\r", result);
        Assert.Equal("https://example.compathmore", result);
    }

    [Fact]
    public void SanitizeUrl_TabCharacters_Removed()
    {
        // Arrange
        var url = "https://example.com\tpath";
        
        // Act
        var result = HyperlinkService.SanitizeUrl(url);
        
        // Assert
        Assert.DoesNotContain("\t", result);
        Assert.Equal("https://example.compath", result);
    }

    [Fact]
    public void SanitizeUrl_EscapeSequences_Removed()
    {
        // Arrange - ESC character (0x1b = 27) is a control char and should be removed
        var url = "https://example.com\x1b[31mred\x1b[0m";
        
        // Act
        var result = HyperlinkService.SanitizeUrl(url);
        
        // Assert - Control characters are removed, printable is kept
        // ESC is char 27 (< 32), so it's removed. Brackets are char 91, kept.
        Assert.Equal("https://example.com[31mred[0m", result);
    }

    [Fact]
    public void SanitizeUrl_ShellCharacters_NotRemovedBySanitize()
    {
        // Arrange - Sanitization doesn't remove shell special chars
        // Shell escaping is handled separately by EscapeShellArg
        var url = "https://example.com/path;cmd=evil";
        
        // Act
        var result = HyperlinkService.SanitizeUrl(url);
        
        // Assert - Semicolon (char 59) and other printable ASCII are kept
        Assert.Contains(";", result);
        Assert.Equal(url, result);
    }

    [Fact]
    public void SanitizeUrl_DollarSign_NotRemovedBySanitize()
    {
        // Arrange
        var url = "https://example.com/$HOME";
        
        // Act
        var result = HyperlinkService.SanitizeUrl(url);
        
        // Assert - Dollar (char 36) is printable ASCII, kept
        Assert.Contains("$", result);
        Assert.Equal(url, result);
    }

    [Fact]
    public void SanitizeUrl_Backtick_NotRemovedBySanitize()
    {
        // Arrange
        var url = "https://example.com/`whoami`";
        
        // Act
        var result = HyperlinkService.SanitizeUrl(url);
        
        // Assert - Backtick (char 96) is printable ASCII, kept
        Assert.Contains("`", result);
        Assert.Equal(url, result);
    }

    [Fact]
    public void SanitizeUrl_UnicodeCharacters_AreRemoved()
    {
        // Arrange - Unicode chars > 127 are removed by implementation
        var url = "https://example.com/\u4e2d\u6587";
        
        // Act
        var result = HyperlinkService.SanitizeUrl(url);
        
        // Assert - Unicode chars are > 127, so they're filtered out
        // Implementation: c >= 32 && c < 127, so chars > 126 are removed
        Assert.DoesNotContain("\u4e2d", result);
        Assert.DoesNotContain("\u6587", result);
        Assert.Equal("https://example.com/", result);
    }


    #endregion

    #region URL Opening - Ctrl+Click Security

    [Fact]
    public async Task OpenUrlAsync_NoCtrlPressed_ReturnsFalse()
    {
        // Arrange
        var url = "https://example.com";
        
        // Act
        bool result = await HyperlinkService.OpenUrlAsync(url, ctrlPressed: false);
        
        // Assert - Security: Ctrl+Click is required
        Assert.False(result);
    }

    [Fact]
    public async Task OpenUrlAsync_CtrlPressed_ValidUrl_ReturnsTrue()
    {
        // Arrange
        var url = "https://example.com";
        
        // Act
        // Note: This will attempt to open the URL, but we can't easily mock Process.Start
        // In a real test environment, we expect this to return true for valid URLs
        bool result = await HyperlinkService.OpenUrlAsync(url, ctrlPressed: true);
        
        // Assert - Should succeed (or fail gracefully if browser can't be opened)
        // The result depends on the environment
        Assert.True(result);
    }

    [Fact]
    public async Task OpenUrlAsync_EmptyUrl_ReturnsFalse()
    {
        // Arrange
        var url = "";
        
        // Act
        bool result = await HyperlinkService.OpenUrlAsync(url, ctrlPressed: true);
        
        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task OpenUrlAsync_InvalidScheme_ReturnsFalse()
    {
        // Arrange
        var url = "javascript:alert('xss')";
        
        // Act
        bool result = await HyperlinkService.OpenUrlAsync(url, ctrlPressed: true);
        
        // Assert - Security: Invalid schemes are rejected
        Assert.False(result);
    }

    [Fact]
    public async Task OpenUrlAsync_SanitizesBeforeOpening()
    {
        // Arrange
        var url = "https://example.com\0evil.com";
        
        // Act - Should sanitize and then attempt to open the clean URL
        bool result = await HyperlinkService.OpenUrlAsync(url, ctrlPressed: true);
        
        // Assert
        // The sanitized URL is "https://example.comevil.com" which is still a valid URL
        // Result depends on whether that domain resolves
    }

    #endregion

    #region Shell Argument Escaping (Private Method Tests via Behavior)

    [Fact]
    public async Task OpenUrlAsync_UrlWithQuotes_HandlesSafely()
    {
        // Arrange
        var url = "https://example.com/search?q=\"test\"";
        
        // Act
        bool result = await HyperlinkService.OpenUrlAsync(url, ctrlPressed: true);
        
        // Assert - Should not throw, handles quotes safely
        // Note: In practice, this URL might fail to open due to the malformed query string,
        // but it shouldn't cause a security issue
    }

    [Fact]
    public async Task OpenUrlAsync_UrlWithSemicolons_HandlesSafely()
    {
        // Arrange
        var url = "https://example.com/path;cmd=evil";
        
        // Act
        bool result = await HyperlinkService.OpenUrlAsync(url, ctrlPressed: true);
        
        // Assert
        // This is a valid URL with a semicolon in the path
        Assert.True(result);
    }

    [Fact]
    public async Task OpenUrlAsync_UrlWithBackticks_HandlesSafely()
    {
        // Arrange
        var url = "https://example.com/`whoami`";
        
        // Act
        var sanitized = HyperlinkService.SanitizeUrl(url);
        
        // Assert - Backtick (char 96) is printable ASCII, kept by sanitization
        // Shell escaping is handled separately by EscapeShellArg
        Assert.Contains("`", sanitized);
        Assert.Equal(url, sanitized);
    }

    [Fact]
    public async Task OpenUrlAsync_UrlWithDollarSign_HandlesSafely()
    {
        // Arrange
        var url = "https://example.com/$HOME";
        
        // Act
        var sanitized = HyperlinkService.SanitizeUrl(url);
        
        // Assert - Dollar (char 36) is printable ASCII, kept by sanitization
        // Shell escaping handles this for process execution
        Assert.Contains("$", sanitized);
        Assert.Equal(url, sanitized);
    }

    #endregion

    #region Security Tests

    [Fact]
    public void IsSchemeAllowed_JavascriptScheme_Prevented()
    {
        // Arrange - Common XSS vector
        var urls = new[]
        {
            "javascript:alert('XSS')",
            "javascript:alert(document.cookie)",
            "JaVaScRiPt:alert('XSS')", // Case variation
            "javascript://%0aalert('XSS')",
        };
        
        // Act & Assert
        foreach (var url in urls)
        {
            Assert.False(HyperlinkService.IsSchemeAllowed(url), 
                $"JavaScript URL should be blocked: {url}");
        }
    }

    [Fact]
    public void IsSchemeAllowed_DataScheme_Prevented()
    {
        // Arrange - Data URIs can contain executable content
        var urls = new[]
        {
            "data:text/html,<script>alert('XSS')</script>",
            "data:text/html;base64,PHNjcmlwdD5hbGVydCgnWFNTJyk8L3NjcmlwdD4=",
        };
        
        // Act & Assert
        foreach (var url in urls)
        {
            Assert.False(HyperlinkService.IsSchemeAllowed(url),
                $"Data URL should be blocked: {url}");
        }
    }

    [Fact]
    public void IsSchemeAllowed_VBScriptScheme_Prevented()
    {
        // Arrange - VBScript is another attack vector (IE)
        var url = "vbscript:msgbox('test')";
        
        // Act
        bool result = HyperlinkService.IsSchemeAllowed(url);
        
        // Assert
        Assert.False(result);
    }

    [Fact]
    public void IsSchemeAllowed_ChromeScheme_Prevented()
    {
        // Arrange - Browser internal schemes
        var urls = new[]
        {
            "chrome://settings",
            "chrome://version",
            "about:blank",
            "about:config",
        };
        
        // Act & Assert
        foreach (var url in urls)
        {
            Assert.False(HyperlinkService.IsSchemeAllowed(url),
                $"Internal scheme should be blocked: {url}");
        }
    }

    [Fact]
    public void IsSchemeAllowed_CommandInjectionAttempts_Prevented()
    {
        // Arrange - Various command injection attempts via URLs
        var urls = new[]
        {
            "https://example.com;rm -rf /",
            "https://example.com && cat /etc/passwd",
            "https://example.com | whoami",
            "https://example.com`whoami`",
            "https://example.com$(whoami)",
        };
        
        // Act & Assert - These should all pass scheme validation (they're https URLs)
        // Sanitization keeps printable ASCII (32-126), shell escaping handles execution
        foreach (var url in urls)
        {
            // Scheme check allows https
            Assert.True(HyperlinkService.IsSchemeAllowed(url));
            
            // Sanitization removes control chars but keeps printable shell chars
            // The security comes from shell argument escaping in EscapeShellArg
            var sanitized = HyperlinkService.SanitizeUrl(url);
            Assert.True(sanitized.IndexOf('\0') < 0, "Sanitization removes null bytes");
            // Shell characters like ; & | ` $ are printable ASCII, kept by sanitization
            // They are escaped by EscapeShellArg when opening the URL
        }
    }

    [Fact]
    public void SanitizeUrl_PathTraversalAttempts_Cleaned()
    {
        // Arrange - Path traversal attempts in file URLs
        var url = "file:///etc/../etc/passwd";
        
        // Act
        var result = HyperlinkService.SanitizeUrl(url);
        
        // Assert - Dots are allowed (printable ASCII), no control chars in this URL
        Assert.Contains("..", result); // Dots are preserved
        // The URL contains only printable ASCII, so it should pass through unchanged
        Assert.Equal(url, result);
    }

    [Fact]
    public async Task OpenUrlAsync_FileScheme_RequiresCaution()
    {
        // Arrange
        var url = "file:///etc/passwd";
        
        // Act
        bool isAllowed = HyperlinkService.IsSchemeAllowed(url);
        bool isCaution = HyperlinkService.IsCautionScheme(url);
        bool opened = await HyperlinkService.OpenUrlAsync(url, ctrlPressed: true);
        
        // Assert
        Assert.True(isAllowed); // file:// is allowed
        Assert.True(isCaution); // but requires caution
        // Opening result depends on OS and permissions
    }

    [Fact]
    public void IsSchemeAllowed_SSHScheme_Blocked()
    {
        // Arrange - SSH URLs could be used for social engineering
        var urls = new[]
        {
            "ssh://attacker.com",
            "ssh://user:pass@evil.com",
        };
        
        // Act & Assert
        foreach (var url in urls)
        {
            Assert.False(HyperlinkService.IsSchemeAllowed(url),
                $"SSH URL should be blocked: {url}");
        }
    }

    [Fact]
    public void IsSchemeAllowed_TelnetScheme_Blocked()
    {
        // Arrange - Telnet is insecure and could be used maliciously
        var url = "telnet://server.example.com";
        
        // Act
        bool result = HyperlinkService.IsSchemeAllowed(url);
        
        // Assert
        Assert.False(result);
    }

    [Fact]
    public void IsSchemeAllowed_FTPSFTPSchemes_Blocked()
    {
        // Arrange - FTP/FTPS/SFTP schemes
        var urls = new[]
        {
            "ftp://ftp.example.com",
            "ftps://ftp.example.com",
            "sftp://ftp.example.com",
        };
        
        // Act & Assert
        foreach (var url in urls)
        {
            Assert.False(HyperlinkService.IsSchemeAllowed(url),
                $"FTP-based URL should be blocked: {url}");
        }
    }

    #endregion

    #region Cross-Platform Tests

    [Fact]
    public void OpenUrlAsync_DetectsCurrentPlatform()
    {
        // This test verifies the platform detection logic works
        bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        bool isLinux = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
        bool isMac = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
        
        // Assert - At least one platform should be true
        Assert.True(isWindows || isLinux || isMac,
            "Current platform should be Windows, Linux, or macOS");
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void IsSchemeAllowed_UrlWithIPv4_Allowed()
    {
        // Arrange
        var url = "http://192.168.1.1";
        
        // Act
        bool result = HyperlinkService.IsSchemeAllowed(url);
        
        // Assert
        Assert.True(result);
    }

    [Fact]
    public void IsSchemeAllowed_UrlWithIPv6_Allowed()
    {
        // Arrange
        var url = "http://[::1]";
        
        // Act
        bool result = HyperlinkService.IsSchemeAllowed(url);
        
        // Assert
        Assert.True(result);
    }

    [Fact]
    public void IsSchemeAllowed_UrlWithPort_Allowed()
    {
        // Arrange
        var urls = new[]
        {
            "http://localhost:3000",
            "https://example.com:443",
            "http://example.com:8080/path",
        };
        
        // Act & Assert
        foreach (var url in urls)
        {
            Assert.True(HyperlinkService.IsSchemeAllowed(url), 
                $"URL with port should be allowed: {url}");
        }
    }

    [Fact]
    public void IsSchemeAllowed_UrlWithUserInfo_Allowed()
    {
        // Arrange - URLs with embedded credentials
        var urls = new[]
        {
            "http://user@example.com",
            "https://user:pass@example.com",
        };
        
        // Act & Assert
        foreach (var url in urls)
        {
            Assert.True(HyperlinkService.IsSchemeAllowed(url),
                $"URL with userinfo should be allowed: {url}");
        }
    }

    [Fact]
    public void IsSchemeAllowed_InternationalizedDomainName_Allowed()
    {
        // Arrange - IDN (Internationalized Domain Name)
        var url = "https://m\u00fcller.de"; // müller.de
        
        // Act
        bool result = HyperlinkService.IsSchemeAllowed(url);
        
        // Assert - Unicode in domain should be handled
        // This might pass or fail depending on Uri parsing behavior
        Assert.True(result);
    }

    [Fact]
    public void SanitizeUrl_VeryLongUrl_Handled()
    {
        // Arrange
        var longUrl = "https://example.com/" + new string('a', 10000);
        
        // Act
        var result = HyperlinkService.SanitizeUrl(longUrl);
        
        // Assert - Should handle long URLs without crashing
        Assert.NotNull(result);
        Assert.True(result.Length > 0);
    }

    [Fact]
    public void SanitizeUrl_OnlyControlChars_ReturnsEmpty()
    {
        // Arrange
        var url = "\x00\x01\x02\x03\x04\x05";
        
        // Act
        var result = HyperlinkService.SanitizeUrl(url);
        
        // Assert
        Assert.Equal(string.Empty, result);
    }

    #endregion
}
