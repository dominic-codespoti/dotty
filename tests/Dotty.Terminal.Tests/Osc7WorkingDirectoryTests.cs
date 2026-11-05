using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Dotty.Terminal.Adapter;
using Xunit;

namespace Dotty.Terminal.Tests;

public sealed class Osc7WorkingDirectoryTests
{
    private static readonly string[] Hosts = { "box" };

    private static bool Parse(string payload, out string path, bool windows = false) =>
        Osc7WorkingDirectory.TryParse(payload, windows, Hosts, out path);

    private static void AssertPath(string payload, string expected, bool windows = false)
    {
        Assert.True(Parse(payload, out string path, windows), payload);
        Assert.Equal(expected, path);
    }

    private static void AssertRejected(string payload, bool windows = false)
    {
        Assert.False(Parse(payload, out string path, windows), payload);
        Assert.Equal("", path);
    }

    [Fact]
    public void ParsesPlainPath() => AssertPath("file:///home/u/src", "/home/u/src");

    [Theory]
    [InlineData("file:///home/u/a%20b", "/home/u/a b")]
    [InlineData("file:///caf%C3%A9", "/café")]
    [InlineData("file:///café", "/café")]
    [InlineData("file:///%E4%BD%A0", "/\u4F60")]
    [InlineData("file:///caf%c3%a9", "/café")]
    public void DecodesScriptShapes(string payload, string expected) => AssertPath(payload, expected);

    [Theory]
    [InlineData("file://localhost/x")]
    [InlineData("file://LOCALHOST/x")]
    [InlineData("file://box/x")]
    [InlineData("file://BOX/x")]
    [InlineData("file://127.0.0.1/x")]
    [InlineData("file://[::1]/x")]
    [InlineData("file://box./x")]
    public void AcceptsLocalHosts(string payload) => AssertPath(payload, "/x");

    [Theory]
    [InlineData("file://other/x")]
    [InlineData("file://box.evil.com/x")]
    [InlineData("file://foo.localhost/x")]
    [InlineData("file://box:8080/x")]
    [InlineData("file://user@box/x")]
    [InlineData("file://bo%78/x")]
    public void RejectsRemoteHosts(string payload) => AssertRejected(payload);

    [Theory]
    [InlineData("")]
    [InlineData("file:")]
    [InlineData("file://")]
    [InlineData("file://box")]
    [InlineData("file:/tmp")]
    [InlineData("file:tmp")]
    [InlineData("http://box/x")]
    public void RejectsBadShapes(string payload) => AssertRejected(payload);

    [Fact]
    public void SchemeIsCaseInsensitive() => AssertPath("FILE:///x", "/x");

    [Fact]
    public void RootStaysRoot() => AssertPath("file:///", "/");

    [Theory]
    [InlineData("file:///a%zz")]
    [InlineData("file:///a%4")]
    [InlineData("file:///a%")]
    [InlineData("file:///a%FF")]
    [InlineData("file:///a%C3")]
    [InlineData("file:///a%C0%AF")]
    public void RejectsBadPercentEncoding(string payload) => AssertRejected(payload);

    [Theory]
    [InlineData("file:///a%00")]
    [InlineData("file:///a%0A")]
    [InlineData("file:///a%0D")]
    [InlineData("file:///a%09")]
    [InlineData("file:///a%1B")]
    [InlineData("file:///a%7F")]
    [InlineData("file:///a%C2%85")]
    [InlineData("file:///a%C2%9B")]
    public void RejectsDecodedControls(string payload) => AssertRejected(payload);

    [Theory]
    [InlineData('\0')]
    [InlineData('\n')]
    [InlineData('\t')]
    [InlineData('\u001b')]
    [InlineData('\u007f')]
    [InlineData('\u0085')]
    [InlineData('\uFFFD')]
    public void RejectsRawControls(char c) => AssertRejected("file:///a" + c + "b");

    [Fact]
    public void RejectsLoneSurrogate() => AssertRejected("file:///a\ud800b");

    [Fact]
    public void QueryAndFragmentRejectedUnlessEncoded()
    {
        AssertRejected("file:///a?b");
        AssertRejected("file:///a#b");
        AssertPath("file:///a%3Fb", "/a?b");
        AssertPath("file:///a%23b", "/a#b");
    }

    [Theory]
    [InlineData("file:///a/../b", "/b")]
    [InlineData("file:///a/./b", "/a/b")]
    [InlineData("file:///a/%2e%2e/b", "/b")]
    [InlineData("file:///../..", "/")]
    [InlineData("file:///a//b", "/a/b")]
    [InlineData("file:///a/b/", "/a/b")]
    public void NormalizesDotSegments(string payload, string expected) => AssertPath(payload, expected);

    [Fact]
    public void BackslashIsKeptOnUnix()
    {
        AssertPath("file:///a%5Cb", "/a\\b");
        AssertPath("file:///a\\b", "/a\\b");
    }

    [Fact]
    public void DriveLikePathOnUnixStaysAbsoluteUnixPath() => AssertPath("file:///C:/Users", "/C:/Users");

    [Fact]
    public void LengthLimits()
    {
        string ascii4096 = "/" + new string('a', Osc7WorkingDirectory.MaxPathLength - 1);
        AssertPath("file://" + ascii4096, ascii4096);
        AssertRejected("file://" + ascii4096 + "a");

        var sw = Stopwatch.StartNew();
        AssertRejected("file:///" + new string('a', 100_000));
        Assert.True(sw.ElapsedMilliseconds < 500);

        var ok = new StringBuilder("file:///");
        for (int i = 0; i < Osc7WorkingDirectory.MaxPathLength - 1; i++) ok.Append("%E4%BD%A0");
        Assert.True(Parse(ok.ToString(), out string path));
        Assert.Equal(Osc7WorkingDirectory.MaxPathLength, path.Length);

        var tooLong = new StringBuilder("file:///");
        for (int i = 0; i < Osc7WorkingDirectory.MaxPathLength; i++) tooLong.Append("%E4%BD%A0");
        AssertRejected(tooLong.ToString());
    }

    [Theory]
    [InlineData("file:///C:/Users/me", @"C:\Users\me")]
    [InlineData("file:///c:/x", @"c:\x")]
    [InlineData("file://localhost/C:/x", @"C:\x")]
    [InlineData("file:///C:", @"C:\")]
    [InlineData("file:///C:/", @"C:\")]
    [InlineData("file:///C%3A/x", @"C:\x")]
    [InlineData("file:///C:/a%20b", @"C:\a b")]
    [InlineData("file:///C:/Program%20Files/x", @"C:\Program Files\x")]
    [InlineData("file:///C:/a/../..", @"C:\")]
    public void WindowsAccepts(string payload, string expected) => AssertPath(payload, expected, windows: true);

    [Theory]
    [InlineData("file:///Users/me")]
    [InlineData("file:///C:foo")]
    [InlineData("file:///1:/x")]
    [InlineData("file://server/share/d")]
    [InlineData("file://server/share/x")]
    [InlineData("file:////server/share")]
    [InlineData("file:///C:/a%5Cb")]
    [InlineData("file:///C:/a:b")]
    [InlineData("file:///C:/a%3Fb")]
    [InlineData("file:///C:/a%22b")]
    [InlineData("file:///C:/a\\b")]
    public void WindowsRejects(string payload) => AssertRejected(payload, windows: true);

    [Fact]
    public void FuzzNeverThrowsAndResultsAreSafe()
    {
        const string alphabet = "file:/%0123456789ABCDEFabcdef.#?\\ \0\u001b\u00e9\ud800";
        var rng = new Random(12345);
        var chars = new char[64];
        for (int n = 0; n < 20_000; n++)
        {
            int len = rng.Next(1, chars.Length);
            for (int i = 0; i < len; i++) chars[i] = alphabet[rng.Next(alphabet.Length)];
            // Bias toward a plausible prefix so deeper stages are exercised.
            string payload = (n % 2 == 0 ? "file:///" : "") + new string(chars, 0, len);
            foreach (bool windows in new[] { false, true })
            {
                if (!Osc7WorkingDirectory.TryParse(payload, windows, Hosts, out string path))
                {
                    Assert.Equal("", path);
                    continue;
                }
                Assert.InRange(path.Length, 1, Osc7WorkingDirectory.MaxPathLength);
                foreach (char c in path)
                    Assert.False(c < 0x20 || c == 0x7F || (c >= 0x80 && c <= 0x9F) || c == '\uFFFD');
                if (windows)
                {
                    Assert.True(char.IsAsciiLetter(path[0]) && path[1] == ':' && (path.Length == 3 || path[2] == '\\'));
                    Assert.Equal('\\', path[2]);
                }
                else
                {
                    Assert.Equal('/', path[0]);
                }
            }
        }
    }
}
