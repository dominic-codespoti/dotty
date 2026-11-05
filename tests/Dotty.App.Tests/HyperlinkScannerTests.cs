using System.Text;
using Dotty.Runtime.Hyperlinks;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Parser;
using Xunit;

namespace Dotty.App.Tests;

public sealed class HyperlinkScannerTests
{
    private static TerminalAdapter Feed(string text)
    {
        var adapter = new TerminalAdapter(rows: 10, columns: 80);
        var parser = new BasicAnsiParser { Handler = adapter };
        parser.Feed(Encoding.UTF8.GetBytes(text));
        return adapter;
    }

    private const string Osc8Close = "\u001b]8;;\u001b\\";

    [Fact]
    public void OscEightLink_ResolvesThroughRenderSnapshot()
    {
        var adapter = Feed("\u001b]8;;https://example.com/x\u001b\\Click" + Osc8Close + " more");

        using var snapshot = adapter.Buffer.CaptureRenderSnapshotVisible();
        var links = HyperlinkScanner.ScanRow(snapshot, 0);

        Assert.Single(links);
        Assert.Equal("https://example.com/x", links[0].Url);
        Assert.Equal(HyperlinkSource.Explicit, links[0].Source);
        Assert.NotNull(links[0].Id);
        Assert.Equal(0, links[0].StartCol);
        Assert.Equal(4, links[0].EndCol);

        var hit = HyperlinkScanner.FindLinkAt(snapshot, 0, 2);
        Assert.NotNull(hit);
        Assert.Equal("https://example.com/x", hit.Value.Url);
        Assert.Null(HyperlinkScanner.FindLinkAt(snapshot, 0, 8));
        Assert.True(HyperlinkScanner.ContainsLinkAt(snapshot, 0, 2));
        Assert.False(HyperlinkScanner.ContainsLinkAt(snapshot, 0, 8));
    }

    [Fact]
    public void OscEightLink_StillResolvesAgainstLiveBuffer()
    {
        var adapter = Feed("\u001b]8;;https://example.com/x\u001b\\Click" + Osc8Close);

        Assert.True(HyperlinkScanner.ContainsLinkAt(adapter.Buffer, 0, 1));
        Assert.Equal(HyperlinkSource.Explicit, HyperlinkScanner.FindLinkAt(adapter.Buffer, 0, 1)!.Value.Source);
    }

    [Fact]
    public void ImplicitLink_IsMarkedDetected()
    {
        var adapter = Feed("see https://github.com/dotty now");

        using var snapshot = adapter.Buffer.CaptureRenderSnapshotVisible();
        var links = HyperlinkScanner.ScanRow(snapshot, 0);

        Assert.Single(links);
        Assert.Equal(HyperlinkSource.Detected, links[0].Source);
        Assert.Null(links[0].Id);
    }

    [Fact]
    public void ExcludingExplicit_HidesOscEightSpansButKeepsDetectedUrls()
    {
        var adapter = Feed(
            "\u001b]8;;https://evil.test/\u001b\\https://shown.test/y" + Osc8Close + " and https://plain.test/z");
        var excluded = new HyperlinkScanOptions(IncludeExplicit: false);

        using var snapshot = adapter.Buffer.CaptureRenderSnapshotVisible();

        var all = HyperlinkScanner.ScanRow(snapshot, 0);
        Assert.Equal(2, all.Count);
        Assert.Equal(HyperlinkSource.Explicit, all[0].Source);
        Assert.Equal("https://evil.test/", all[0].Url);

        var detectedOnly = HyperlinkScanner.ScanRow(snapshot, 0, excluded);
        Assert.Equal(2, detectedOnly.Count);
        Assert.All(detectedOnly, link => Assert.Equal(HyperlinkSource.Detected, link.Source));
        Assert.Equal("https://shown.test/y", detectedOnly[0].Url);

        Assert.True(HyperlinkScanner.ContainsLinkAt(snapshot, 0, 2, excluded));
        Assert.Equal("https://shown.test/y", HyperlinkScanner.FindLinkAt(snapshot, 0, 2, excluded)!.Value.Url);
    }

    [Fact]
    public void ExcludingExplicit_PlainOscEightTextIsNotALink()
    {
        var adapter = Feed("\u001b]8;;https://example.com/x\u001b\\Click" + Osc8Close);
        var excluded = new HyperlinkScanOptions(IncludeExplicit: false);

        using var snapshot = adapter.Buffer.CaptureRenderSnapshotVisible();

        Assert.Empty(HyperlinkScanner.ScanRow(snapshot, 0, excluded));
        Assert.Null(HyperlinkScanner.FindLinkAt(snapshot, 0, 2, excluded));
        Assert.False(HyperlinkScanner.ContainsLinkAt(snapshot, 0, 2, excluded));
    }

    [Fact]
    public void GetSpanText_ReturnsVisibleTextAcrossWideCharacters()
    {
        var adapter = Feed("\u001b]8;;https://e.test/\u001b\\\u4E2D\u6587ab" + Osc8Close + "tail");

        using var snapshot = adapter.Buffer.CaptureRenderSnapshotVisible();
        var span = HyperlinkScanner.ScanRow(snapshot, 0)[0];

        Assert.Equal("\u4E2D\u6587ab", HyperlinkScanner.GetSpanText(snapshot, span));
    }

    [Fact]
    public void GetSpanText_ForPlainAsciiLink()
    {
        var adapter = Feed("\u001b]8;;https://evil.test/\u001b\\https://paypal.com" + Osc8Close);

        using var snapshot = adapter.Buffer.CaptureRenderSnapshotVisible();
        var span = HyperlinkScanner.FindLinkAt(snapshot, 0, 3)!.Value;

        Assert.Equal("https://paypal.com", HyperlinkScanner.GetSpanText(snapshot, span));
        Assert.Equal("https://evil.test/", span.Url);
    }
}
