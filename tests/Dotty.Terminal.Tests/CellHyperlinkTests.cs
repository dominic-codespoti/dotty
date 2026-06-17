using Dotty.Terminal.Adapter;
using Dotty.Terminal.Adapter.Buffer;
using Xunit;

namespace Dotty.Terminal.Tests;

/// <summary>
/// Comprehensive tests for Cell hyperlink functionality.
/// HyperlinkId is now stored in ColdCell.
/// </summary>
public class CellHyperlinkTests
{
    #region HyperlinkId Storage

    [Fact]
    public void Cell_DefaultHyperlinkId_IsZero()
    {
        var cold = new ColdCell();
        Assert.Equal((ushort)0, cold.HyperlinkId);
    }

    [Fact]
    public void Cell_SetHyperlinkId_StoresCorrectly()
    {
        var cold = new ColdCell();
        cold.HyperlinkId = 5;
        Assert.Equal((ushort)5, cold.HyperlinkId);
    }

    [Fact]
    public void Cell_HyperlinkIdZero_MeansNoHyperlink()
    {
        var cold = new ColdCell();
        cold.HyperlinkId = 0;
        Assert.Equal((ushort)0, cold.HyperlinkId);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    [InlineData(1000)]
    [InlineData(65535)]
    public void Cell_SetVariousHyperlinkIds_StoresCorrectly(ushort id)
    {
        var cold = new ColdCell();
        cold.HyperlinkId = id;
        Assert.Equal(id, cold.HyperlinkId);
    }

    #endregion

    #region Cell Reset

    [Fact]
    public void Cell_Reset_ClearsHyperlinkId()
    {
        var cold = new ColdCell();
        cold.HyperlinkId = 10;
        var hot = new CellHot();
        hot.Rune = 'A';
        hot.Reset();
        cold.Reset();
        Assert.Equal((ushort)0, cold.HyperlinkId);
        Assert.Equal((uint)0, hot.Rune);
    }

    [Fact]
    public void Cell_Reset_AllFieldsCleared()
    {
        var hot = new CellHot
        {
            Rune = 'A',
            StyleId = 42,
            Width = 2,
            IsContinuation = true,
        };
        var cold = new ColdCell();
        cold.HyperlinkId = 42;

        hot.Reset();
        cold.Reset();

        Assert.Equal((uint)0, hot.Rune);
        Assert.Equal((ushort)0, hot.StyleId);
        Assert.Equal((byte)1, hot.Width);
        Assert.False(hot.IsContinuation);
        Assert.Equal((ushort)0, cold.HyperlinkId);
    }

    #endregion

    #region Cell IsEmpty

    [Fact]
    public void Cell_IsEmpty_WithHyperlink_ReturnsFalse()
    {
        var hot = new CellHot();
        var cold = new ColdCell();
        cold.HyperlinkId = 5;
        Assert.True(hot.IsEmpty);
    }

    [Fact]
    public void Cell_IsEmpty_WithContentAndHyperlink_ReturnsFalse()
    {
        var hot = new CellHot();
        hot.SetAscii('A');
        var cold = new ColdCell();
        cold.HyperlinkId = 5;
        Assert.False(hot.IsEmpty);
    }

    #endregion

    #region Cell StyleId Independence

    [Fact]
    public void Cell_HyperlinkIdIndependentOfStyleId()
    {
        var hot = new CellHot();
        hot.StyleId = 7;
        var cold = new ColdCell();
        cold.HyperlinkId = 10;
        Assert.Equal((ushort)10, cold.HyperlinkId);
        Assert.Equal((ushort)7, hot.StyleId);
    }

    [Fact]
    public void Cell_SetHyperlinkId_DoesNotAffectStyleId()
    {
        var hot = new CellHot();
        hot.StyleId = 7;
        var originalStyleId = hot.StyleId;
        var cold = new ColdCell();
        cold.HyperlinkId = 42;
        Assert.Equal(originalStyleId, hot.StyleId);
        Assert.Equal((ushort)42, cold.HyperlinkId);
    }

    #endregion

    #region Cell with Hyperlink and Other Attributes

    [Fact]
    public void Cell_HyperlinkWithBold_BothPreserved()
    {
        var buffer = new TerminalBuffer(rows: 10, columns: 80);
        var styleSet = buffer.StyleSet;
        var attrs = new CellAttributes { Bold = true, HyperlinkId = 5 };
        ushort styleId = styleSet.GetOrCreateId(in attrs);
        var hot = new CellHot();
        hot.SetAscii('A');
        hot.StyleId = styleId;
        var cold = new ColdCell();
        cold.HyperlinkId = 5;

        Assert.Equal('A', (char)hot.Rune);
        Assert.Equal((ushort)5, cold.HyperlinkId);

        var resolved = styleSet.GetStyle(hot.StyleId);
        Assert.True(resolved.Bold);
    }

    [Fact]
    public void Cell_HyperlinkWithUnderline_BothPreserved()
    {
        var buffer = new TerminalBuffer(rows: 10, columns: 80);
        var styleSet = buffer.StyleSet;
        var attrs = new CellAttributes { UnderlineStyle = UnderlineStyle.Single, HyperlinkId = 5 };
        ushort styleId = styleSet.GetOrCreateId(in attrs);
        var hot = new CellHot();
        hot.SetAscii('A');
        hot.StyleId = styleId;
        var cold = new ColdCell();
        cold.HyperlinkId = 5;

        Assert.Equal((ushort)5, cold.HyperlinkId);

        var resolved = styleSet.GetStyle(hot.StyleId);
        Assert.True(resolved.Underline);
    }

    [Fact]
    public void Cell_HyperlinkWithColors_AllPreserved()
    {
        var buffer = new TerminalBuffer(rows: 10, columns: 80);
        var styleSet = buffer.StyleSet;
        var attrs = new CellAttributes
        {
            Foreground = new SgrColorArgb(0xFFFF0000),
            Background = new SgrColorArgb(0xFF00FF00),
            HyperlinkId = 5
        };
        ushort styleId = styleSet.GetOrCreateId(in attrs);
        var hot = new CellHot();
        hot.SetAscii('A');
        hot.StyleId = styleId;
        var cold = new ColdCell();
        cold.HyperlinkId = 5;

        var resolved = styleSet.GetStyle(hot.StyleId);
        Assert.Equal((uint)0xFFFF0000, resolved.Foreground.Argb);
        Assert.Equal((uint)0xFF00FF00, resolved.Background.Argb);
        Assert.Equal((ushort)5, cold.HyperlinkId);
    }

    [Fact]
    public void Cell_FullHyperlinkStyle_AllAttributesPreserved()
    {
        var buffer = new TerminalBuffer(rows: 10, columns: 80);
        var styleSet = buffer.StyleSet;
        var attrs = new CellAttributes
        {
            Bold = true,
            UnderlineStyle = UnderlineStyle.Single,
            Foreground = new SgrColorArgb(0xFF0000FF),
            HyperlinkId = 10
        };
        ushort styleId = styleSet.GetOrCreateId(in attrs);

        var hot = new CellHot();
        hot.SetAscii('L');
        hot.StyleId = styleId;
        var cold = new ColdCell();
        cold.HyperlinkId = 10;

        Assert.Equal('L', (char)hot.Rune);
        Assert.Equal((ushort)10, cold.HyperlinkId);

        var resolved = styleSet.GetStyle(hot.StyleId);
        Assert.True(resolved.Bold);
        Assert.True(resolved.Underline);
        Assert.Equal((uint)0xFF0000FF, resolved.Foreground.Argb);
    }

    #endregion

    #region Wide Characters with Hyperlinks

    [Fact]
    public void Cell_WideCharHyperlink_BaseCellHasLink()
    {
        var hot = new CellHot();
        hot.Rune = 0x4e2d;
        hot.Width = 2;
        var cold = new ColdCell();
        cold.HyperlinkId = 5;

        Assert.Equal((ushort)5, cold.HyperlinkId);
        Assert.Equal((byte)2, hot.Width);
    }

    [Fact]
    public void Cell_ContinuationCell_CanHaveHyperlink()
    {
        var hot = new CellHot();
        hot.IsContinuation = true;
        var cold = new ColdCell();
        cold.HyperlinkId = 5;

        Assert.True(hot.IsContinuation);
        Assert.Equal((ushort)5, cold.HyperlinkId);
    }

    #endregion

    #region Cell Struct Behavior

    [Fact]
    public void Cell_IsValueType_PassedByValue()
    {
        var cold1 = new ColdCell();
        cold1.HyperlinkId = 5;
        var hot1 = new CellHot();
        hot1.SetAscii('A');

        var cold2 = cold1;
        cold2.HyperlinkId = 10;

        Assert.Equal((ushort)5, cold1.HyperlinkId);
        Assert.Equal((ushort)10, cold2.HyperlinkId);
    }

    [Fact]
    public void Cell_ReferenceSemantics_ModifiesOriginal()
    {
        var hot = new CellHot();
        hot.SetAscii('A');
        var cold = new ColdCell();
        cold.HyperlinkId = 5;

        ref var coldRef = ref cold;
        coldRef.HyperlinkId = 10;

        Assert.Equal((ushort)10, cold.HyperlinkId);
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void Cell_MaxHyperlinkId_Valid()
    {
        var cold = new ColdCell();
        cold.HyperlinkId = ushort.MaxValue;
        Assert.Equal(ushort.MaxValue, cold.HyperlinkId);
    }

    [Fact]
    public void Cell_HyperlinkIdOverflow_Wraps()
    {
        ushort max = ushort.MaxValue;
        Assert.Equal(65535, max);
    }

    [Fact]
    public void Cell_SetGrapheme_DoesNotAffectHyperlinkId()
    {
        var cold = new ColdCell();
        cold.HyperlinkId = 5;
        var graphemeIdx = GraphemeHelper.StoreGrapheme("Hello");
        var hot = new CellHot();
        hot.Rune = 0x4f60;
        Assert.Equal((ushort)5, cold.HyperlinkId);
        Assert.Equal("Hello", GraphemeHelper.Resolve(hot.Rune, graphemeIdx));
    }

    [Fact]
    public void Cell_SetAscii_DoesNotAffectHyperlinkId()
    {
        var hot = new CellHot();
        var cold = new ColdCell();
        cold.HyperlinkId = 5;
        hot.SetAscii('Z');
        Assert.Equal((ushort)5, cold.HyperlinkId);
        Assert.Equal('Z', (char)hot.Rune);
    }

    #endregion

    #region Integration with CellAttributes

    [Fact]
    public void CellAttributes_DefaultHyperlinkId_IsZero()
    {
        var attrs = CellAttributes.Default;
        Assert.Equal((ushort)0, attrs.HyperlinkId);
    }

    [Fact]
    public void CellAttributes_SetHyperlinkId_StoresCorrectly()
    {
        var attrs = new CellAttributes();
        attrs.HyperlinkId = 5;
        Assert.Equal((ushort)5, attrs.HyperlinkId);
    }

    [Fact]
    public void CellAttributes_HyperlinkIdIncludedInIsDefaultColors()
    {
        var attrs = new CellAttributes();
        Assert.True(attrs.IsDefaultColors);

        attrs.HyperlinkId = 5;
        Assert.True(attrs.IsDefaultColors);
    }

    #endregion

    #region StyleSet Integration

    [Fact]
    public void StyleSet_DefaultStyle_ReturnsIdZero()
    {
        var styleSet = new StyleSet();
        ushort id = styleSet.GetOrCreateId(CellAttributes.Default);
        Assert.Equal((ushort)0, id);
    }

    [Fact]
    public void StyleSet_SameAttributes_ReturnsSameId()
    {
        var styleSet = new StyleSet();
        var attrs = new CellAttributes { Bold = true, Foreground = new SgrColorArgb(0xFFFF0000) };

        ushort id1 = styleSet.GetOrCreateId(in attrs);
        ushort id2 = styleSet.GetOrCreateId(in attrs);

        Assert.Equal(id1, id2);
    }

    [Fact]
    public void StyleSet_DifferentAttributes_ReturnsDifferentId()
    {
        var styleSet = new StyleSet();
        var attrs1 = new CellAttributes { Bold = true };
        var attrs2 = new CellAttributes { Italic = true };

        ushort id1 = styleSet.GetOrCreateId(in attrs1);
        ushort id2 = styleSet.GetOrCreateId(in attrs2);

        Assert.NotEqual(id1, id2);
    }

    [Fact]
    public void StyleSet_GetStyle_ReturnsOriginalAttributes()
    {
        var styleSet = new StyleSet();
        var attrs = new CellAttributes
        {
            Bold = true,
            Italic = true,
            UnderlineStyle = UnderlineStyle.Single,
            Foreground = new SgrColorArgb(0xFFFF0000),
            Background = new SgrColorArgb(0xFF00FF00)
        };

        ushort id = styleSet.GetOrCreateId(in attrs);
        var resolved = styleSet.GetStyle(id);

        Assert.Equal(attrs.Foreground.Argb, resolved.Foreground.Argb);
        Assert.Equal(attrs.Background.Argb, resolved.Background.Argb);
        Assert.True(resolved.Bold);
        Assert.True(resolved.Italic);
        Assert.True(resolved.Underline);
    }

    [Fact]
    public void StyleSet_GetStyle_InvalidId_ReturnsDefault()
    {
        var styleSet = new StyleSet();
        var resolved = styleSet.GetStyle(999);
        Assert.Equal((uint)0, resolved.Foreground.Argb);
        Assert.Equal((uint)0, resolved.Background.Argb);
        Assert.False(resolved.Bold);
    }

    [Fact]
    public void StyleSet_DefaultId_ReturnsDefaultStyle()
    {
        var styleSet = new StyleSet();
        var resolved = styleSet.GetStyle(0);
        Assert.Equal(CellAttributes.Default.Foreground.Argb, resolved.Foreground.Argb);
        Assert.Equal(CellAttributes.Default.Background.Argb, resolved.Background.Argb);
    }

    #endregion
}
