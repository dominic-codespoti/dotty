using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Dotty.Runtime.Config;
using Dotty.Silk;
using Dotty.Silk.Rendering;
using Xunit;

namespace Dotty.App.Tests;

public sealed class WindowStartOptionsTests
{
    [Fact]
    public void Resolve_Defaults()
    {
        var options = WindowStartOptions.Resolve(new WindowUserConfig(), themeOpacity: 100);

        Assert.Equal(new WindowStartOptions("Dotty", 80, 24, WindowStartState.Normal, Floating: false, TransparentFramebuffer: false), options);
    }

    [Theory]
    [InlineData(0, 80)]
    [InlineData(-5, 80)]
    [InlineData(1, 1)]
    [InlineData(132, 132)]
    [InlineData(WindowStartOptions.MaxGridCells, WindowStartOptions.MaxGridCells)]
    [InlineData(WindowStartOptions.MaxGridCells + 1, WindowStartOptions.MaxGridCells)]
    [InlineData(int.MaxValue, WindowStartOptions.MaxGridCells)]
    public void GridDimension_FallsBackAndClamps(int configured, int expected) =>
        Assert.Equal(expected, WindowStartOptions.GridDimension(configured, 80));

    [Theory]
    [InlineData(false, false, WindowStartState.Normal)]
    [InlineData(true, false, WindowStartState.Maximized)]
    [InlineData(false, true, WindowStartState.Fullscreen)]
    [InlineData(true, true, WindowStartState.Fullscreen)]
    public void Resolve_FullscreenWinsOverMaximized(bool maximized, bool fullscreen, WindowStartState expected)
    {
        var config = new WindowUserConfig { StartMaximized = maximized, StartFullscreen = fullscreen };

        Assert.Equal(expected, WindowStartOptions.Resolve(config, 100).StartState);
    }

    [Theory]
    [InlineData("", "Dotty")]
    [InlineData("   ", "Dotty")]
    [InlineData("work", "work")]
    public void Resolve_BlankTitleUsesDefault(string title, string expected) =>
        Assert.Equal(expected, WindowStartOptions.Resolve(new WindowUserConfig { Title = title }, 100).Title);

    [Theory]
    [InlineData(1.0, 100, false)]
    [InlineData(0.99, 100, true)]
    [InlineData(1.0, 99, true)]
    [InlineData(double.NaN, 100, false)]
    public void Resolve_AlphaFramebufferOnlyWhenTranslucent(double opacity, byte theme, bool expected) =>
        Assert.Equal(expected, WindowStartOptions.Resolve(new WindowUserConfig { Opacity = opacity }, theme).TransparentFramebuffer);

    [Fact]
    public void Resolve_CarriesGridAndFloating()
    {
        var options = WindowStartOptions.Resolve(new WindowUserConfig { Columns = 120, Rows = 40, Floating = true }, 100);

        Assert.Equal((120, 40, true), (options.Columns, options.Rows, options.Floating));
    }

    [Theory]
    [InlineData(DisplayPlatform.X11, true)]
    [InlineData(DisplayPlatform.Win32, true)]
    [InlineData(DisplayPlatform.Wayland, false)]
    [InlineData(DisplayPlatform.Cocoa, false)]
    [InlineData(DisplayPlatform.Null, false)]
    [InlineData(DisplayPlatform.Unknown, false)]
    public void SupportsWindowIcon_OnlyWhereGlfwSetsIcons(DisplayPlatform platform, bool expected) =>
        Assert.Equal(expected, WindowStartOptions.SupportsWindowIcon(platform));

    [Fact]
    public void EmbeddedIcons_DecodeAtTheirNominalSize()
    {
        var assembly = typeof(WindowStartOptions).Assembly;
        foreach (int size in WindowStartOptions.IconSizes.ToArray())
        {
            using Stream? stream = assembly.GetManifestResourceStream(WindowStartOptions.IconResourceName(size));
            Assert.NotNull(stream);
            var (width, height) = PngSize(ReadAll(stream!));
            Assert.Equal((size, size), (width, height));
        }
    }

    private static string RepoRoot()
    {
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "Dotty.slnx")))
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("repository root not found");
        return root;
    }

    private static byte[] ReadAll(Stream stream)
    {
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static (int Width, int Height) PngSize(ReadOnlySpan<byte> png)
    {
        Assert.True(png.Length > 24 && png[..8].SequenceEqual(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10 }), "PNG signature");
        Assert.True(png.Slice(12, 4).SequenceEqual("IHDR"u8), "IHDR first");
        return (System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png[16..]),
            System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png[20..]));
    }

    [Theory]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(48)]
    [InlineData(64)]
    [InlineData(128)]
    [InlineData(256)]
    [InlineData(512)]
    public void IconSet_PngHasNominalSize(int size) =>
        Assert.Equal((size, size), PngSize(File.ReadAllBytes(Path.Combine(RepoRoot(), "assets", "icons", $"dotty-{size}.png"))));

    [Fact]
    public void IconSet_IcoListsPngEntries16To256()
    {
        byte[] ico = File.ReadAllBytes(Path.Combine(RepoRoot(), "assets", "icons", "dotty.ico"));
        Assert.Equal(new byte[] { 0, 0, 1, 0 }, ico[..4]);
        int count = BitConverter.ToUInt16(ico, 4);
        var sizes = new int[count];
        for (int i = 0; i < count; i++)
        {
            int entry = 6 + 16 * i;
            int length = BitConverter.ToInt32(ico, entry + 8);
            int offset = BitConverter.ToInt32(ico, entry + 12);
            Assert.InRange(offset + length, 0, ico.Length);
            var (width, height) = PngSize(ico.AsSpan(offset, length));
            Assert.Equal(width, height);
            Assert.Equal(ico[entry] == 0 ? 256 : ico[entry], width);
            sizes[i] = width;
        }
        Assert.Equal(new[] { 16, 32, 48, 64, 128, 256 }, sizes);
    }

    [Fact]
    public void IconSet_IcnsChunksCoverTheirLength()
    {
        byte[] icns = File.ReadAllBytes(Path.Combine(RepoRoot(), "assets", "icons", "dotty.icns"));
        Assert.Equal("icns"u8.ToArray(), icns[..4]);
        Assert.Equal(icns.Length, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(icns.AsSpan(4)));
        var types = new System.Collections.Generic.List<string>();
        for (int offset = 8; offset < icns.Length;)
        {
            string type = System.Text.Encoding.ASCII.GetString(icns, offset, 4);
            int length = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(icns.AsSpan(offset + 4));
            PngSize(icns.AsSpan(offset + 8, length - 8));
            types.Add(type);
            offset += length;
        }
        Assert.Equal(new[] { "icp4", "icp5", "icp6", "ic07", "ic08", "ic09" }, types);
    }

    [Fact]
    public void DesktopEntry_MatchesAppIdentity()
    {
        string path = Path.Combine(RepoRoot(), "packaging", "linux", WindowStartOptions.AppId + ".desktop");
        var entries = File.ReadAllLines(path)
            .Where(line => line.Contains('=') && !line.StartsWith('#'))
            .ToDictionary(line => line[..line.IndexOf('=')], line => line[(line.IndexOf('=') + 1)..]);

        Assert.Equal("[Desktop Entry]", File.ReadLines(path).First());
        Assert.Equal("Application", entries["Type"]);
        Assert.Equal("Dotty", entries["Name"]);
        Assert.Equal("dotty", entries["Exec"]);
        Assert.Equal(WindowStartOptions.AppId, entries["Icon"]);
        Assert.Equal(WindowStartOptions.AppId, entries["StartupWMClass"]);
        Assert.Contains("TerminalEmulator;", entries["Categories"]);
        Assert.EndsWith(";", entries["Categories"]);
    }

    [Fact]
    public void InfoPlist_DeclaresBundleIdentityAndHiDpi()
    {
        string root = RepoRoot();
        using var reader = System.Xml.XmlReader.Create(Path.Combine(root, "packaging", "macos", "Info.plist"),
            new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Ignore });
        var dict = XDocument.Load(reader).Root!.Element("dict")!;
        var keys = dict.Elements().ToList();
        string? Value(string key) => keys.SkipWhile(e => e.Name != "key" || e.Value != key).Skip(1).FirstOrDefault()?.Value;
        XElement? Element(string key) => keys.SkipWhile(e => e.Name != "key" || e.Value != key).Skip(1).FirstOrDefault();

        Assert.Equal(WindowStartOptions.AppId, Value("CFBundleIdentifier"));
        Assert.Equal("dotty", Value("CFBundleExecutable"));
        Assert.Equal("dotty.icns", Value("CFBundleIconFile"));
        Assert.Equal("true", Element("NSHighResolutionCapable")?.Name.LocalName);
        string version = Regex.Match(File.ReadAllText(Path.Combine(root, "Directory.Build.props")), "<Version>([^<]+)</Version>").Groups[1].Value;
        Assert.Equal(version, Value("CFBundleShortVersionString"));
    }

    [Fact]
    public void WindowsManifest_IsPerMonitorV2DpiAware()
    {
        var manifest = XDocument.Load(Path.Combine(RepoRoot(), "packaging", "windows", "app.manifest"));
        var awareness = manifest.Descendants().Single(e => e.Name.LocalName == "dpiAwareness");

        Assert.Equal("PerMonitorV2", awareness.Value);
        Assert.Equal("true/pm", manifest.Descendants().Single(e => e.Name.LocalName == "dpiAware").Value);
    }
}
