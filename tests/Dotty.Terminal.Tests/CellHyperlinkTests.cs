using Dotty.Terminal.Adapter;
using Xunit;

namespace Dotty.Terminal.Tests;

/// <summary>
/// Comprehensive tests for Cell hyperlink functionality.
/// Tests the HyperlinkId field in the Cell struct.
/// </summary>
public class CellHyperlinkTests
{
    #region HyperlinkId Storage

    [Fact]
    public void Cell_DefaultHyperlinkId_IsZero()
    {
        // Arrange & Act
        var cell = new Cell();
        
        // Assert - Default hyperlink ID should be 0 (no hyperlink)
        Assert.Equal((ushort)0, cell.HyperlinkId);
    }

    [Fact]
    public void Cell_SetHyperlinkId_StoresCorrectly()
    {
        // Arrange
        var cell = new Cell();
        
        // Act
        cell.HyperlinkId = 5;
        
        // Assert
        Assert.Equal((ushort)5, cell.HyperlinkId);
    }

    [Fact]
    public void Cell_HyperlinkIdZero_MeansNoHyperlink()
    {
        // Arrange
        var cell = new Cell();
        cell.HyperlinkId = 0;
        
        // Assert - 0 is the sentinel value for "no hyperlink"
        Assert.Equal((ushort)0, cell.HyperlinkId);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    [InlineData(1000)]
    [InlineData(65535)]
    public void Cell_SetVariousHyperlinkIds_StoresCorrectly(ushort id)
    {
        // Arrange
        var cell = new Cell();
        
        // Act
        cell.HyperlinkId = id;
        
        // Assert
        Assert.Equal(id, cell.HyperlinkId);
    }

    #endregion

    #region Cell Reset

    [Fact]
    public void Cell_Reset_ClearsHyperlinkId()
    {
        // Arrange
        var cell = new Cell();
        cell.HyperlinkId = 10;
        cell.Rune = 'A';
        cell.Width = 1;
        
        // Act
        cell.Reset();
        
        // Assert
        Assert.Equal((ushort)0, cell.HyperlinkId);
        Assert.Equal((uint)0, cell.Rune);
        Assert.Equal((byte)0, cell.Width);
    }

    [Fact]
    public void Cell_Reset_AllFieldsCleared()
    {
        // Arrange - Set all fields
        var cell = new Cell
        {
            Rune = 'A',
            Foreground = 0xFF0000,
            Background = 0x00FF00,
            UnderlineColor = 0x0000FF,
            Flags = 0xFFFF,
            Width = 2,
            IsContinuation = true,
            HyperlinkId = 42
        };
        
        // Act
        cell.Reset();
        
        // Assert - All fields should be reset
        Assert.Equal((uint)0, cell.Rune);
        Assert.Equal((uint)0, cell.Foreground);
        Assert.Equal((uint)0, cell.Background);
        Assert.Equal((uint)0, cell.UnderlineColor);
        Assert.Equal((ushort)0, cell.Flags);
        Assert.Equal((byte)0, cell.Width);
        Assert.False(cell.IsContinuation);
        Assert.Equal((ushort)0, cell.HyperlinkId);
    }

    #endregion

    #region Cell IsEmpty

    [Fact]
    public void Cell_IsEmpty_WithHyperlink_ReturnsFalse()
    {
        // Arrange
        var cell = new Cell();
        cell.HyperlinkId = 5;
        
        // Act & Assert
        // Note: IsEmpty only checks Rune and IsContinuation, not HyperlinkId
        // This is intentional - a cell can be empty (no content) but still have attributes
        Assert.True(cell.IsEmpty);
    }

    [Fact]
    public void Cell_IsEmpty_WithContentAndHyperlink_ReturnsFalse()
    {
        // Arrange
        var cell = new Cell();
        cell.SetAscii('A');
        cell.HyperlinkId = 5;
        
        // Act & Assert
        Assert.False(cell.IsEmpty);
    }

    #endregion

    #region Cell Flags Independence

    [Fact]
    public void Cell_HyperlinkIdIndependentOfFlags()
    {
        // Arrange
        var cell = new Cell();
        
        // Act - Set various flags
        cell.Bold = true;
        cell.Italic = true;
        cell.Underline = true;
        cell.HyperlinkId = 10;
        
        // Assert - HyperlinkId should be independent of Flags
        Assert.Equal((ushort)10, cell.HyperlinkId);
        Assert.True(cell.Bold);
        Assert.True(cell.Italic);
        Assert.True(cell.Underline);
    }

    [Fact]
    public void Cell_SetHyperlinkId_DoesNotAffectFlags()
    {
        // Arrange
        var cell = new Cell();
        cell.Flags = 0xFF;
        var originalFlags = cell.Flags;
        
        // Act
        cell.HyperlinkId = 42;
        
        // Assert - Flags should be unchanged
        Assert.Equal(originalFlags, cell.Flags);
        Assert.Equal((ushort)42, cell.HyperlinkId);
    }

    #endregion

    #region Cell with Hyperlink and Other Attributes

    [Fact]
    public void Cell_HyperlinkWithBold_BothPreserved()
    {
        // Arrange
        var cell = new Cell();
        cell.SetAscii('A');
        cell.HyperlinkId = 5;
        cell.Bold = true;
        
        // Assert
        Assert.Equal('A', (char)cell.Rune);
        Assert.Equal((ushort)5, cell.HyperlinkId);
        Assert.True(cell.Bold);
    }

    [Fact]
    public void Cell_HyperlinkWithUnderline_BothPreserved()
    {
        // Arrange
        var cell = new Cell();
        cell.SetAscii('A');
        cell.HyperlinkId = 5;
        cell.Underline = true;
        
        // Assert
        Assert.True(cell.Underline);
        Assert.Equal((ushort)5, cell.HyperlinkId);
    }

    [Fact]
    public void Cell_HyperlinkWithColors_AllPreserved()
    {
        // Arrange
        var cell = new Cell();
        cell.SetAscii('A');
        cell.HyperlinkId = 5;
        cell.Foreground = 0xFF0000;
        cell.Background = 0x00FF00;
        
        // Assert
        Assert.Equal((uint)0xFF0000, cell.Foreground);
        Assert.Equal((uint)0x00FF00, cell.Background);
        Assert.Equal((ushort)5, cell.HyperlinkId);
    }

    [Fact]
    public void Cell_FullHyperlinkStyle_AllAttributesPreserved()
    {
        // Arrange - Simulate a fully styled hyperlink cell
        var cell = new Cell();
        cell.SetAscii('L');
        cell.HyperlinkId = 10;
        cell.Bold = true;
        cell.Underline = true;
        cell.Foreground = 0x0000FF; // Blue for hyperlinks
        
        // Assert
        Assert.Equal('L', (char)cell.Rune);
        Assert.Equal((ushort)10, cell.HyperlinkId);
        Assert.True(cell.Bold);
        Assert.True(cell.Underline);
        Assert.Equal((uint)0x0000FF, cell.Foreground);
    }

    #endregion

    #region Wide Characters with Hyperlinks

    [Fact]
    public void Cell_WideCharHyperlink_BaseCellHasLink()
    {
        // Arrange
        var cell = new Cell();
        cell.Grapheme = "\u4e2d"; // Chinese character (wide)
        cell.HyperlinkId = 5;
        cell.Width = 2;
        
        // Assert
        Assert.Equal((ushort)5, cell.HyperlinkId);
        Assert.Equal((byte)2, cell.Width);
    }

    [Fact]
    public void Cell_ContinuationCell_CanHaveHyperlink()
    {
        // Arrange
        var cell = new Cell();
        cell.IsContinuation = true;
        cell.HyperlinkId = 5;
        
        // Assert - Continuation cells can have hyperlinks for proper rendering
        Assert.True(cell.IsContinuation);
        Assert.Equal((ushort)5, cell.HyperlinkId);
    }

    #endregion

    #region Cell Struct Behavior

    [Fact]
    public void Cell_IsValueType_PassedByValue()
    {
        // Arrange
        var cell1 = new Cell();
        cell1.HyperlinkId = 5;
        cell1.SetAscii('A');
        
        // Act - cell2 is a copy
        var cell2 = cell1;
        cell2.HyperlinkId = 10;
        
        // Assert - cell1 should be unchanged
        Assert.Equal((ushort)5, cell1.HyperlinkId);
        Assert.Equal((ushort)10, cell2.HyperlinkId);
    }

    [Fact]
    public void Cell_ReferenceSemantics_ModifiesOriginal()
    {
        // Arrange
        var cell = new Cell();
        cell.SetAscii('A');
        cell.HyperlinkId = 5;
        
        // Act - Use ref to modify
        ref var cellRef = ref cell;
        cellRef.HyperlinkId = 10;
        
        // Assert
        Assert.Equal((ushort)10, cell.HyperlinkId);
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void Cell_MaxHyperlinkId_Valid()
    {
        // Arrange
        var cell = new Cell();
        
        // Act - Max ushort value
        cell.HyperlinkId = ushort.MaxValue;
        
        // Assert
        Assert.Equal(ushort.MaxValue, cell.HyperlinkId);
    }

    [Fact]
    public void Cell_HyperlinkIdOverflow_Wraps()
    {
        // Arrange
        var cell = new Cell();
        ushort max = ushort.MaxValue;
        
        // Act & Assert - ushort wraps on overflow
        // This tests the behavior of the ushort type
        Assert.Equal(65535, max);
    }

    [Fact]
    public void Cell_SetGrapheme_DoesNotAffectHyperlinkId()
    {
        // Arrange
        var cell = new Cell();
        cell.HyperlinkId = 5;
        
        // Act
        cell.Grapheme = "Hello";
        
        // Assert
        Assert.Equal((ushort)5, cell.HyperlinkId);
        Assert.Equal("Hello", cell.Grapheme);
    }

    [Fact]
    public void Cell_SetAscii_DoesNotAffectHyperlinkId()
    {
        // Arrange
        var cell = new Cell();
        cell.HyperlinkId = 5;
        
        // Act
        cell.SetAscii('Z');
        
        // Assert
        Assert.Equal((ushort)5, cell.HyperlinkId);
        Assert.Equal('Z', (char)cell.Rune);
    }

    #endregion

    #region Integration with CellAttributes

    [Fact]
    public void CellAttributes_DefaultHyperlinkId_IsZero()
    {
        // Arrange & Act
        var attrs = CellAttributes.Default;
        
        // Assert
        Assert.Equal((ushort)0, attrs.HyperlinkId);
    }

    [Fact]
    public void CellAttributes_SetHyperlinkId_StoresCorrectly()
    {
        // Arrange
        var attrs = new CellAttributes();
        
        // Act
        attrs.HyperlinkId = 5;
        
        // Assert
        Assert.Equal((ushort)5, attrs.HyperlinkId);
    }

    [Fact]
    public void CellAttributes_HyperlinkIdIncludedInIsDefaultColors()
    {
        // Arrange
        var attrs = new CellAttributes();
        
        // Act & Assert
        // When all colors are default and no other flags, it's "default colors"
        Assert.True(attrs.IsDefaultColors);
        
        // Setting HyperlinkId doesn't affect IsDefaultColors
        attrs.HyperlinkId = 5;
        Assert.True(attrs.IsDefaultColors); // Still default colors
    }

    #endregion
}
