using Dotty.Rendering.Gpu;
using Dotty.Terminal.Adapter;
using SkiaSharp;
using Xunit;

namespace Dotty.App.SkiaTests;

/// <summary>
/// Regression guard for the W4-C3 decoration budget: each cell can emit its glyph
/// plus an underline sprite and a strikethrough/overline sprite, so the allocating
/// <see cref="QuadFrameBuilder.Build"/> overload budgets 3 instances per cell.
/// The perf baselines for the Silk warm/cold frame builds were recalibrated to this
/// 3x budget (CI run 41311937151); this test locks the budget in so future changes
/// to <see cref="QuadFrameBuilder.MaxInstancesPerCell"/> force a deliberate baseline update.
/// </summary>
public sealed class QuadFrameBuilderBudgetTests
{
    private const float TextSize = 14f;

    // 10x20 cell geometry: underline at row 17, strikethrough at 10, overline at 0.
    private static readonly CellSpriteMetrics Sprites =
        CellSpriteMetrics.Create(10, 20, 15, 17, 1, 10, 1, 0, 1);

    private static readonly TextAttributeOptions SpriteOptions =
        new(0.5f, BlinkVisible: true, Sprites: Sprites);

    [Fact]
    public void InstanceBudget_CoversGlyphPlusTwoDecorationSprites()
    {
        Assert.Equal(3, QuadFrameBuilder.MaxInstancesPerCell);
        Assert.Equal(24 * 80 * 3, QuadFrameBuilder.InstanceBudget(24, 80));
        Assert.Equal(40 * 120 * 3, QuadFrameBuilder.InstanceBudget(40, 120));
    }

    [Fact]
    public void Build_AllocatesFullInstanceBudget()
    {
        var buffer = new TerminalBuffer(rows: 2, columns: 4);
        buffer.SetCursor(0, 0);
        buffer.WriteText("abcd".AsSpan(), CellAttributes.Default);

        using var atlas = new GlyphAtlas(SKTypeface.Default, TextSize);
        var geo = new FrameGeometry(10f, 20f, 2, 4);

        var result = QuadFrameBuilder.Build(buffer, atlas, SKTypeface.Default, TextSize, geo);

        Assert.Equal(QuadFrameBuilder.InstanceBudget(2, 4), result.Instances.Length);
        Assert.True(result.InstanceCount <= result.Instances.Length);
    }

    [Fact]
    public void DecoratedCell_EmitsExtraInstancesWithinBudget()
    {
        var buffer = new TerminalBuffer(rows: 1, columns: 2);
        buffer.SetCursor(0, 0);
        buffer.WriteText("ab".AsSpan(), new CellAttributes
        {
            UnderlineStyle = UnderlineStyle.Single,
            Strikethrough = true,
        });

        using var atlas = new GlyphAtlas(SKTypeface.Default, TextSize);
        var geo = new FrameGeometry(10f, 20f, 1, 2);

        var plain = QuadFrameBuilder.Build(
            buffer, atlas, SKTypeface.Default, TextSize, geo,
            textOptions: TextAttributeOptions.Default);
        var decorated = QuadFrameBuilder.Build(
            buffer, atlas, SKTypeface.Default, TextSize, geo,
            textOptions: SpriteOptions);

        // Two glyphs plus decoration sprites; decorations share one sprite slot for
        // strike/overline, so 2 glyphs + up to 4 decoration instances.
        Assert.Equal(2, plain.InstanceCount);
        Assert.True(decorated.InstanceCount > plain.InstanceCount);
        Assert.True(decorated.InstanceCount <= QuadFrameBuilder.InstanceBudget(1, 2));
        Assert.Contains(decorated.Instances[..decorated.InstanceCount],
            inst => (inst.Flags & CellFlags.Decoration) != 0);
    }

    [Fact]
    public void PooledSpanOverload_StaysWithinBudget()
    {
        var buffer = new TerminalBuffer(rows: 1, columns: 4);
        buffer.SetCursor(0, 0);
        buffer.WriteText("abcd".AsSpan(), new CellAttributes
        {
            UnderlineStyle = UnderlineStyle.Single,
            Strikethrough = true,
        });

        using var atlas = new GlyphAtlas(SKTypeface.Default, TextSize);
        var destination = new CellInstance[QuadFrameBuilder.InstanceBudget(1, 4)];
        var dirtyRows = new System.Collections.Generic.HashSet<int>();

        int written = QuadFrameBuilder.Build(
            buffer, atlas, SKTypeface.Default, TextSize,
            destination.AsSpan(), dirtyRows, 1, 4,
            textOptions: SpriteOptions);

        Assert.True(written > 4);
        Assert.True(written <= destination.Length);
    }
}
