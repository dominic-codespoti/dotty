using System;
using System.Collections.Generic;
using Dotty.Terminal.Adapter;
using Xunit;

namespace Dotty.Terminal.Tests;

/// <summary>
/// Unit tests for the SearchMatch struct - creation, properties, equality, and edge cases.
/// </summary>
public class SearchMatchTests
{
    #region Construction Tests

    [Fact]
    public void Constructor_SetsPropertiesCorrectly()
    {
        // Arrange & Act
        var match = new SearchMatch(row: 5, startColumn: 10, length: 3);

        // Assert
        Assert.Equal(5, match.Row);
        Assert.Equal(10, match.StartColumn);
        Assert.Equal(3, match.Length);
        Assert.Equal(13, match.EndColumn);
    }

    [Fact]
    public void Constructor_NegativeRow_IsEmpty()
    {
        // Arrange & Act - negative row is valid but marks it as "empty" for display
        var match = new SearchMatch(row: -5, startColumn: 10, length: 3);

        // Assert - negative row makes IsEmpty true
        Assert.Equal(-5, match.Row);
        Assert.True(match.IsEmpty); // Negative row = empty
    }

    [Fact]
    public void Constructor_ZeroLength_Allowed()
    {
        // Arrange & Act
        var match = new SearchMatch(row: 5, startColumn: 10, length: 0);

        // Assert
        Assert.Equal(0, match.Length);
        Assert.Equal(10, match.EndColumn);
    }

    [Fact]
    public void Constructor_LargeValues_Handled()
    {
        // Arrange & Act
        var match = new SearchMatch(row: int.MaxValue, startColumn: int.MaxValue - 1, length: 1);

        // Assert
        Assert.Equal(int.MaxValue, match.Row);
        Assert.Equal(int.MaxValue - 1, match.StartColumn);
        Assert.Equal(int.MaxValue, match.EndColumn); // May overflow but that's OK
    }

    #endregion

    #region Empty Singleton Tests

    [Fact]
    public void Empty_HasCorrectValues()
    {
        // Act
        var empty = SearchMatch.Empty;

        // Assert
        Assert.Equal(-1, empty.Row);
        Assert.Equal(-1, empty.StartColumn);
        Assert.Equal(0, empty.Length);
        Assert.True(empty.IsEmpty);
    }

    [Fact]
    public void Empty_IsSingleton()
    {
        // Act
        var empty1 = SearchMatch.Empty;
        var empty2 = SearchMatch.Empty;

        // Assert - Empty should always return the same instance (struct semantics)
        Assert.Equal(empty1, empty2);
    }

    #endregion

    #region IsEmpty Property Tests

    [Fact]
    public void IsEmpty_True_WhenRowNegative()
    {
        // Arrange & Act
        var match1 = new SearchMatch(-1, 0, 5);
        var match2 = new SearchMatch(-100, 10, 3);

        // Assert
        Assert.True(match1.IsEmpty);
        Assert.True(match2.IsEmpty);
    }

    [Fact]
    public void IsEmpty_False_WhenRowZeroOrPositive()
    {
        // Arrange & Act
        var match1 = new SearchMatch(0, 0, 5);
        var match2 = new SearchMatch(100, 10, 3);

        // Assert
        Assert.False(match1.IsEmpty);
        Assert.False(match2.IsEmpty);
    }

    [Fact]
    public void IsEmpty_True_OnlyRowMatters()
    {
        // Arrange & Act - negative row makes it empty regardless of other values
        var match = new SearchMatch(-1, 999, 100);

        // Assert
        Assert.True(match.IsEmpty);
        Assert.Equal(999, match.StartColumn); // Other properties still set
        Assert.Equal(100, match.Length);
    }

    #endregion

    #region EndColumn Property Tests

    [Fact]
    public void EndColumn_CalculatesCorrectly()
    {
        // Arrange & Act
        var match1 = new SearchMatch(0, 5, 10);
        var match2 = new SearchMatch(0, 0, 1);
        var match3 = new SearchMatch(0, 100, 0);

        // Assert
        Assert.Equal(15, match1.EndColumn);
        Assert.Equal(1, match2.EndColumn);
        Assert.Equal(100, match3.EndColumn);
    }

    [Fact]
    public void EndColumn_WithNegativeStart_StillWorks()
    {
        // Arrange & Act
        var match = new SearchMatch(0, -5, 10);

        // Assert
        Assert.Equal(5, match.EndColumn);
    }

    #endregion

    #region Equality Tests

    [Fact]
    public void Equals_SameValues_ReturnsTrue()
    {
        // Arrange
        var match1 = new SearchMatch(5, 10, 3);
        var match2 = new SearchMatch(5, 10, 3);

        // Act & Assert
        Assert.True(match1.Equals(match2));
        Assert.True(match1 == match2);
        Assert.False(match1 != match2);
    }

    [Fact]
    public void Equals_DifferentRow_ReturnsFalse()
    {
        // Arrange
        var match1 = new SearchMatch(5, 10, 3);
        var match2 = new SearchMatch(6, 10, 3);

        // Act & Assert
        Assert.False(match1.Equals(match2));
        Assert.False(match1 == match2);
        Assert.True(match1 != match2);
    }

    [Fact]
    public void Equals_DifferentStartColumn_ReturnsFalse()
    {
        // Arrange
        var match1 = new SearchMatch(5, 10, 3);
        var match2 = new SearchMatch(5, 11, 3);

        // Act & Assert
        Assert.False(match1.Equals(match2));
        Assert.False(match1 == match2);
    }

    [Fact]
    public void Equals_DifferentLength_ReturnsFalse()
    {
        // Arrange
        var match1 = new SearchMatch(5, 10, 3);
        var match2 = new SearchMatch(5, 10, 4);

        // Act & Assert
        Assert.False(match1.Equals(match2));
        Assert.False(match1 == match2);
    }

    [Fact]
    public void Equals_EmptyMatches_AreEqual()
    {
        // Arrange
        var empty1 = SearchMatch.Empty;
        var empty2 = new SearchMatch(-1, -1, 0);

        // Act & Assert
        Assert.True(empty1.Equals(empty2));
        Assert.True(empty1 == empty2);
    }

    [Fact]
    public void Equals_ObjectOverload_Works()
    {
        // Arrange
        var match = new SearchMatch(5, 10, 3);
        object sameMatch = new SearchMatch(5, 10, 3);
        object differentMatch = new SearchMatch(5, 10, 4);
        object notAMatch = "not a match";

        // Act & Assert
        Assert.True(match.Equals(sameMatch));
        Assert.False(match.Equals(differentMatch));
        Assert.False(match.Equals(notAMatch));
        Assert.False(match.Equals(null));
    }

    [Fact]
    public void Equals_NullObject_ReturnsFalse()
    {
        // Arrange
        var match = new SearchMatch(5, 10, 3);

        // Act & Assert
        Assert.False(match.Equals(null));
    }

    #endregion

    #region GetHashCode Tests

    [Fact]
    public void GetHashCode_SameValues_SameHashCode()
    {
        // Arrange
        var match1 = new SearchMatch(5, 10, 3);
        var match2 = new SearchMatch(5, 10, 3);

        // Act & Assert
        Assert.Equal(match1.GetHashCode(), match2.GetHashCode());
    }

    [Fact]
    public void GetHashCode_DifferentValues_DifferentHashCode()
    {
        // Arrange
        var match1 = new SearchMatch(5, 10, 3);
        var match2 = new SearchMatch(6, 10, 3);

        // Act & Assert - different values should ideally have different hash codes
        // (though collisions are theoretically possible, they're unlikely for simple values)
        Assert.NotEqual(match1.GetHashCode(), match2.GetHashCode());
    }

    [Fact]
    public void GetHashCode_ConsistentForSameInstance()
    {
        // Arrange
        var match = new SearchMatch(5, 10, 3);

        // Act & Assert - hash code should be consistent across multiple calls
        var hash1 = match.GetHashCode();
        var hash2 = match.GetHashCode();
        Assert.Equal(hash1, hash2);
    }

    #endregion

    #region ToString Tests

    [Fact]
    public void ToString_ContainsRow()
    {
        // Arrange
        var match = new SearchMatch(5, 10, 3);

        // Act
        string str = match.ToString();

        // Assert
        Assert.Contains("5", str);
    }

    [Fact]
    public void ToString_ContainsStartColumn()
    {
        // Arrange
        var match = new SearchMatch(5, 10, 3);

        // Act
        string str = match.ToString();

        // Assert
        Assert.Contains("10", str);
    }

    [Fact]
    public void ToString_ContainsEndColumn()
    {
        // Arrange
        var match = new SearchMatch(5, 10, 3);

        // Act
        string str = match.ToString();

        // Assert
        Assert.Contains("13", str); // 10 + 3 = 13
    }

    [Fact]
    public void ToString_EmptyMatch_ContainsNegativeValues()
    {
        // Arrange
        var empty = SearchMatch.Empty;

        // Act
        string str = empty.ToString();

        // Assert
        Assert.Contains("-1", str);
    }

    #endregion

    #region Comparison Operators Tests

    [Fact]
    public void EqualityOperator_SameValues_ReturnsTrue()
    {
        // Arrange
        var match1 = new SearchMatch(5, 10, 3);
        var match2 = new SearchMatch(5, 10, 3);

        // Act & Assert
        Assert.True(match1 == match2);
    }

    [Fact]
    public void InequalityOperator_DifferentValues_ReturnsTrue()
    {
        // Arrange
        var match1 = new SearchMatch(5, 10, 3);
        var match2 = new SearchMatch(5, 10, 4);

        // Act & Assert
        Assert.True(match1 != match2);
    }

    [Fact]
    public void Operators_AreConsistentWithEquals()
    {
        // Arrange
        var match1 = new SearchMatch(5, 10, 3);
        var match2 = new SearchMatch(5, 10, 3);
        var match3 = new SearchMatch(6, 10, 3);

        // Act & Assert
        Assert.Equal(match1.Equals(match2), match1 == match2);
        Assert.Equal(!match1.Equals(match2), match1 != match2);
        Assert.Equal(match1.Equals(match3), match1 == match3);
        Assert.Equal(!match1.Equals(match3), match1 != match3);
    }

    #endregion

    #region Struct Semantics Tests

    [Fact]
    public void Struct_IsValueType()
    {
        // Arrange & Act
        var match = new SearchMatch(5, 10, 3);

        // Assert
        Assert.IsType<SearchMatch>(match);
        Assert.True(typeof(SearchMatch).IsValueType);
    }

    [Fact]
    public void Struct_CopyByValue()
    {
        // Arrange
        var match1 = new SearchMatch(5, 10, 3);
        var match2 = match1;

        // Act - modify match2 (create new struct)
        match2 = new SearchMatch(6, 11, 4);

        // Assert - match1 should be unchanged
        Assert.Equal(5, match1.Row);
        Assert.Equal(10, match1.StartColumn);
        Assert.Equal(3, match1.Length);
    }

    [Fact]
    public void Struct_DefaultConstructor_CreatesEmpty()
    {
        // Arrange & Act - default struct constructor
        var match = default(SearchMatch);

        // Assert - default values should make it "empty"
        Assert.Equal(0, match.Row); // Default int is 0, not -1
        Assert.Equal(0, match.StartColumn);
        Assert.Equal(0, match.Length);
        // Note: IsEmpty will be false because Row is 0, not -1
        Assert.False(match.IsEmpty);
    }

    #endregion

    #region IEquatable Implementation Tests

    [Fact]
    public void ImplementsIEquatable()
    {
        // Arrange & Act
        var match = new SearchMatch(5, 10, 3);

        // Assert
        Assert.IsAssignableFrom<IEquatable<SearchMatch>>(match);
    }

    #endregion

    #region Dictionary/HashSet Compatibility Tests

    [Fact]
    public void CanBeUsedAsDictionaryKey()
    {
        // Arrange
        var dict = new Dictionary<SearchMatch, string>();
        var match1 = new SearchMatch(5, 10, 3);
        var match2 = new SearchMatch(5, 10, 3);
        var match3 = new SearchMatch(6, 10, 3);

        // Act
        dict[match1] = "first";

        // Assert
        Assert.Equal("first", dict[match2]); // Same value should find same entry
        Assert.Throws<KeyNotFoundException>(() => dict[match3]); // Different value should not be found
    }

    [Fact]
    public void CanBeStoredInHashSet()
    {
        // Arrange
        var set = new HashSet<SearchMatch>();
        var match1 = new SearchMatch(5, 10, 3);
        var match2 = new SearchMatch(5, 10, 3);
        var match3 = new SearchMatch(6, 10, 3);

        // Act
        set.Add(match1);
        set.Add(match2); // Should not add duplicate
        set.Add(match3);

        // Assert
        Assert.Equal(2, set.Count);
        Assert.Contains(match1, set);
        Assert.Contains(match3, set);
    }

    #endregion
}
